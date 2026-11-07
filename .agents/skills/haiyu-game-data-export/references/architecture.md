# 架构、状态所有权与不可破坏的不变量

## 1. 从捕获到文件的完整链路

```text
Capture / Rebuild / Decrypt / Session
  → ProtocolJsonDecoder → ProtocolMessage（冻结字段树）
  → ICollectionMessageAdapter / CollectionMessageHandler
  → 类型化业务变更 → DataCollectionBase.ApplyBatch
  → CollectionSnapshot（包含冻结的 IsComplete）
  → SourceState（单会话单来源）
  → AggregateState（跨来源完成条件与版本向量）
  → PlayerDataSnapshotMapper → ABI DTO → InventoryJsonWriter.Encode
  → 共享 Output / 原子写入 / 回执
  → Acknowledge 后更新计数、最后成功投影并清理同玩家旧文件
```

旁路 `server-messages` 保存全部服务端事件，`item-changes` 保存筛选事件。它们完成不代表玩家数据快照完成。消费模块的 `inventory` ID 是历史稳定标识，显示名已扩展为玩家数据快照。

## 2. 先阅读的文件

以下路径以 `src/Extensions/Haiyu.ABI/Services/GameDataExport/` 为前缀：

| 文件/目录 | 你要理解的内容 |
|---|---|
| `Aggregation/Contracts/IDataCollection.cs`、`ChangeResult.cs` | 增删改接口；Changed/Unchanged/MissingTarget/Invalid 等结果 |
| `Aggregation/Collections/DataCollectionBase.cs` | 默认行为、Replace、Equivalent、ApplyBatch、IsComplete |
| `Aggregation/Collections/CollectionSnapshot.cs` | 冻结对象列表、计数、保留字节估算和完成标记 |
| `Adapters/CollectionMessageAdapter.cs` | 协议注册、错误码过滤、全量/增量转换、事务冻结 |
| `Adapters/PlayerDataMessageRegistry.cs` | 组合根、来源列表、可选来源、共享消息分派 |
| `Aggregation/SourceState.cs` | 基线、顺序、硬失同步、完整恢复及来源进度 |
| `Aggregation/AggregateState.cs` | 必需来源、完成谓词、候选快照和持久化回执 |
| `Modules/InventoryExportModule.cs` | session 隔离、身份绑定、候选提交和最后成功结果 |
| `Export/PlayerDataSnapshotMapper.cs` | 业务对象到 ABI DTO 的显式映射、关联查询 |
| `Export/InventoryJsonWriter.cs` | GameDataExportPaths、实际文件格式、固定文件名与旧文件清理 |
| `Modules/ItemChangeMessages.cs` | 筛选事件集合复用注册表，避免漏收已聚合消息 |

再按任务阅读一个最接近的业务示例：简单键值集合看 NormalItems；完整对象 upsert 看 Mail；复合键及状态看 Achievements；软初始化和关系联结看 Roles；跨对象移动看 Weapons/Phantoms。

## 3. 每层只能管自己的事

| 层 | 应该放什么 | 不应该放什么 |
|---|---|---|
| Domains | 业务状态、变更类型、合并、删除、关系约束、结构相等 | 消息编号、protobuf 字段、UI、文件写入、独立全局版本 |
| Adapters | 消息/字段号、错误码、缺失对象条件、协议到业务变更 | 自己的玩家缓存、业务文件线程、推测的属性公式 |
| SourceState | 来源基线、顺序、硬失同步与恢复 | 特定业务字段、角色专用判断 |
| AggregateState | 完成条件、候选版本与回执 | 具体消息分支、某个业务的 ID 列表 |
| Mapper | 显式 DTO 映射和库存联结 | 改动业务集合、伪造缺失目标、解析协议 |
| Module | 生命周期、隔离、提交、统计 | 每种消息的 switch、另一套字段合并实现 |
| Storage | 原子性、队列、输出归属和回执 | 决定领取状态或对象归属 |

发现“不改 CaptureConnectionState 就加不进这类数据”的想法时，先检查是否选错扩展层。纯业务接入通常不需要改 KCP、解密和登录识别。

## 4. 默认增删改不是通用业务真理

`DataCollectionBase<TKey,TEntity,TChange>` 当前约束 `TKey : notnull`、`TEntity : class`。核对当前版本后使用：

