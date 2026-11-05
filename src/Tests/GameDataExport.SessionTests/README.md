# 游戏数据导出生命周期回归

从仓库根运行（.NET 10）：

```sh
dotnet run --project src/Tests/ABIRuntime.TransportTests -c Release -- --game-data-export
dotnet run --project src/Tests/ABIRuntime.TransportTests -c Release -- --transport
dotnet run --project src/Tests/GameDataExport.SessionTests -c Release
```

第一组使用真实 ABI 实现，覆盖 RSA/AES、压缩、半帧、重登换钥和切号、112 重传、缺口超时和缓冲上限、目录故障恢复重试，以及真实命名管道 Cancel→收尾→Result。

第二组验证共享 ABI 管道的依赖注入、序列化、大消息、并发及取消控制帧。

第三组链接生产 Launcher、Windows 启动适配层和 ViewModel 源码。`UiBoundaries.cs` 只替代 Windows 宿主边界、语言查询、ViewModel 基类及 Shell API；不启动真实 WinUI。覆盖就绪/失败/超时、停止终态、会话复用、每会话空转 watcher、游戏运行时保留会话、自动会话窗口接入，以及窗口释放后的弱引用回收。窗口状态通过受控 SynchronizationContext 调度。

这些检查不代替 Windows 下的 NativeAOT 发布、Npcap 真机抓包和 XAML 验证。部署时须同时更新 UI 和 ABI 包，并重启旧宿主：进度模型尾部新增了显式的 CaptureReady、HasDataUpdate 信号。

## 本次修复范围

- 新的有效 112 原子换钥并隔离旧快照；相同 112 重传不清空快照，失败协商保留旧密钥和历史文件。
- 只有网卡启动成功才发 CaptureReady；等待文案不代表就绪或业务活动。
- 每次会话自带空转 watcher，计时依据为成功解析的目标背包数据。页面关闭不停止后台会话。
- KCP 单流最多保留 32 MiB / 32768 段，缺口等待最多 30 秒；超限/超时明确报错，禁止跨缺口拼帧。
- 写入失败保持待持久化状态，每秒重试最新快照，停止时再尝试收尾；成功写入后才推进已写版本。
- 窗口订阅状态并在释放时退订，不长期 await 会话；打开即同步已有监控状态。

增删改 Notify 支持按用户要求搁置，本次没有修改 MsgIdFilter 或增加任何增量通知类型。
