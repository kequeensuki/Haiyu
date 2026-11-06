# GameDataExport 统一导出模块实施与验收

本次按《GameDataExport-统一导出模块实现调研》实施。调研中的现状分析对应改造前代码，本文件记录改造后的结构、扩展方法和验收方式。

## 1. 实现结构

业务代码位于 `src/Extensions/Haiyu.ABI/Services/GameDataExport/`：

| 层 | 实现与职责 |
|---|---|
| 传输/登录 | `Session/CaptureConnectionState` 只校验、解密、识别登录与发布消息/会话事件；没有 Inventory、Ready 或背包解析分支 |
| 确认后分发 | `Session/MessageBatch` 先保留当前输入内的顺序，再根据捕获服务对连接的确认结果分发，候选/历史连接不能推进当前快照 |
| 协议 | `ProtocolDecoder` 生成只读字段模型；`ProtocolJsonWriter` 按需编码。`ProtocolJsonDecoder` 保留兼容入口及独立的解析/编码状态 |
| 模块契约 | `Modules/ExportContracts` 定义描述、订阅、会话通知、检查点、进度、完成和释放 |
| 组合根 | `Modules/BuiltInExportModules` 注册模块并兼容旧 Scope 请求；这里是默认选择策略的唯一业务入口 |
| 调度 | `Modules/ExportCoordinator` 按订阅分发，调度检查点和输出，统一汇总所有启用模块的结果 |
| 聚合 | `Aggregation/SourceState` 提供来源基线、分页、受限有序增量、完整性、新鲜度、一致性、不可变候选和回执确认 |
| 输出 | `Storage/JsonExportStore` 共享有界队列、总正文预算、路径/目标冲突检查、原子覆盖和串行回执；`JsonLinesArtifactWriter` 提供追加/分片/句柄上限 |
| 背包模块 | `Modules/InventoryExportModule` 从共享字段模型映射 DTO，不进行第二次 protobuf 解析；按登录会话聚合，成功写入后按玩家累计次数 |
| 消息模块 | `Modules/ServerMessagesExportModule` 使用同一个生命周期。`Events/EventExportSession` 只保留事件格式、玩家归属和统计，复用共享队列及文件写入器 |
| 兼容 | 旧 Inventory、PlayerSummaries、EventExport 字段由模块实现的 `ILegacyExportProjection` 投影。旧写入测试钩子隔离在 `GameDataExportService.Composition`，回放默认使用临时输出根，不写入用户 Documents 或仓库目录 |

`InventoryCaptureState` 已从生产工程移到测试工程，旧线协议夹具通过观察者组合诊断投影，不给生产连接类恢复业务属性。原公开 `InventoryMapper` 和旧格式 writer 保留兼容入口；生产背包模块使用共享字段模型与共享原子输出服务。

UI 通过 `IGameDataExportCatalogService` 查询 ABI 宿主中的模块目录，动态产生选择项；默认是否选中来自描述。选中模块 ID 通过请求传递，零选择明确拒绝。通用展示依据 Modules 中的状态、玩家计数、来源和产物，不以背包为默认业务结果。旧宿主结果的显示分支仅作兼容回退。

## 2. 聚合和持久化语义

