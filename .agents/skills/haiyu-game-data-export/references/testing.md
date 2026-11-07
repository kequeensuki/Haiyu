# 测试、真实回放与交付判定

## 1. 测试要能推翻错误假设

不要写只重复实现逻辑的测试。例如“实现把进度满当作完成，测试也把进度满当作完成”不会提供证据。预期应来自客户端规则、独立 wire 向量或真实操作结果。

| 层次 | 要证明什么 | 典型断言 |
|---|---|---|
| 领域集合 | 合并/删除/约束/不可变 | 原快照不变；失败批次整体回滚；重复同值无新变化 |
| 适配器/解码 | 编号、字段号、错误码、默认值 | 载荷经过 decoder；失败响应不更新；0/false 不被丢弃 |
| 完整链路 | 来源、隔离、候选与文件 | 真正文件存在且字段正确；不完整时原文件字节不变 |
| ABI | DTO 在两端可传输 | JSON 与 MemoryPack 往返包含新字段及边界数值 |
| NativeAOT | 编译后行为与托管一致 | 原生程序退出 0 并通过真实断言 |
| 真实回放 | 载荷形态与到达顺序符合现场 | 同一输入修复前复现、修复后恢复，独立核对字段 |

## 2. 最小验收矩阵

将下表实例化成**本次业务的数据**；不适用项要写理由，不能默默省略。

### 基线与列表

- 未收到来源：可选节点缺席，不能显示 count=0 冒充已同步。
- 收到完整空列表：节点出现且为合法空，不保留上个登录的条目。
- 完整列表覆盖旧集合；首次 Notify 后的部分列表按证据合并，不凭缺席删除。
- 分页要测中间页、重复页、乱序页、缺页、超时、批次冲突；没有页证据则不自造分页协议。
- 非法基线不覆盖已有成功文件。

### 增删改与数值

- 新增 → 修改 → 删除 → 重新新增；重新新增不复活旧状态。
- 完整对象 upsert 与默认 Add 去重分别有证据；同 ID 的服务器修订不会被吞掉。
- 相同内容重复通知无额外落盘版本；变更内容即使 ID 相同仍应更新。
- 相同属性键的相同值、不同值、后值为零；如果语义是字典赋值，验证后值覆盖。
- 节点/实体身份冲突、无效 ID、负数或范围异常仍按各业务规则处理，不全局放宽。
- false/0/空列表能清除旧值；patch 缺席与全量空列表不混淆。
- uint32 上限、int64 大于 2^53、字符串 ID 含前导零等边界保持原值。

### 响应与状态

- 失败响应不改状态、计数和文件。
- 有成功子集时只更新成功项；一条变更批次内部失败时整体回滚，不提交一半状态。
- 合法空 ACK 在**无来源基线时**也不能误创建 Required 来源。
- 无目标/新值的成功响应不能猜目标；区分 ACK 与随后真正的通知。
- 多个 bool/位字段独立；完整状态替换能撤销旧标记，不永久 OR。
- 成就未完成、完成未领、已领、组奖励与条目奖励分别测；进度满不等于完成。

### 初始化、缺失与恢复

- 有列表基线，主实体尚未出现：属性 → 节点 → 技能 → 完整实体，包含一次中途 Checkpoint。
- 软等待期间旧文件字节不变；Source 仍能接收补齐消息；完整实体到达后恢复。
- 完整实体永远缺席时，停止不能报告 Completed 或输出假对象。
- 完整对象与先行片段冲突时，按客户端规则确定权威，不能重复重放初始化片段。
- 整个来源没有基线时的 delta 仍不能代替基线。
- 真正非法字段造成的硬失同步不能被单个 Active 或部分 Notify 清除；正确全量入口能恢复。

### 关系与账号

- 关系先于物品或角色到达、目标暂缺、卸下空槽、跨角色转移、同实例不能重复装备。
- 角色详情联结的是当前候选库存；库存升级/调谐后详情同步变化。
- A 登录 → B 登录 → A 新会话；不混用 playerId 与 sessionId。
- 同账号退出重进，明确 Logout 与仅新 LoginResponse 两种路径；复用密钥/连接。
- 新全量为空时清空旧数据；必需来源未齐时不覆盖旧文件。
- 旧会话迟到消息/回执和未验证连接不能修改新会话结果。

### 文件与 ABI

