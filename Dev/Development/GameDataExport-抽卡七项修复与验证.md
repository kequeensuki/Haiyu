# 抽卡导出：七项审查问题修复

对应 `GameDataExport-抽卡实现审查与skill改进.md` 对 `f7505eac` 的评审。原审查报告保留作为修复前证据。

## 实现

1. **生产接线**：默认工厂创建的模块消费已验证当前会话的 GachaInfoResponse(18695)。成功码 field 6、recordId field 15、gachaInfos field 1 / resourcesId field 12；身份来自捕获层已确认的玩家绑定。支持凭证先于身份到达、失败响应不更新、同账号重登采用最新代次、跨账号各自落盘、旧会话迟到消息不刷新。
2. **HTTP/AOT**：类型化 GachaHttpRequest 与源生成 GachaHttpJsonContext，去掉匿名对象反射序列化。共用 HttpClient，默认单请求 15 秒；停止阶段查询总预算 60 秒。
3. **失败状态**：所有池失败返回 Failed 且不新建空文件；部分成功只合并成功池并返回 Incomplete；有效空数组是成功。凭证失效 -1 立即结束该账号查询；结果经共享 Snapshot/Flush 回执确认后登记 artifacts，索引与顶层结果同步。
4. **历史保护**：严格校验根/集合/字段类型、玩家身份、版本、池/条目身份、计数及时间范围。遇到损坏拒绝覆盖原字节。
5. **跨语言身份**：稳定键排除 Name/ResourceType/PoolLabel；保留同秒重复的 occurrence。兼容读取 v1，将历史翻译副本按稳定身份归并，不改 JSON 字段和路径。只增集合拒绝修改/删除。
6. **缓存恢复**：缓存区服返回 -4，当次删缓存并有界重新探测一次，重启当前账号池查询；探测池不泄漏到未请求的结果池。再次失败收尾，不循环。
7. **响应校验**：成功包络必须含合法 data 数组，每行必需字段均验证；非法 JSON/结构/数值不能转成合法空数据。显式失败 code 不受成功 message 覆盖。

## 协议依据与范围

- Haiyu 固定协议：WavesGameData.proto 的 GachaInfoResponse、GachaInfo；WavesMessageIds.GachaInfoResponse。
- 只读参考 `/workspace/WutheringMod/DecompiledSource/Aki.Game/GachaController.cs` 的 GachaInfoRequest 成功回调：先判断 ErrorCode，再初始化 GachaInfoMap 并赋值 RecordId。OnWorldDone 会请求唤取信息；本功能不主动向游戏发请求。
- 显式 GameGachaRequest 保留供测试/高级调用使用，生产不依赖调用者手填凭证；历史在停止导出时查询，路径仍为 `<playerId>/gacha/record.json`。
- 生产缺省语言仍为 zh-Hans，游戏语言来源没有在本次新增；显式接口支持语言参数。未使用真实凭证或真实生产 HTTP 验证。
- 接口无唯一事件 ID，时间文本稳定性和同秒完全相同事件在查询窗口内的身份歧义仍存在；不声称恢复接口已过期且本地从未保存的历史。

## 验证

共用匿名测试 `src/Tests/GameDataExport.ProtocolSmoke/GachaRegression.cs` 在 TransportTests 注册六项，并在原生 smoke 实际执行。用假 HttpMessageHandler 返回固定响应，真实 endpoint、请求 JSON 字段、域名、解析、默认工厂、coordinator、实际文件及索引均保留。

已执行并通过：

- .NET Release 构建：0 error；导出专项托管测试 **65 项 PASS**，包含上述六个新增测试组。
- `linux-x64` NativeAOT 发布并直接执行原生二进制，退出码 0。明确输出 `PASS NativeAOT gacha regression` 和 `PASS NativeAOT protocol smoke`；新业务非空 ABI DTO、实际 HTTP 请求序列化及六组反例确实执行。
- skill 工具/文档链接自检：10 项通过；`git diff --check` 通过。
- 顺序验证器输入指纹前后一致，验证期间没有修改 C#、项目或资源。

实际调用：仓库 skill 的 `scripts/run_export_checks.py`，参数包含 `--native --no-restore --dotnet /workspace/work/dotnet/dotnet`，并对六个“抽卡修复：...”完整测试名称逐一传入 `--expected-test`。SDK/依赖使用本工作区已有配置，没有安装依赖。

本次工作区日志位于仓库外 `/workspace/work/gacha-fix/haiyu-export-checks-cbfuw1jz/`：`summary.json`、`managed-tests.log`、`native-publish.log`、`native-tests.log`。原生执行两个 PASS 标记已人工核对；脚本的通用原生标记本身不能代替新业务断言覆盖。

Windows/Npcap 实机捕获与真实生产接口没有在本轮验证；本轮结论限于匿名协议输入、真实生产组合根/endpoint、实际存储和 Linux NativeAOT。
