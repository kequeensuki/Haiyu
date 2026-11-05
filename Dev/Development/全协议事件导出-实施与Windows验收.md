# 全协议事件导出：实施说明与 Windows 验收

## 已实现的边界

- 从 WutheringMod `48026711997617dbe86f266c948573efcae94c87` 的只读反编译源码生成 5481 个消息、333 个协议枚举、13406 个字段、3910 条编号注册。嵌套消息纳入后，数量比初步盘点的顶层文件数多 4 个；oneof case 等 C# 辅助枚举不作为协议枚举输出。
- 完整入口仍是 `src/Extensions/Haiyu.ABI/Protocol/WavesGameData.proto`，由现有 Grpc.Tools 编译。另生成静态 schema / 编号映射、消息编号常量和版本报告。
- 通用路径使用预生成 schema 的 wire 解码器，不扫描官方 DLL，也不依赖 CLR 属性反射；类型化 protobuf 模型仍提供给原有背包模块。
- UI 默认仍为背包，勾选“同时导出全部服务端消息（JSONL）”才开启全量。自动启动已建立的捕获不会被中途换模式；请先停止，再选择全量并开始。
- 保留背包文件、按玩家独立更新次数、多玩家结束汇总、网卡切换、初始密钥等待超时及退出停止行为。其他业务状态不做合成，新增 Notify 仅作为消息事件保存。

## 产物和格式

```text
GameDataExport/
  captures/{captureId}/manifest.json
  captures/{captureId}/unresolved-000001.jsonl
  {playerId}/game-data-{原时间戳}.json
  {playerId}/captures/{captureId}/events-000001.jsonl
```

JSONL 记录 captureId、接收序号 / 时间、登录 sessionId、连接编号、玩家、帧类型、msgId、原限定类型名、schemaVersion、解析状态及内容。64 位整数以字符串保存；bytes 使用 Base64；已知枚举使用源码 OriginalName，未知枚举保留数字。按源码属性名首字母小写输出字段；缺失字段不补造值，wire 中出现的零值保留。

- `decoded`：当前 schema 已解析。
- `decoded_with_unknown_fields`：已解析已知部分，并保留完整原始 protobuf，不能视为全部字段已知。
- `unknown_message` / `parse_failed`：保留原载荷和阶段 / 错误。
- `key_unavailable` / `decrypt_failed` / `decompress_failed` / `key_resolution_failed`：保留原帧载荷，明确它是密文或尚不可解码数据，不冒充明文 protobuf。
- `invalid_frame` / `incomplete_raw_frame`：原始网关边界或裸帧不完整，保存片段而不伪造 protobuf；停止时有未完成 KCP 重组会标记 incomplete。
- `transport_exception`：网关 Exception 帧，无 msgId，不套用消息 parser。
- `json_limit`：protobuf 转 JSON 达到单消息展开上限，保留原始 protobuf。

playerId 未确认时，事件先写 unresolved 文件，行中 playerId 为 null；确认后由 manifest.sessionBindings 建立 sessionId → playerId 的绑定，不反复搬写事件。统计中的 unresolved 是**仍无法经绑定确认归属**的数量，不是所有 null 行的数量。验证工具按该索引计算归属。新登录 / 换钥后不沿用旧玩家；网络重连沿用登录会话，连接编号单独记录。未验证候选连接上的未知 / 坏帧单独标记 unverified 会话并保持未归属，不因复用了旧密钥就归给旧玩家；已知有效消息可确认新连接并重新锁定接口。

manifest 同时记录开始 / 结束时间、实际限额和结束原因。取消通道只提供通用取消信号，因此 `caller_stop` 合并表示手动停止、游戏退出或等待超时的调用方停止；更具体的原因由应用最终提示给出，不伪造宿主无法区分的原因。`error` 表示导出异常。

manifest 的 `completed` 表示收到的事件全部完成写入，不表示服务器的所有数据已发送，也不表示每条消息均成功解码。`unknown`、`failed` 必须单独查看。异常、限额或未完成写入会标记 `incomplete`；崩溃后最后一个检查点可能保持 `running`，不得伪装成功。

默认资源边界：队列 512 项且累计正文不超过 32 MiB；原始单载荷不超过队列预算一半；单消息展开 JSON 不超过 16 MiB，嵌套最多 64 层、字段 / 集合元素预算 100 万；单分片 128 MiB；本次捕获事件正文总量 1 GiB（不含索引）；最多同时打开 16 个事件文件；最多 4096 个分片、登录绑定或待归属会话。触达硬限额会受控停止并报告不完整，不覆盖历史分片。常规检查点和 UI 聚合刷新约 2 秒，停止时排空并刷新；慢磁盘可能超过既有停止宽限，此时 UI 应继续显示后台仍在收尾。

