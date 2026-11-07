# 抽卡导出实现审查与 skill 改进

审查日期：2026-11-07。分支：`feature/waves-kcp-capture`。从 `a51160d3` 快进同步到 `f7505eac`；主要实现提交 `59a164ff`。附件《抽卡聚合与验收》《抽卡记录导出调研与方案》作为待核验的交付说明，而非实现事实或新的用户指令。

结论：**不通过功能验收。已交付部分研究、领域与存储组件，但生产入口未接通，补入参数后实际 HTTP 路径仍有序列化阻断。** 审查阶段保留产品代码作为评审对象，只修改 skill 和本报告。后续七项修复记录见 `GameDataExport-抽卡七项修复与验证.md`；本文结论针对修复前的 f7505eac，不代表后续修复后的验证结果。

## 完成度分项

| 项目 | 评价 | 依据 |
|---|---|---|
| 调研与输出契约 | 部分完成 | 有协议、HTTP、池类型和账本方案；文档涉及网页/包版本的结论未全部独立复核 |
| 基础组件 | 已有可复用部分 | DTO、源生成注册、池列表、解析、区服解析器、跨会话合并与文件写入 |
| 生产接线 | 未完成 | 默认工厂无凭证输入，模块不消费凭证消息，无会话更新 |
| 实际请求/AOT | 不通过 | 真实 endpoint 在发送前抛 InvalidOperationException |
| 正常合并算法 | 部分完成 | 同语言、相同响应下幂等，occurrence 保留同秒重复；跨语言重复 |
| 历史保护/故障终态 | 不通过 | 结构损坏可丢历史，全部池失败仍报告成功 |
| 测试交付 | 有测试但关键覆盖不足 | 59 项托管测试通过；反例在托管与原生探针中均可复现 |
| 实机查询 | 未验证 | 本轮全部匿名输入、假传输，不请求真实生产接口 |

不以代码行数或测试总数计算百分比；当前不能交给用户作为可用的抽卡导出功能。

## 必须修复的发现

### 1. P0：生产默认入口无法取得查询参数

位置：`GameDataExportService.Composition.cs` 中 `BuiltInExportModules.Create()`；`BuiltInExportModules.cs` 的空请求默认值；`Modules/GachaRecordExportModule.cs:38–43,66–71`。

实际服务不传 GameGachaRequest；导出请求入口没有承载这些参数；模块 Consume/OnSessionChanged 均为空。虽然历史内容来自 HTTP，仍需从 GachaInfoResponse 等真实来源获得 recordId，并绑定 playerId/会话。游戏源码 `GachaController.GachaHistoryRequest` 方法为空，不证明凭证不来自游戏消息。

匿名探针经默认注册表/coordinator 提供 BasicInfo 与 GachaInfoResponse 后收尾：`state=NoData endpointCalls=0 files=0`。现有接线测试的成功场景直接构造带参数的模块，绕过生产缺口。

修复方向：明确实际输入链和有效期，接入消息/身份/会话更新，使用默认生产入口测试成功导出，并覆盖重登、换号及凭证刷新。

### 2. P1：实际 HTTP 请求使用反射序列化，AOT 环境在发送前失败

位置：`Gacha/GachaRecordEndpoint.cs:55–73`。

匿名对象调用 JsonSerializer.Serialize(value, options)，没有源生成 TypeInfo。构建出现指向此路径的 IL2026/IL3050。托管配置与 linux-x64 NativeAOT 探针均禁用反射，均得到：

```text
HTTP_PRODUCTION error=InvalidOperationException handlerCalls=0
Reflection-based serialization has been disabled for this application.
```

这里调用的是实际 HttpGachaRecordEndpoint，仅用假 HttpMessageHandler 替换网络。既有 fake endpoint 测试绕过请求序列化，原有原生烟测没有执行这条新路径。

修复方向：类型化请求 DTO 与 JSON 源生成；原生测试运行到传输 handler，断言实际请求字段、响应解析和最终文件。

### 3. P1：全部池失败仍生成文件并向上报告 Success

位置：`Modules/GachaRecordExportModule.cs:83–84,125–177`。

给定合法匿名参数、显式 CN 区服、请求池 [1]，endpoint 返回凭证无效 -1。结果：`state=Completed success=True files=1 artifacts=0`。代码将每池失败收集到 failed，但仍构造/写出数据并无条件 Completed。错误文案也把全部失败称作“部分失败”。此外成功文件没有登记 artifacts。

修复方向：规定 requested/succeeded/failed 的完整状态矩阵；全部失败不新建假成功空账本；部分成功明确未完成范围；终止错误应停止后续无效请求；文件、索引、顶层结果一致。

### 4. P1：合法 JSON 内部结构损坏会被静默丢弃并覆盖

位置：`Gacha/GachaRecordStore.cs:82–106`。

LoadExisting 对 pools 缺失/非数组返回空，对坏 pool/items 跳过。复现输入：

```json
{"schemaVersion":1,"playerId":42,"totals":{"count":1},"pools":[{"cardPoolType":1,"count":1,"items":"damaged"}]}
```