- 一个模块实例属于一次捕获；背包内部以登录 sessionId 隔离来源，未确认玩家时保留在该登录会话内，确认后绑定 playerId，不能合并到公共 playerId=0 桶。
- 正式的同一登录重连沿用 sessionId，因此延续来源状态；新登录、换钥和账号切换建立新的身份代次。
- 必需、可选、条件未知来源分别表达。来源数量不固定；合法空数据与未收到数据不同。
- 分页按 assemblyId/版本收集，支持乱序、去重、总页数/结束标记，受页数、条目及等待期限限制。缺页不能发布新基线，旧版本不能替换正在收集的更新批次。
- 泛型增量基础只重放具有连续来源版本的变更；无版本、无基线、缺口或超限均不能猜测性合并。实体键、增删改、关联与条件启用仍由具体模块适配器提供。
- 当前四种背包 Response 没有共同批次标记，采用 **LatestComplete**，没有伪造服务器事务版本。**StrictBatch** 需要调用方提供可信批次证据；本地轮次不代表服务器原子快照。
- 候选冻结来源值和版本向量。V 写入期间来源更新，V 回执只确认 V；新向量仍可提交。失败重试不增加次数，成功回执才增加该玩家次数。
- 同一玩家本次捕获仍使用原文件名。新登录接管目标后，旧代次在原子提交前必须再次验证所有权；检查和重命名在同一短临界区执行。索引仅登记该路径当前成功版本，保留所有玩家成功计数。
- 聚合元数据包含来源本地/服务端版本、批次、登录代次、玩家、消息序号范围、接收时间及连接，不能将本地到达顺序冒充服务器业务版本。
- 一般未知/解码失败消息继续作为诊断事件保留。不可恢复输出错误统一停止并收尾；其他模块的成功不能覆盖失败。无数据为 NoData，部分来源为 Incomplete。

没有添加尚未验证的普通背包增删改 Notify。分页/增量的离线测试是基础设施验证，不表示已确认游戏中相应业务协议的语义。

## 3. 输出与资源边界

原有文件兼容：

- 背包：`{playerId}/game-data-{captureStart:yyyyMMdd-HHmmss}.json`，字段、计数包装、sessionKeyHex 等旧格式保留。
- 事件：`{playerId}/captures/{captureId}/events-*.jsonl`、未归属分片和 `captures/{captureId}/manifest.json`。
- 配置化消息模块使用 `modules/{moduleId}` 子目录隔离分片与 manifest；模块内 eventIndex 连续，captureEventIndex 保留原捕获消息序号，多个筛选模块可以同时启用。
- 新增总索引：`captures/{captureId}/capture-index.json`，登记每个启用模块的独立状态、计数、来源和产物；事件 manifest 只代表消息模块。

一次生产捕获共用 512 项 / 32 MiB 正文队列和 1 GiB 累计正文写入预算。元数据有独立的 4 MiB 内存上限，保证正文配额用尽后仍能尝试写结束索引；同样受队列项数限制。快照覆盖产生的每次写入也计入累计正文预算。事件默认分片 128 MiB，最多打开 16 个文件，最多 4096 个分片。

背包聚合另有 4096 个登录会话、32 MiB 累计来源 wire 大小以及每来源 100000 个条目的限制。wire 大小不是 CLR 堆的精确字节数，字段树、DTO 和当前编码缓冲还会占用内存；队列预算也不是进程总内存上限。限额触发必须明确失败，不能静默丢弃后返回成功。

输出回执在捕获/模块串行通道处理，后台文件线程不直接修改模块业务状态。进度由模块修订号触发并有公共心跳，不再把每条普通消息写盘都变成 UI 进度通知。

## 4. 新增模块的方法

1. 实现 `IGameDataExportModule`，或为简单消息事件导出复用 `ServerMessagesExportModule` 的 descriptor/messageIds 参数。
2. 在 `BuiltInExportModules.Create` 增加 descriptor 与 factory。默认选中策略写在 descriptor，显示资源通过 NameKey 指定。
3. 在 Consume 中读取共享 ProtocolMessage；使用共同的 Store.Snapshot 或 JsonLinesArtifactWriter。DTO 编码使用显式 JsonTypeInfo，不用反射或 dynamic。
4. 按需组合 AggregateState。一个 msgId 可向多个 SourceState 投递，多个 msgId 也可更新同一来源；关联键和语义完整性校验由适配器实现。
5. 返回通用计数、来源和产物；不可恢复故障从消费/检查点抛出，由公共服务受控停止。

不用改捕获循环、登录状态机或 UI 模块 ID 分支。离线测试注册了非背包下行消息模块，独立完成 JSON 写入及结果序列化。

## 5. 离线验证命令

