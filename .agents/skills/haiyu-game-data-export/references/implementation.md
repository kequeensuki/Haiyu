# 逐文件实施指南

本指南是检查顺序，不是要求每次都改所有文件。没有变更理由的共享层不改；确需新增通用能力时给共享层单独测试。先完成工作表中的协议表。

## 1. 选择来源与输出

写下：sourceId、实体键、全量入口、成功条件、首次/后续列表策略、依赖来源及 JSON 节点。

- 来源要能独立建立基线。角色拥有列表与声骸装备分别同步，所以是不同来源；多个消息更新同一列表，不意味着需要多个来源。
- 关联数据已在现有来源中时不要复制实体状态。存 instanceId 关系，Mapper 联结库存。
- 兼容旧捕获通常先 Optional；有成功业务事实触达后由公共管线变为 Required。失败 ACK 不应让一个未触达来源变为 Required。
- 未收到可选来源时 DTO 为 null、文件节点缺席；确认完整空列表后才输出空集合。
- 不用 Optional 掩盖不完整数据。某个来源一旦被要求完整，它不完整就保留上次完整快照。

## 2. Domains：先实现可独立测试的业务集合

在 `Domains/<Feature>/` 建立状态、变更类型和集合。实体类型命名用业务名，不用万能 Dictionary<string,object>。

下面是**仅演示领域层的完整 C# 形状**，Feature 是待替换业务名；没有任何虚构消息号或字段号。它演示“已证明的完整对象 upsert + 绝对等级修改”，不代表所有业务都适用：

```csharp
using Haiyu.ABI.Services.GameDataExport.Aggregation.Collections;
using Haiyu.ABI.Services.GameDataExport.Aggregation.Contracts;

namespace Haiyu.ABI.Services.GameDataExport.Domains.Feature;

internal sealed record FeatureState(int Id, int Level);
internal abstract record FeatureChange;
internal sealed record FeatureReplaced(FeatureState Value) : FeatureChange;
internal sealed record FeatureLevelChanged(int Level) : FeatureChange;

internal sealed class FeatureCollection(IEnumerable<FeatureState> baseline)
    : DataCollectionBase<int, FeatureState, FeatureChange>(
        baseline, value => value.Id, id => id > 0, Comparer<int>.Default)
{
    // 只有证据说明“新增是完整对象刷新”时才覆盖这个默认行为。
    public override ChangeResult Add(FeatureState entity) => Replace(entity.Id, entity);

    public override ChangeResult Update(int key, FeatureChange change) =>
        change is FeatureReplaced full ? Replace(key, full.Value) : base.Update(key, change);

    protected override FeatureState Merge(FeatureState current, FeatureChange change) => change switch
    {
        FeatureLevelChanged level => current with { Level = level.Level },
        _ => throw new InvalidDataException("不支持的业务变更。")
    };
}
```

实施时检查：

- ID 类型由协议决定。邮件 ID 不转整数；成就组/成就条目放入不同键空间；实例 ID 与物品配置 ID 分开。
- 列表或字典要复制、冻结，并覆盖结构相等判断；不要使用 `record ==` 假定比较列表内容。
- 业务变化不应保存 ProtocolMessage 或捕获它的 Func 交给集合执行。用 `LevelChanged`、`PropertiesReplaced` 等明确类型。
- 复杂操作如换装、跨角色转移、拆合堆要批内原子。先验证身份及约束，再改工作字典；跨对象影响也要在事务内。
- 若实体需完整确认：参照 RoleCollection 的 `IsInitialized` / `IsComplete`。占位只能表示“待确认”，不能凭部分字段成为已拥有实体；完整对象到达时按证据决定覆盖还是合并。
- 不在业务集合里保存来源同步版本、sessionId、上次输出文件或异步任务。

## 3. Adapter：只做协议转换

继承 `CollectionMessageAdapter<TKey,TEntity,TChange>`。构造器传 sourceId、集合工厂和 retainedBytes 估算。真正的编号/字段必须来自证据表。

| 当前 helper | 使用条件 | 容易犯的错误 |
|---|---|---|
| `Full(id, listField, read)` / `Full(id, readMany)` | 经证明的完整来源，没有错误码需过滤 | 对任何叫 List/Response 的消息都这样注册 |
| `FullChecked(id, errorField, readMany)` | 有整体错误码的完整响应 | 失败响应误清空集合 |
| `InitialFullThenMerge(...)` | 首次列表可作基线，后续按完整实体合并 | 将后续部分列表当作全量删除或硬失同步恢复 |
| `On(id, errorField, apply, when: ...)` | 有序增量；可选对象条件应在进入来源增量前判断 | 在 delta 中才判断合法空 ACK，导致无基线时误报错误 |
| `Ignore(id, errorField)` | 已证明只确认、不提供新事实的 ACK | 忽略真正携带目标和新状态的成功结果 |
| `Invalidate(id, errorField)` | 成功变更确实无法靠下行上下文安全解释 | 为图方便过度失同步，或反过来猜一个目标 |
| `Each(...)` | 一条消息含多个业务操作 | 先成功的操作在后续失败时没有回滚 |

读取字段时：