| 方法/属性 | 默认效果 | 常见重写条件 |
|---|---|---|
| `Add(entity)` | 校验键；已有键返回 Unchanged | 新增通知实际上刷新完整对象，如邮件、武器 |
| `Update(key, change)` | 无目标返回 MissingTarget；否则 Merge 后 Replace | 有证据的 upsert、懒创建、实体初始化先行 |
| `Remove(key)` | 删除不存在的键也是 Unchanged | 同时清理其他关系或子对象 |
| `Replace(key, value)` | 校验身份；允许新增；按 Equivalent 判断是否变化 | 不是“有目标才允许修改”，不要误读为普通 Update |
| `Merge(current, change)` | 由业务实现 | 必须用显式变更类型，不接收 ProtocolMessage 委托来偷偷解析协议 |
| `Equivalent(a,b)` | 默认记录/类型的相等性 | 嵌套列表、字典必须按内容判断，不能只比容器引用 |
| `IsComplete` | 默认 true | 有尚未收到权威完整对象的初始化条目 |
| `Snapshot()` | 冻结列表，可显式排序 | 键仅需相等性；排序时明确提供 comparer |
| `ApplyBatch(...)` | 任一失败/异常回滚整个工作字典；比较最终结构值 | 只回滚字典不等于能回滚被原地修改的嵌套对象 |

“写 record”不自动使 `List<T>`、`Dictionary<K,V>` 不可变。复制并冻结子集合；新变更创建新对象；不能清空旧对象的列表再返回同一引用。

## 5. 四种状态必须分开

| 情况 | 当前典型表现 | 能否输出完整快照 | 恢复方式 |
|---|---|---|---|
| 尚未见过来源 | Missing；可选来源可省略节点 | 取决于来源是否必需 | 等基线，不能伪造空数据 |
| 已确认合法空列表 | Ready，列表为空 | 可以 | 后续正常更新 |
| 有基线，某实体尚待完整初始化 | AwaitingEntities；SourceState 的基线仍可接入有序变更 | 暂停包含该必需来源的新投影 | 收到该完整实体，或重新完整同步 |
| 无基线、非法字段、不可解释的变化 | AwaitingBaseline / Invalid 等 | 保留上次成功文件 | 经证明的完整基线；单个普通对象不能重置整个来源错误 |

当前 `AggregateState` 可接受 `complete` 谓词；Inventory 模块传入 `snapshot.IsComplete`。来源的基线可用性与业务实体完成性不同：软等待时不能令 SourceState 拒绝所有后续有序事件，否则后到的 Active 永远无法补齐。

角色当前做法是建立 `IsInitialized=false` 的内部待初始化标识。它不代表已拥有角色，不可出现在成功投影中。完整角色到达后用权威对象替换初始化片段，不能把此前片段再叠加一次。真正无效的键仍硬拒绝。

不要把这条角色规则推广到所有业务：成就可按客户端懒创建；试用/机器人属性查询不建立账号拥有关系；缺失邮件修改目标仍需要基线恢复。

## 6. 来源、玩家和版本

- `sourceId` 代表一个可以独立确认基线的来源，不一定等于一种 DTO 或一个消息。
- 状态按 `sessionId` 保存，而不是只按 playerId；同账号重登也必须重新收集。
- 一个玩家的多个登录代次竞争同一路径，由既有 Claim/回执隔离，旧回执不能抢回新代次的文件。
- 本地 EventIndex 仅提供已观察的串行顺序，不是服务器版本，也不能证明网络未漏包。
- `LatestComplete` 不保证不同消息属于同一个服务器事务。没有 batch/version 证据时不要宣称 StrictBatch。
- 不新建全局 PlayerDataCache、第二份背包、独立文件任务或自行递增的“服务器版本”。
- 目前有来源条目上限、会话上限及聚合保留字节预算。新增字符串和嵌套列表要提供保守 retainedBytes 估算；它不是精确工作集。

## 7. 输出与兼容

- 固定路径为 `<根目录>/<playerId>/game-data.json`。其他事件及捕获索引仍在 captures/modules 路径。
- 先成功写新文件，再清理同玩家匹配旧格式的时间戳文件；失败时保留旧文件，不清理未登录账号的数据。
- 新增来源通常先 Optional，在真实业务消息触达后由现有管线转 Required；首次基线缺席不等于已知空集合。
- 全部可选来源也必须有明确策略；不能为了通过 Ready 强行永远 Optional。
- ABI DTO、JSON 源生成、MemoryPack 注册和顶层投影都要同步。固定 JSON 的既有字段与模块 ID 默认兼容保留。
- 库存物品与角色装备是“权威物品 + 关系”，导出时联结；提升声骸等级后角色详情应自动变化，不维护第二份独立声骸状态。