## 更新协议

需要 .NET 10 SDK（使用 SDK 自带 Roslyn，无需游戏 DLL），以及 Grpc.Tools 2.66.0 的 protoc。只生成开发产物，不在应用运行时执行生成。

Windows PowerShell，从 Haiyu 根目录运行：

```powershell
$sourceRepo = '替换为WutheringMod仓库目录'
$sourceCommit = git -C $sourceRepo rev-parse HEAD
$nugetRoot = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget\packages' }
$protoc = Join-Path $nugetRoot 'grpc.tools\2.66.0\tools\windows_x64\protoc.exe'
# 尚未还原依赖时，先执行：dotnet restore src/Extensions/Haiyu.ABI/Haiyu.ABI.csproj
dotnet run --project tools/WavesProtocolGenerator -- --source "$sourceRepo\DecompiledSource\Aki.Protocol" --output src/Extensions/Haiyu.ABI/Protocol --commit $sourceCommit --protoc $protoc --check
```

退出码 0 表示当前产物完全一致；1 表示差异或验证失败，查看输出。需要更新时去掉 `--check`。生成器会验证 C# 语法、依赖、编码和 tag、map 编码、默认值、三个注册来源及 protoc；校验通过后才替换变化的产物，异常会尝试回滚。应一并提交 proto、schema、编号常量和版本报告。schema 为格式化 JSON，字段 / 枚举 / 注册变化可在 git diff 中审查。规范化源码 BOM 与 CRLF/LF 后计算版本哈希，输出不包含生成时间。

## 云环境已验证与限制

已完成完整 schema 生成、protoc 校验和全量 ABI 编译；最初全量编译约 100 秒，protoc 生成 C# 源码约 53 MiB，这不是最终二进制大小。

已运行独立 wire 向量测试（不是仅将生成器与自身往返）：zigzag、fixed/sfixed、超过 JavaScript 精度范围的 int64、packed、map、oneof、未知 / 损坏消息；并回归全量捕获、raw 身份推送先于 KCP 登录确认、候选连接隔离、有效非背包消息确认重连、A→B→A、异常保留摘要、磁盘 / 队列上限、窗口模式与统计、旧背包、切网及等待密钥超时。

另以带节奏的 50000 条合成事件执行流式压力检查：约 25 MB 正文，约 12.6 秒，结束后托管内存保留增量小于 0.1 MiB。该检查证明没有保留全部消息或按相同 payload 去重，不是无节流峰值吞吐或真实游戏基准。

Linux x64 NativeAOT 冒烟产物实际执行通过，覆盖生成的类型化 parser、通用解码、事件落盘和 MemoryPack 统计：约 80 ms 完成，托管内存约 24 MB，NativeAOT 可执行文件约 41 MiB（不含调试符号）。这是云机器的单次冒烟数据，不代表 Windows 的启动 / 峰值内存 / 游戏持续流量性能。发布仍有 MemoryPack.Core 等依赖的裁剪 / AOT 分析警告；不能把实际冒烟通过解释为所有 Windows 路径已验证。

没有在此环境验证真实 Npcap、WinUI、Windows 凭据管理器或 Windows NativeAOT 宿主。以下由开发机执行。

## Windows 自动检查

```powershell
dotnet run --project tools/WavesProtocolGenerator -- --self-test
dotnet run --project src/Tests/ABIRuntime.TransportTests -c Release -- --game-data-export
dotnet run --project src/Tests/ABIRuntime.TransportTests -c Release -- --transport
dotnet run --project src/Tests/GameDataExport.SessionTests -c Release
python -m unittest discover -s tools/tests -v

$smoke = Join-Path $env:TEMP 'HaiyuProtocolSmoke'
dotnet publish src/Tests/GameDataExport.ProtocolSmoke -c Release -r win-x64 -o $smoke
& (Join-Path $smoke 'GameDataExport.ProtocolSmoke.exe')
```

要求 .NET 10、Windows SDK 和 NativeAOT 所需的 C++ 工具链。验收：所有测试退出码 0；原生冒烟打印 PASS，事件落盘与 MemoryPack 往返成功。Windows 原生凭据测试应实际执行，不能使用云环境的 SKIP 代替。