```bash
source /workspace/.setup/env.sh
DOTNET_PROCESSOR_COUNT=2 dotnet build src/Tests/ABIRuntime.TransportTests --no-restore -m:1 -nr:false -p:UseSharedCompilation=false
DOTNET_PROCESSOR_COUNT=2 dotnet src/Tests/ABIRuntime.TransportTests/bin/Debug/net10.0/ABIRuntime.TransportTests.dll --game-data-export
DOTNET_PROCESSOR_COUNT=2 dotnet src/Tests/ABIRuntime.TransportTests/bin/Debug/net10.0/ABIRuntime.TransportTests.dll --transport
DOTNET_PROCESSOR_COUNT=2 dotnet build src/Tests/GameDataExport.SessionTests --no-restore -m:1 -nr:false -p:UseSharedCompilation=false
DOTNET_PROCESSOR_COUNT=2 dotnet src/Tests/GameDataExport.SessionTests/bin/Debug/net10.0/GameDataExport.SessionTests.dll
python -m unittest discover -s tools/tests -p 'test_validate_game_data_export.py'
DOTNET_PROCESSOR_COUNT=2 dotnet publish src/Tests/GameDataExport.ProtocolSmoke -c Release -r linux-x64 --self-contained true --no-restore -m:1 -nr:false -p:UseSharedCompilation=false -o /tmp/haiyu-module-aot
/tmp/haiyu-module-aot/GameDataExport.ProtocolSmoke --keep
```

AOT smoke 使用真实校验/解密管线，同时启用两个内置模块和一个按消息 ID 筛选的模块，验证 JSON、JSONL、公共索引及新增 ABI 的 MemoryPack 往返。`--keep` 保留测试目录并输出 manifest 路径，可继续用下文工具校验。Windows 发布产物和 WinUI/XAML 构建不由 Linux smoke 代替。

## 6. Windows 实机步骤及验收标准

先用同一次构建更新 Haiyu UI 与 ABI 宿主 DLL；新增 MemoryPack 契约要求两端配套。保持 Npcap 已安装、凭据可读。在游戏启动/重新登录前启动捕获；仅进入世界而没有新的登录握手不能验证首次取钥。

| 用例 | 操作 | 验收标准 |
|---|---|---|
| 目录及零选择 | 打开导出窗口，取消全部模块并开始 | 显示明确的未选择提示，不启动捕获；可分别勾选背包和消息 |
| 仅背包 | 只选背包，开始后启动游戏并进入世界 | 四来源均收到后产生原格式 JSON；UI 显示玩家、更新次数及来源；总索引只登记 inventory |
| 仅消息 | 只选消息，重新登录并进入世界 | 消息持续写 JSONL；不产生本次背包 game-data 文件；停止后模块和总索引均成功 |
| 同时启用 | 勾选两种模块，重新登录 | 一次捕获共用 captureId；两种产物同时生成；总索引有两个模块，不能把事件成功当作背包已完成 |
| 多来源等待 | 登录过程中观察来源进度；在来源未齐前停止 | 缺少必需响应时不生成伪空背包；模块为 NoData 或 Incomplete，不能整体报成功 |
| A→B→A | A 进入世界、回登录换 B，再切回 A；最后停止 | 文件按玩家归属，A 的次数继续累计，B 独立；最终展示两个玩家，A 的最新来源代次对应最后登录；旧连接不能回滚 UI 或文件 |
| 单来源全量刷新 | 使用已知会触发某种完整 Response 的操作/回放 | LatestComplete 只替换该来源，其他有效来源保留；不承诺未验证 Notify 会触发更新 |
| 切网 | 已导出后有线转无线，等待游戏重连 | 同一登录的密钥/玩家/来源不因换网清空；新有效连接后继续写，重复镜像包不额外增加更新次数 |
| 窗口与游戏生命周期 | 捕获中关闭并重开窗口；然后退出游戏进程 | 关闭观察窗口不中断捕获；重开正确反映模块及状态；游戏退出停止所有接口并收尾 |
| 无密钥期限 | 开始捕获并启动游戏，但让其一直没有有效握手/密钥 | 自检测到游戏启动起达到 30 分钟后关闭所有候选接口；取得密钥后不受该初次期限影响 |
| 写盘失败 | 在可控测试目录/权限或离线注入中制造写入失败 | 保留上次成功文件，失败重试不增加成功次数；不可恢复故障不报整体成功，已成功模块仍可追溯 |
| 凭据与发布 | 导入有效 PEM，再用正式配套发布运行 | 凭据读取/导入原有行为保持；WinUI 页面及本机 NativeAOT ABI 都能正常调用新目录和导出契约 |