- 真实固定路径，不额外生成业务时间戳文件。
- 新文件写入失败保留旧文件；成功后只清理该账号匹配格式的旧快照，不动其他文件/账号。
- 候选 V 写入期间收到 V+1，不被旧回执清除；没有共享可变引用污染旧候选。
- 实际 JSON 节点与 count/items 包装、空/缺席行为正确。
- MemoryPack/JSON 往返包含新 DTO、空槽 null 与非空嵌套对象。

## 3. 现有测试基础设施

阅读当前测试文件，确认 API 后复用：

- `src/Tests/ABIRuntime.TransportTests/InventoryNotificationTests.cs` 的 NotificationFixture：真实协议解析与 coordinator，不是仅直接调 Merge。
- `RoleRealTrafficRegressionTests.cs`：匿名重复字段、初始化先行、完成谓词与硬失同步保护。
- `SameAccountReloginTests.cs`：实际 CaptureConnectionState、同账号新登录代次。
- `ExportModuleTests.cs`、`ExportReviewRegressionTests.cs`：候选/回执/隔离/存储失败。
- `FullProtocolTests.cs`：独立手写 wire 向量，不是所有测试都用同一生成器同时编码和“证明”字段号。
- `src/Tests/GameDataExport.ProtocolSmoke/Program.cs`：原生、加密帧、协议、聚合、落盘、MemoryPack 全链路。

注意 Fixture.Flush 只是检查点和输出刷新，不等于服务完成；不要假定它返回的顶层 Success 已是最终收尾结果。实时用文件、业务 DTO 和来源状态断言；最终 Success/Completed 在 Finish/Complete 链路检查。

新增方法必须进入 Program 测试表与导出筛选器。检查输出中 `RUN`/`PASS` 的本次完整名称和最终 `ALL TESTS PASSED`，测试数随任务变化，不固定为历史的 51/54。

## 4. 可直接执行的验证

先确认 SDK 及依赖，缺少工具链时记录具体限制，不输出“全部通过”。自带脚本优先：

```text
python .agents/skills/haiyu-game-data-export/scripts/run_export_checks.py --native --expected-test "本次新增测试完整名称" --artifacts-dir <仓库外日志父目录>
```

SDK 不在 PATH 时加 `--dotnet <dotnet或dotnet.exe完整路径>`。每个本次新增测试都重复传一次 `--expected-test`，不要只挑一个容易通过的名称。`--no-restore` 只在已恢复依赖时使用。脚本顺序构建，检查退出码与测试标记，执行实际原生程序，校验运行期间的源码输入指纹，并保留 summary.json 与各阶段日志。

手工命令等价形状，在仓库根执行：

```text
dotnet build src/Tests/ABIRuntime.TransportTests -c Release -m:1 -p:UseSharedCompilation=false
dotnet src/Tests/ABIRuntime.TransportTests/bin/Release/net10.0/ABIRuntime.TransportTests.dll --game-data-export
dotnet publish src/Tests/GameDataExport.ProtocolSmoke -c Release -r <当前宿主RID> -m:1 -p:UseSharedCompilation=false -o <仓库外原生目录>
<原生目录>/GameDataExport.ProtocolSmoke
```

先读取烟测项目的 `<PublishAot>true</PublishAot>`，或用 `dotnet msbuild <烟测csproj> -getProperty:PublishAot -p:Configuration=Release -p:RuntimeIdentifier=<RID>` 核对有效值。不要全局传 `-p:PublishAot=true`：全局属性会传播到 netstandard 分析器项目，可能造成 NETSDK1207；本仓库烟测项目自身已经配置 AOT。

Windows 执行 `.exe`；Linux 无后缀。不要在 Linux 中把 win-x64 的发布成功当作 Windows 原生执行成功。不要使用 `dotnet ...ProtocolSmoke.dll` 代替最后一条，即使控制台也打印了 NativeAOT 字样。

大型协议生成文件编译耗时较长；通常单构建并设 `DOTNET_PROCESSOR_COUNT=2` 可控制资源。不要同时跑多个 C# 构建，不在构建期间改源码，否则可能得到过期 DLL 的假阳性。命令执行工具应及时让出控制并向用户报告有意义的进度。

沙箱命名 mutex、缓存权限等失败，应按当前环境的授权工具处理；不要把权限报错改成测试跳过，不自动覆盖 HOME、全局 git 配置或 SDK。安装依赖和网络访问遵循当前会话授权，脚本不代替授权。

## 5. 真实捕获分析和回放

### 5.1 先检查什么

使用 `capture_audit.py` 对目录或 zip 只读分析；输出放在输入目录与代码仓库之外。先检查：