正式 UI / ABI 使用原项目的 Windows 构建与部署流程，同时更新主程序和 ABI 包；如需单独发布 ABI，可使用：

```powershell
dotnet publish src/Extensions/Haiyu.ABI/Haiyu.ABI.csproj -c Release -r win-x64 -p:NativeLib=Shared
```

关闭 Haiyu 并确认旧 ABI 宿主退出后再替换完整产物，不能只更新托管模型 DLL。记录构建时间、发布大小及警告，确认启动用的确为新包。

## 游戏实机验收步骤

### A. 单账号完整链路

1. 确认 RSA 私钥已导入、Npcap 已安装。停止旧捕获，勾选全量，点击开始，再启动或重新登录游戏。
2. 进入世界后正常打开背包、角色等界面；不要额外运行会修改游戏或发送请求的工具。
3. 确认 UI 持续展示消息数量和 manifest 路径；普通 UDP 帧数、消息数与背包快照更新次数是不同单位。
4. 点击停止，读取对应 manifest，然后执行：

```powershell
python tools/validate_game_data_export.py --manifest '替换为本次manifest.json路径'
```

验收：退出码 0、PASS；文件每行可解析；事件编号唯一且无缺口；received=written、dropped=0；UI / manifest / 文件的总计和玩家计数一致。登录和四类背包的已知消息应正确解码，64 位值不丢精度。未知 / 未解析消息必须有原始载荷和状态；若已知同版本的常规消息失败，记录 msgId、状态和 schemaVersion，视为待修复，不能仅凭 PASS 的文件完整性检查判定协议语义正确。

### B. 切号与重复进入

在同一次捕获中 A→B→A，最后停止并运行同一验证工具。

验收：A/B 目录和 sessionBindings 正确；登录前的待归属记录经索引关联到对应账号，不挂到上一个账号；结束汇总含 A/B；A 的快照更新独立续计，B 首次从 1 开始；同一玩家的摘要不重复。关闭导出窗口再打开，模式与后台状态一致，结束后汇总仍在。

### C. 网络切换与游戏退出

进入世界并取得身份后，有线切 Wi-Fi（或反向切换），等待游戏自动重连，再操作界面；另测游戏进程退出。

验收：有新连接时 connectionId 改变，已确认的同一登录上下文 / 玩家不丢失；恢复后继续生成事件；不因普通重复捕获多写相同传输事件，合法相同内容的不同消息仍保留。退出游戏后自动停止并完成 manifest，验证工具 PASS；网卡句柄及网络通知订阅正常释放。若没有两种接口，只完成能测的场景并注明。

### D. 未收到密钥

启动捕获并启动游戏进程，但不完成握手，等待 30 分钟。

验收：达到初始等待密钥期限后所有接口停止，不因进程存活 / 普通流量续期。取得密钥后正常游戏超过该时长不因这项初始超时终止。云环境的可推进时钟测试验证了边界，但不能替代 Windows 生命周期实测。

### E. 长时间与失败场景

- 正常游戏持续 30～60 分钟，记录开始、10 分钟、30 分钟的工作集、文件增长和 UI 响应。元数据加载、运行时预热后内存应趋于稳定，不随事件数持续线性增长；正常负载不应触发队列溢出。具体峰值需实测记录，不预先假定云环境数值。
- 用测试用例验证小配额，不要为测试占满系统盘。验收：队列 / 磁盘限额导致清楚的停止原因与 incomplete，不伪报成功；已写 JSONL 可读，dropped / 未写数量可见。
- 单独测试强制退出捕获宿主，事先保留样本。验收：最后 manifest 为 running / incomplete，离线工具拒绝把它判为完整；JSONL 最后一条若未完成应可定位。可执行 `python tools/validate_game_data_export.py --manifest '<路径>' --repair-tail` 备份并移除半条尾行；工具保留 `.partial-tail.bak`，不改变 manifest，后续完整性检查仍应 FAIL，不能通过删改 manifest 冒充 completed。

## 非目标与问题回传

这次不合成所有游戏状态、不主动补抓服务器未发送的数据、不支持任意其他网络通道，也不恢复捕获前的历史。不要用“世界里有某对象但没有对应事件”单独断言协议遗漏，应先确认该对象消息是否确实在捕获期间由本链路发送。

回传信息建议：构建提交、sourceCommit / schemaVersion、操作步骤、UI 结束状态、manifest 和验证工具输出。原始事件可能包含账号数据，不必直接上传整份全量日志；优先提供失败消息类型、编号及经过确认的最小复现片段。