验收关注本次新 captureId/时间戳，避免将历史文件当作本次输出。仅消息时仍会创建玩家的 JSONL 目录，不能用“没有玩家目录”作为禁用背包的判断条件。

停止后校验总索引：

```powershell
python tools/validate_game_data_export.py --capture-index "C:\Users\<user>\Documents\Waves\GameDataExport\captures\<captureId>\capture-index.json"
```

工具检查模块完成状态、产物归属/路径/存在性、JSON 和来源版本，并对事件 manifest 继续执行原有流式校验：JSONL 编号、字节数、解析分类、登录绑定与玩家汇总一致。未完成捕获应校验失败，不能修改索引冒充已完成；尾记录修复仍只适用于显式指定的事件 manifest。

## 7. 验证结果

- 30 组导出测试通过：包含原有 21 组以及新增的聚合、版本/回执、注册扩展、筛选模块、完成状态和已确认恢复通知检查。
- 11 组实际 ViewModel/会话业务测试通过，包括新模块自动呈现、空选择、禁用背包、通用结果、凭据导入和窗口生命周期。
- 5 组 ABI/传输测试通过，覆盖 DI、序列化、完整性、并发和取消。
- 10 组 Python 离线验收工具测试通过，包含公共索引和模块子目录。
- 三模块 NativeAOT 实际运行及其产物校验已通过：5481 个协议类型、3910 个消息注册；总索引登记 3 个模块、6 个产物。最终 Release 重建及实际运行同样通过。

云端曾出现 MSBuild 子进程异常退出，随后显式 `dotnet build -m:1 -nr:false` 串行重试成功。保留原有 MemoryPack.Core 的 IL2104/IL3053 警告；不把这些既有库警告解释成 Windows 发布验证。

Windows 实机结果由使用者按上表验证，本次没有运行 Windows UI、Npcap 驱动或凭据管理器实机验收。

## 8. ABI 发布位置与模块目录注册排查

`publish.ps1` 必须向 `[Environment]::GetFolderPath('MyDocuments')` 下的 `Waves\Haiyu.ABI` 部署，与 `AppSettings.ABIRuntimeSavePath` 及界面导入 ABI 包使用的目录一致。不能用 `%USERPROFILE%\Waves\Haiyu.ABI` 替代；文档目录可能被 OneDrive 或组策略重定向。

此前脚本将新版 DLL 部署到了用户目录，而应用仍从文档目录加载旧版 DLL。旧宿主能完成 UAC 提权及基础初始化，但没有 `haiyu.gameDataExport.modules.v1` 服务，打开导出窗口便会返回 `0x80004001`。当前组合根已经注册模块目录服务，修复应统一部署位置。

Windows 验收：退出 Haiyu，运行根目录 `publish.ps1`，确认末尾 `ABI` 路径指向实际文档目录。可在仓库根目录检查发布 DLL 与部署 DLL 的 SHA256：

```powershell
$deployedAbi = Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'Waves\Haiyu.ABI\Haiyu.ABI.dll'
Get-FileHash '.\src\Extensions\Haiyu.ABI\bin\win-x64\publish\Haiyu.ABI.dll', $deployedAbi
```

两者哈希应一致。启动新发布的 Haiyu，打开游戏数据导出并确认 UAC，窗口应加载模块选择项，不再显示未注册 Service。开始导出后应进入正常捕获流程。已重定向文档目录的机器应满足同一标准。

无需删除用户目录下此前误部署的文件；应用仍使用现有文档目录架构。退出并重新启动 Haiyu 可使其启动新的 ABI 宿主，避免沿用旧进程。
