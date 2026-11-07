# 游戏数据导出结构

消息链路：捕获/重组/解密 → 协议解码 → 消息适配器 → 业务集合 → 来源聚合 → 快照映射 → 共享存储。

## 职责和状态归属

| 目录 | 职责 |
|---|---|
| `Capture` / `Rebuild` / `Decrypt` / `Session` | 捕获、传输恢复、密码和登录/玩家身份；不解释库存字段 |
| `Protocol` | 只解码冻结的协议字段树；事件导出继续使用完整协议结果和原载荷 |
| `Adapters/Inventory` / `Adapters/Mail` / `Adapters/Achievements` | 消息编号、字段编号、错误码、载荷形状及跨来源实例路由；转换为类型化变更 |
| `Domains/Inventory` / `Domains/Mail` / `Domains/Achievements` | 无协议/ABI 依赖的不可变状态、业务变更和集合；武器装备、声骸养成和拆合堆规则 |
| `Aggregation/Contracts` | `IDataCollection` 增删改契约与 `ChangeResult`，区分变化、无变化、目标缺失和非法操作 |
| `Aggregation/Collections` | 可覆盖的默认增删改、冻结快照、共享批事务；只持有事务工作集合，不持有来源同步或落盘版本 |
| `Aggregation/SourceState.cs` | 每个会话/来源的基线、接收顺序、来源版本、失同步和恢复的唯一管理者 |
| `Aggregation/AggregateState.cs` | 跨来源完成条件、候选快照与持久化回执版本的唯一管理者 |
| `Modules` | 模块生命周期、登录隔离、消息调度及快照提交；不按背包消息类型分支 |
| `Export` | `PlayerDataSnapshotMapper` 将业务快照转为稳定 ABI DTO；`InventoryJsonWriter` 编码；旧 `InventoryMapper` 保留给旧纯快照链路 |
| `Storage` / `Events` | 原子写入、受限队列、回执、元数据及逐条事件流 |

`InventoryMessageProjection` 已删除，其中央操作枚举和分支不再保留第二套实现。`PlayerDataMessageRegistry` 是适配器注册组合根，`ItemChangeMessages` 复用已注册编号，避免新增聚合消息却漏记事件。

## 默认行为与覆盖

`IDataCollection<TKey, TEntity, TChange>` 由管理同类对象的集合实现。`DataCollectionBase` 默认新增按键去重、修改缺失目标返回 `MissingTarget`、删除不存在的对象为 `Unchanged`；派生集合只实现自己的 `Merge`，或覆盖必要的增删改方法。

- 普通道具：只更新绝对数量，保留过期信息。
- 限时道具：按 `(道具ID, 实例ID)` 定位，更新数量和过期时间。
- 武器：新增刷新完整对象；等级/经验、突破、谐振、装备与标记使用不同变更类型。换装同时维护旧归属和新归属，忽略皮肤槽位。
- 声骸：区分库存更新和养成更新，保留数量/获得时间的既有规则；拆合堆由集合执行。一次复杂操作自身具有原子性，多操作消息还由共享批事务整体保护。

默认集合只要求键支持相等性，不隐式要求 `IComparable`；需要稳定排序的业务集合显式传入比较器，现有四类背包继续按原有键排序。

对象和子集合必须不可变。协议适配器将集合冻结为只读数组，快照不与可变解析器共享内容。`ApplyBatch` 遇到失败结果或异常时恢复整个工作集合；依据最终结构值判断变化，重复通知不制造新落盘版本。

完整来源保存的是类型化业务快照，未知协议字段仍由可选的消息事件模块记录；业务集合不保留不使用的协议字段树。32 MiB 聚合保留预算、来源数量上限及全量/增量防护继续有效；保留字节数改为对实际业务对象和属性列表的保守估算，不代表精确进程工作集。

## 新增或修改一类数据

1. 在 `Domains/<业务域>/<类型>` 定义不可变状态、明确的变更类型及集合；优先复用默认行为。
2. 在对应 `Adapters` 中声明消息编号、成功条件和字段转换，避免在集合中出现协议字段编号。
3. 在组合根注册适配器；同一编号重复注册会失败。为新增快照来源在输出映射中明确增加 DTO 字段，而非隐式反射序列化。
4. 保留 `SourceState` / `AggregateState` 的同步和版本所有权，不在新集合中重复维护。
5. 添加领域边界测试和实际消息到文件的链路测试。未知目标必须等待全量基线，不能靠猜测补造对象。

换皮肤和批量标记的成功下行响应仍缺少目标及新值，维持 `AwaitingBaseline`，完整响应才恢复；没有新增客户端请求捕获或主动请求。目录、JSON 字段、MemoryPack ABI 和 JSONL 事件格式保持兼容。

## 验证

`ABIRuntime.TransportTests --game-data-export` 覆盖共享集合契约、默认/覆盖行为、独立新类型注册、失败与异常回滚、快照冻结，以及既有增删改/重登/切网/持久化链路。新增规则应同时验证来源失同步状态和旧文件保留，不能仅比较最终内存值。


## 邮件来源

公共适配器位于 `Adapters/CollectionMessageAdapter`，公共冻结快照位于 `Aggregation/Collections/CollectionSnapshot`，两者不再限于背包。`MailCollection` 复用默认删除，覆盖新增（按 ID 替换完整对象）、完整对象同步和状态变更。

邮件自动并入同一玩家最新文件的 `mails` 节点。未收到首次列表时不伪造空邮箱；来源一旦收到邮件变更即变为必需，缺失目标会阻止新快照并保留旧文件。首次列表建立基线，后续列表逐封合并，不能用列表缺席删除邮件。具体协议依据、状态位和同步边界见 `Dev/Development/GameDataExport-邮件聚合与验收.md`。


## 成就来源

`AchievementCollection` 将成就、成就组、服务器汇总分别放入独立键空间。完成时间和 isReceive 独立保存，领奖规则由领域状态解释，进度满不自动当成领奖。初始 InfoResponse 建基线，部分更新合并，确认响应不猜测领奖；新条目可按客户端懒创建语义新增。`achievements` 与邮件一样自动并入玩家最新文件，未同步时不伪造空数据，重登后重新收集。协议依据及字段见 `Dev/Development/GameDataExport-成就聚合与验收.md`。

角色导出采用 `Domains/Roles` / `Adapters/Roles`，账号角色与声骸装备独立建立基线。导出映射按声骸实例 ID 联结现有背包，确保装备详情跟随升级、调谐和换装。`roles.items` 包含技能实际等级、技能节点、服务器属性与五槽声骸；属性保持协议整数单位。详细协议依据、缺失数据状态及验收见 `Dev/Development/GameDataExport-角色聚合与验收.md`。同一下行消息可分派给不同来源，重复注册同一来源仍会拒绝。