- capture-index 各模块状态、updates、sources、artifacts，尤其是 Ready / AwaitingBaseline / AwaitingEntities；多次捕获按 captureId 分开，不把历史捕获的状态当作本次状态。
- manifest 的 received/written/decoded/failed/unknown/dropped、文件列表与 sessionBindings。
- 不同 playerId/sessionId 的原始事件数与筛选事件数；captureEventIndex 的重号、缺号和匹配。
- snapshot 的内容时间和路径；历史文件存在不代表本次仍在使用旧命名。
- 协议 sourceCommit 是协议来源提交，不是应用构建 SHA。

`unresolved-*.jsonl` 非空不自动是 bug。BasicInfo 到达前事件可以物理留在那里，之后由 manifest.sessionBindings 解析所属账号；不可只看文件名就认定串号或未解析。

### 5.2 准确还原消息

JSONL 是解码事件，不是原始 KCP 流量。优先使用已有协议 schema/descriptor，按字段号和实际类型还原后重新走 decoder/coordinator。不能把从 JSON 搭出的简化对象直接交给 Mapper，当作完整协议链路回放。

捕获时间带时区偏移时，按用户时区解释；服务器业务时间与 receivedAt、文件日期、回放开始时间分开。不要把 UTC 直接当作用户本地时间。

当前事件字段名、枚举原名与重建的生成 protobuf JsonParser 可能存在大小写或枚举前缀差异。还原工具应显式按 descriptor/元数据映射名称并还原枚举数值，记录转换；**不得在还原阶段去重业务字段、删掉失败事件或修正异常值**，否则修复前也可能假通过。

按 captureId 和 captureEventIndex 排序；保留 session/身份及接收顺序。手工重建的 EventIndex、时间、CapturedFrames 等元数据若不是原始值，必须标为回放数据；JSONL 通常不能恢复握手密钥，不编造 sessionKeyHex。

### 5.3 对照和独立预期

1. 固定原始输入，只读留存。记录修复前的错误与第一处失败，不只看最终被覆盖后的错误码。
2. 若多种缺陷叠加，用明确标记的对照实验隔离：例如仅规范化重复属性的实验不是“原始输入回放”，只能用于证明另一个初始化顺序问题独立存在。
3. 修复后再次使用**未经业务修正**的同一输入。
4. 独立归约或人工核对完整字段：对象 ID 集合、计数/等级、状态、时间、技能、属性、关系和删除结果。仅数量相同不足以通过。
5. 把原有历史文件复制到单独输出目录验证迁移，不在唯一原始数据上做清理实验。
6. 不把原始账号、正文、密钥或压缩包加入 Git；正式回归用匿名结构向量保留故障形状。

### 5.4 结论边界

- “7554 条已记录消息全部解析成功”不等于“网络从未丢包”。
- “事件模块 Completed”不等于“玩家快照 Completed”。
- “真实样本只有成就全量”不能报告“成就领奖链路实测通过”。
- “Linux NativeAOT 通过”不等于“Windows Npcap/管理员服务/真实目录权限均已实测”。
- “离线重建快照成功”不等于“原客户端那次导出成功”。重建文件需另存并标注元数据来源。

## 6. 最终验收报告格式

报告范围、协议依据、修改文件、实际执行命令及退出码、被执行的新测试名称、真实回放输入/结果、独立核对方法、未覆盖操作与实机限制。若推送已获授权，再记录提交与远端 SHA；不将将来的验证计划写成结果。

## 7. 防止“测试通过，功能未接通”

测试分三类分别报告：直接测领域算法；带指定参数构造模块；使用生产默认工厂和真实输入来源。前两类不能代替第三类。生产工厂不允许自动注入测试专用凭证；通过真实消息/请求入口填入匿名参数，然后断言目标请求被发出且结果在实际文件、artifacts、顶层状态中一致。

外部 HTTP 与历史功能按 [专项矩阵](external-history.md) 验收。至少保留一个实际 endpoint，由假 HttpMessageHandler 返回固定响应；这样才能暴露发送前的序列化失败。原生程序须明确输出本次业务断言后的 PASS 标记，报告中列出对应断言与日志。当前 run_export_checks.py 仅检查通用原生标记，**不能自动证明新业务原生路径执行过**；人工核对具体测试源码与原生日志，未执行则记未验证。

有条件时做反证：去掉参数来源、让所有池失败、把历史 items 改成字符串、切换语言。若测试仍绿，先补能识别该故障的断言。不要为了测试通过而让生产代码接受损坏文件或把 Failed 改成 Completed。