- 匹配 Int32/UInt32/Int64/String/Bool/Message/Messages/Int32s/Strings/map 的真实类型。
- uint32 完成时间不要压成 signed int；int64 不经 double 中转；64 位 JSON 字符串按协议还原。
- 错误码字段号与列表字段号逐条核对，不从其他 Response 抄。若客户端在非零整体错误码下仍处理成功子集，不能直接套用 helper 的整体错误码过滤；用最小专用 handler 保留分项语义，并测试混合结果。
- 可选对象为空是否合法，查客户端回调；必须存在的对象缺席才报非法载荷。
- repeated 键值列表的唯一性规则按客户端执行；角色属性后值覆盖，而技能节点可能要求唯一。
- 状态从返回值整体替换，不用 OR 累积，不丢零值。count=0、level=0 是否意味着删除必须逐业务和逐消息证明，不能统一猜测。
- 自定义 handler 时，将可能失败的业务解析和验证留在现有 Full/Apply/Delta 受保护路径；Read 阶段不要提前修改实体或在事务外提交部分状态。
- retainedBytes 应考虑字符串字符、嵌套对象和列表，不能把很大的对象一律估成几十字节。

## 4. 注册组合根

修改 `Adapters/PlayerDataMessageRegistry.CreateDefault`：

1. 实例化适配器，避免不必要的静态全局业务缓存。
2. 将新来源加入正确的 OptionalSources 策略。
3. Sources 由 handler 声明得到；空适配器不会“自动工作”。
4. 一条消息可由不同来源各消费一次，如 PhantomItemResponse 更新库存和装备；重复注册同一消息的同一来源应拒绝。
5. 共享消息每个消费者都要保留正确错误码过滤；失败消息不能让其中一个消费者更新。
6. 检查 `ItemChangeMessages` 是否已通过注册表自动涵盖新消息。额外纯事件才显式补充，不复制一套并行编号表。

不要在 InventoryExportModule.Consume 加“if 是新业务消息就另行解析”的中央分支。

## 5. ABI 与 JSON：最容易漏的四个接点

位置在 `src/Extensions/ABI/ABI.Models/`：

- 建立 `[MemoryPackable] partial record` DTO，显式 `[JsonPropertyName]`，确认字段名和数值单位。
- 在 `GameDataExportResult` 尾部追加有缺省值的可空字段，不重排既有成员。不要据此承诺任意旧 MemoryPack 端点都兼容。
- `ABIJsonContext.cs` 注册 DTO 及实际需要直接序列化的集合类型。
- `ABIMemoryPack.cs` 注册相关类型。只在普通 .NET 测试中过序列化还不够。

必须部署配套的主程序与 ABI。未知状态建议保留原始数值并增加派生状态，而不是不可逆压缩为一个 bool。

## 6. 映射及实际文件

在 `Export/PlayerDataSnapshotMapper.cs` 增加显式业务映射方法：

- 缺席来源返回 null；合法空集合返回 DTO 空集合。
- 从冻结的候选快照取值，不从正在更新的解析器、全局缓存或“当前账号”对象取值。
- 关系联结用同一候选中的来源；找不到目标则明确 unresolved/missing，不造空物品。
- 同一实例转移后旧关系应清空；保留槽位空洞和主槽位语义。
- 原始属性与派生属性含义写清，不重复叠加同一贡献。

在 `Export/InventoryJsonWriter.cs` 显式编码新节点，使用源生成 JsonTypeInfo；沿用现有根格式和固定路径。注意某些章节有 count/items 包装，ABI DTO 形状与落盘包装不一定完全相同，测试两者。

在 `Modules/InventoryExportModule.cs` 检查：

1. Checkpoint 构造 GameDataExportResult 时带上新映射。
2. ProjectResult 返回最后成功投影时带上新 DTO。
3. 如需计数，在成功回执后更新；未收到新登录来源时移除旧可选计数，不能显示上个会话的数据。
4. Artifact 与来源进度仍按原管线记录，不自己写文件或抢占输出路径。
5. 必要时复用 complete 谓词，软等待允许后续有序变更，不清除硬失同步。

## 7. 语言、文档与测试入口

- 更新 zh-Hans、zh-Hant、en-US、ja-JP 的来源、计数或状态键；解析 JSON 并检查重复 key。
- 在 `Dev/Development/GameDataExport-<业务>-聚合与验收.md` 说明消息证据、字段语义、来源条件、JSON 形状、已测和未测范围。
- 在 TransportTests 新建 partial Program 测试文件，同时在 Program.cs 的测试表和 `--game-data-export` 筛选器注册。忘记任一处，新测试可能根本没运行。
- 更新 ProtocolSmoke 实际加密帧、预期事件数量和最终 DTO/文件断言；不要只改预期数量来掩盖丢消息。

## 8. 合并前逐文件对照

| 你声称的结果 | 在哪里证明 |
|---|---|
| 新类型真正被消费 | 注册表有 handler，真实消息或 wire 测试触发它 |
| 增删改语义正确 | 领域边界测试 + 协议取证表 |
| 没有串号/重登污染 | Coordinator/Session 完整链路与旧文件断言 |
| 新 JSON 节点可用 | 打开实际 game-data.json 检查，不只看内存 DTO |
| UI 收到数据 | 顶层 ProjectResult 与 ABI 序列化往返 |
| 原生版本可运行 | 执行 publish 目录里的原生可执行文件 |
| 文件名与清理正确 | 成功写入后旧文件迁移测试，失败保留旧文件 |
| 样本验证覆盖了某操作 | 真实事件中确实出现该消息，且结果独立核对 |