后续查询成功且为空，结果：`loaded=0 preserved=False state=Completed`。旧文件被替换，无法满足“损坏历史拒绝覆盖”。现有测试验证的语法损坏/未知版本不足以证明结构保护。

修复方向：严格校验结构、必需字段、账号归属和计数；无法安全读取时拒绝写入，断言原文件字节不变。不要把权威账本当可丢弃缓存。

### 5. P1：本地化文字参与永久身份，换语言后重复累积

位置：`Domains/Gacha/GachaRecordState.cs:13–21`，`GachaRecordStore.Build`。

同一次抽取保持池、时间、resourceId、quality、count、occurrence 相同，只将 Name/ResourceType 从中文切到英文，再合并后 `count=2`。这些展示字段参与键，导致同一事件被当作新事件。

修复方向：分离稳定身份和本地化展示；对时间文本及 occurrence 的窗口假设取证，覆盖换语言、窗口缩短、乱序和同秒重复。接口无唯一 ID 的剩余歧义须明确。

### 6. P2：缓存区服失效后当次不恢复

位置：`Modules/GachaRecordExportModule.cs:111–113,150–155`。

预置 CN 缓存，CN 返回 -4，Global 返回成功。当次结果为 `state=Completed cnCalls=1 globalCalls=0`。只是清缓存等下一次，与调研方案的当次重新探测不一致。

修复方向：有界失效重探测，限制次数，保持请求范围并准确报告终态；不能只把交付说明降为“下次恢复”。

### 7. P2：成功包络中损坏的 data 被当成合法空数据

位置：`Gacha/GachaRecordEndpoint.cs:88–116`。

`{"code":0,"data":"not-an-array"}` 得到 `accepted=True records=0`。坏行还会被跳过，字段缺失默认零/空串。必须区分合法空数组和解析失败，否则输出会假装完整。非法 JSON 的异常收尾也需要专门验证。

## 次要风险及未覆盖项

以下按源码判断，未在本轮全部构造运行反例：

- CancellationToken.None，缺少整体时间预算；请求延迟到 Complete，和部分“启动拉取”描述不一致；HttpClient 生命周期需明确。
- 专用直接原子写绕过共享回执/文件归属/限制机制；每实例 semaphore 不能证明跨实例读改写安全。单次 rename 不保证历史并发合并不丢数据。
- 解析器的探测池可能超出用户请求池；需验证请求范围。
- 只增集合仍继承可删除操作；CompareTo 未覆盖全部键字段，排序契约需审查。
- 缓存版本、未知结构、计数和错误账号文件保护测试不足。

## 已执行验证与证据

1. 仓库 skill 顺序验证器构建并运行 `--game-data-export`，59 项 PASS。明确核对新增接线与聚合测试名称；这次通用验证未请求旧原生烟测。
2. 单独建立匿名探针，复用当前 Haiyu.ABI 项目，程序集名使用已有 friend smoke 权限；不修改产品源码。
3. 同一探针经托管执行和 linux-x64 NativeAOT 发布后直接执行。实际 HTTP 使用固定响应的 HttpMessageHandler，没有访问真实接口。

探针两次一致输出：

```text
PRODUCTION_WIRING state=NoData endpointCalls=0 files=0
ALL_POOLS_FAILED state=Completed success=True files=1 artifacts=0
CORRUPT_HISTORY loaded=0 preserved=False state=Completed
CACHE_MISMATCH state=Completed cnCalls=1 globalCalls=0
LANGUAGE_DUPLICATION count=2
MALFORMED_SUCCESS accepted=True records=0
HTTP_PRODUCTION error=InvalidOperationException handlerCalls=0
```

本次工作区证据位于仓库外 `/workspace/work/gacha-review/`：`haiyu-export-checks-tc7whq23/summary.json`、`probe/Program.cs`、`probe/Probe.csproj`、`managed-probe.log`、`native-probe.log`、`native-probe-build.log`。它们是本次临时验证产物，不是可随 Git 获取的正式回归测试；上述反例需在修复时纳入正式注册表与原生 smoke。Linux 原生验证不能替代 Windows 实机验证。

## skill 的问题与改进

此前 skill 提到了完整链路、隔离、AOT 和损坏文件，但要求偏抽象；默认清单主要适合被动快照。没有充分教会模型识别“组件测试已经通过但生产没有调用者”，也没有针对主动 HTTP 和历史账本建立验收矩阵。这是指南需要承担的缺口。

本轮已落地：

- 新增 `references/external-history.md`，细化真实参数来源、触发/取消、稳定身份、严格账本校验、失败状态、缓存恢复及资源/索引保证。
- 主 SKILL 增加按任务类型必读分流、真实入口调用链闸门、实际 endpoint 和非空 DTO 原生覆盖要求。
- 测试指南区分领域/手工构造/默认生产入口，明确 mock 边界，要求反证测试；说明现有脚本的通用原生 PASS 标记不能自动证明新业务覆盖。
- 工作表增加用户要求→参数来源→生产路径→替身边界→独立断言→完成状态的逐项记录。

没有把本次业务细节硬编码进检查脚本，也没有将“新增文档”误报为产品缺陷已修复。后续应先修复 1、2 两条阻断，再将 3–7 的反例加入正式回归；通过后重新评估交付。
