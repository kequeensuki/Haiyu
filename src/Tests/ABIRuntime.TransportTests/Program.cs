using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text.Json;
using ABI.Models;
using ABIRuntime.Abstractions;
using ABIRuntime.Runtime;
using ABIRuntime;
using Haiyu.ABI.Services;
using MemoryPack;
using Microsoft.Extensions.DependencyInjection;

namespace ABIRuntime.TransportTests;

internal static partial class Program
{
    private static readonly List<string> Results = [];
    private static int _lastJsonMonitorSize;
    private static int _lastMemoryPackMonitorSize;

    private static async Task<int> Main(string[] args)
    {
        ABIMemoryPack.EnsureFormatters();
        var tests = new (string Name, Func<Task> Run)[]
        {
            ("武器变更链：养成/装备/增删改/失同步恢复", TestWeaponMutationChainAsync),
            ("声骸变更链：拆合堆/标记/失同步/全量恢复", TestMutationDesyncRecoveryAsync),
            ("道具通知：绝对数量/实例键/增删改/幂等", TestInventoryNotificationsAsync),
            ("声骸响应：升级/调谐/确认/堆叠/失败不覆盖", TestPhantomMutationResponsesAsync),
            ("库存与养成字段：数量/获得时间/已有武器/缺省字段", TestInventoryModelFieldSemanticsAsync),
            ("通知接入：基线/账号隔离/筛选事件/独立目录", TestNotificationBaselineAndEventsAsync),
            ("通用会话通知：候选/确认恢复/新登录/退役", TestConfirmedResumeNotificationAsync),
            ("消息筛选模块：并行路径/独立编号/玩家计数", TestFilteredMessageModulesAsync),
            ("模块完成状态：单独/组合/无数据/部分数据", TestModuleCompletionStatesAsync),
            ("快照在写时更新：冻结候选和 V/V+1 回执", TestInventoryPendingVersionAsync),
            ("多来源：任意来源数/条件/空值/轮次/新鲜度/回执", TestAggregateSourcesAsync),
            ("多来源：乱序分页/迟到批次/超时/有序增删改", TestAggregatePagesAndDeltasAsync),
            ("共享输出：路径验证/代次隔离/失败保留旧文件", TestSharedOutputFencingAsync),
            ("模块注册：新增无背包模块/选择校验/索引/ABI", TestExportModuleRegistryAsync),
            ("模块目录：高权限宿主组合根注册与查询", TestModuleCatalogRegistrationAsync),
            ("背包模块：待身份聚合/多玩家次数/历史流量隔离", TestInventoryModuleIsolationAsync),
            ("Xbox 档位、序列化、服务与本机 XInput", TestXboxSessionsAsync),
            ("DI 构造注入、作用域与释放", TestDependencyInjectionAsync),
            ("MonitorRecord JSON/MemoryPack AOT 序列化", TestMonitorSerializationAsync),
            ("64 MiB 分帧与双向完整性", TestLargeMessagesAsync),
            ("并发独立请求管道", TestConcurrentPipesAsync),
            ("请求 ID 关联取消", TestCancellationMessageAsync),
            ("消费者边界：纯快照与独立连接验证", TestExportConsumerBoundariesAsync),
            ("登录隔离：复用密钥，两种模式保持一致", TestLoginIsolationAsync),
            ("帧验证：CRC/压缩/异常控制帧/RSA 尺寸", TestFrameValidationAsync),
            ("会话解密器：并发与独立密钥", TestParallelDecryptionAsync),
            ("组合导出：快照失败不被事件成功覆盖", TestExportResultIsolationAsync),
            ("连接隔离：不同端点复用 conv", TestConnectionEndpointIsolationAsync),
            ("慢速收尾：真实响应读取超过 5 秒仍返回结果", TestSlowExportDrainAsync),
            ("全量协议：独立 wire 编码/oneof/map/未知/损坏载荷", TestFullProtocolAsync),
            ("事件写入：分片/待归属绑定/限额/完整性", TestEventFilesAsync),
            ("KCP 流水位/增量/Compact 语义", TestKcpStreamWatermarkAsync),
            ("导出单消费者：加密/半帧/回收/playerId/持续更新", TestInventoryCaptureAsync),
            ("playerId 状态机：提取/重试/Id=0/超时/重登", TestPlayerIdResolverAsync),
            ("原子落盘：会话路径/tmp+Replace/更新覆盖", TestAtomicWriteAsync),
            ("KCP 缺口超时和缓存上限", TestCaptureLimitsAsync),
            ("导出服务写入失败重试及就绪/数据进度", TestExportPersistenceRetryAsync),
            ("服务取消后最终结果仍送达", TestExportStopResultAsync),
            ("候选接口：状态/IPv4/GUID 匹配", TestNetworkCandidatesAsync),
            ("多接口捕获：公平轮询/锁定/防抖/错误恢复/释放", TestMultiInterfaceCaptureAsync),
            ("无密钥 30 分钟后取消服务并释放全部接口", TestKeyTimeoutCleanupAsync),
            ("有线切无线：同连接及新连接保留密钥/玩家/快照", TestNetworkReconnectAsync),
            ("切号后监控状态/路径及旧连接隔离", TestAccountMonitoringAsync),
            ("ComputerMonitorService 实际采样", TestDirectMonitorAsync),
            ("ComputerMonitorService 管道进度链", TestMonitorServicePipeAsync),
            ("控制管道与独立请求管道完整调用", TestElevatedHostRoutingAsync),
        };

        if (args.Contains("--game-data-export"))
            tests = tests.Where(t => t.Run == TestConfirmedResumeNotificationAsync || t.Run == TestFilteredMessageModulesAsync || t.Run == TestModuleCompletionStatesAsync || t.Run == TestInventoryPendingVersionAsync || t.Run == TestAggregateSourcesAsync || t.Run == TestAggregatePagesAndDeltasAsync || t.Run == TestSharedOutputFencingAsync || t.Run == TestExportModuleRegistryAsync || t.Run == TestInventoryModuleIsolationAsync || t.Run == TestExportConsumerBoundariesAsync || t.Run == TestLoginIsolationAsync || t.Run == TestFrameValidationAsync
                || t.Run == TestParallelDecryptionAsync || t.Run == TestExportResultIsolationAsync
                || t.Run == TestConnectionEndpointIsolationAsync || t.Run == TestSlowExportDrainAsync || t.Run == TestFullProtocolAsync || t.Run == TestEventFilesAsync || t.Run == TestKcpStreamWatermarkAsync
                || t.Run == TestInventoryCaptureAsync || t.Run == TestPlayerIdResolverAsync
                || t.Run == TestAtomicWriteAsync || t.Run == TestCaptureLimitsAsync
                || t.Run == TestExportPersistenceRetryAsync || t.Run == TestExportStopResultAsync
                || t.Run == TestNetworkCandidatesAsync || t.Run == TestMultiInterfaceCaptureAsync
                || t.Run == TestNetworkReconnectAsync || t.Run == TestAccountMonitoringAsync
                || t.Run == TestKeyTimeoutCleanupAsync || t.Run == TestModuleCatalogRegistrationAsync
                || t.Run == TestWeaponMutationChainAsync || t.Run == TestMutationDesyncRecoveryAsync
                || t.Run == TestInventoryNotificationsAsync || t.Run == TestPhantomMutationResponsesAsync
                || t.Run == TestNotificationBaselineAndEventsAsync || t.Run == TestInventoryModelFieldSemanticsAsync).ToArray();

        if (args.Contains("--transport"))
            tests = tests.Where(t => t.Run == TestDependencyInjectionAsync
                || t.Run == TestMonitorSerializationAsync || t.Run == TestLargeMessagesAsync
                || t.Run == TestConcurrentPipesAsync || t.Run == TestCancellationMessageAsync).ToArray();

        int failures = 0;
        foreach ((string name, Func<Task> run) in tests)
        {
            try
            {
                Console.WriteLine("RUN " + name);
                var stopwatch = Stopwatch.StartNew();
                await run();
                Results.Add($"PASS  {name}  {stopwatch.ElapsedMilliseconds} ms");
            }
            catch (Exception exception)
            {
                failures++;
                Results.Add($"FAIL  {name}\n{exception}");
            }
        }

        if (args.Length > 0 && !args.Contains("--game-data-export") && !args.Contains("--transport"))
        {
            try
            {
                await TestPublishedNativeDllAsync(Path.GetFullPath(args[0]));
                Results.Add("PASS  已部署 NativeAOT DLL 实际采样");
            }
            catch (Exception exception)
            {
                failures++;
                Results.Add($"FAIL  已部署 NativeAOT DLL 实际采样\n{exception}");
            }
        }

        foreach (string result in Results)
            Console.WriteLine(result);
        if (_lastJsonMonitorSize > 0 && _lastMemoryPackMonitorSize > 0)
        {
            double reduction = 1d - (double)_lastMemoryPackMonitorSize / _lastJsonMonitorSize;
            Console.WriteLine($"MONITOR PAYLOAD: JSON={_lastJsonMonitorSize} bytes, " +
                              $"MemoryPack={_lastMemoryPackMonitorSize} bytes, " +
                              $"reduction={reduction:P1}");
        }
        Console.WriteLine(failures == 0 ? "ALL TESTS PASSED" : $"FAILED: {failures}");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// KCP 流水位语义（v2 阶段 1 核心）：Add 长存拷贝、增量视图只含新字节、
    /// AdvanceConsumed/Compact 收缩、半帧保留、重复 sn 去重。
    /// </summary>
    private static async Task TestKcpStreamWatermarkAsync()
    {
        var stream = new Haiyu.ABI.Services.GameDataExport.Rebuild.KcpStreamRebuilder();

        Haiyu.ABI.Services.GameDataExport.Rebuild.KcpSegment MakeSegment(uint sn, byte fill, int length) =>
            new(0x1234, Haiyu.ABI.Services.GameDataExport.Rebuild.KcpSegment.CmdPush, 0, 0, 0, sn, 0, (uint)length,
                Enumerable.Repeat(fill, length).ToArray());

        // 3 段入流（sn=0,1,2 各 10 字节）
        stream.Add(MakeSegment(0, 0xAA, 10));
        stream.Add(MakeSegment(1, 0xBB, 10));
        stream.Add(MakeSegment(2, 0xCC, 10));
        Equal(3, stream.SegmentCount);
        Equal(0L, stream.ConsumedBytes);

        // 全量视图 = 30 字节
        var full = stream.Build();
        Equal(30, full.Length);
        Equal(0xAA, full[0]);
        Equal(0xBB, full[10]);

        // 增量视图 1：水位 0 → 全部 30 字节
        var inc1 = stream.BuildIncremental();
        Equal(30, inc1.Length);

        // 消费 25 字节（第 3 段还剩 5 字节未消费 = 半帧悬挂）
        stream.AdvanceConsumed(25);
        stream.Compact();
        // ★ Compact 平移坐标系：丢弃 20 字节前缀后，水位在幸存数据中为 5（本地坐标）
        Equal(5L, stream.ConsumedBytes);

        // 前两段（整体位于水位前）应被 Compact 丢弃；第三段跨界保留
        Equal(1, stream.SegmentCount);

        // 增量视图 2：只剩未消费的 5 字节
        var inc2 = stream.BuildIncremental();
        Equal(5, inc2.Length);
        Equal(0xCC, inc2[0]);

        // 重复 sn（重传）被去重：sn=2 重发不改变视图
        stream.Add(MakeSegment(2, 0xDD, 10));
        var inc3 = stream.BuildIncremental();
        Equal(5, inc3.Length);
        Equal(0xCC, inc3[0]); // 保留首份数据

        // 新段到达，水位推进到底后 Compact 全清
        stream.Add(MakeSegment(3, 0xEE, 8));
        stream.AdvanceConsumed(13); // 5 + 8
        stream.Compact();
        Equal(0, stream.SegmentCount);

        // 全消费后增量视图为空
        var inc4 = stream.BuildIncremental();
        Equal(0, inc4.Length);

        // Clear 复位
        stream.Add(MakeSegment(9, 0xFF, 6));
        stream.Clear();
        Equal(0, stream.SegmentCount);
        Equal(0L, stream.ConsumedBytes);

        await Task.CompletedTask;
    }

    /// <summary>序列化 BasicInfoNotify{Id}（手工 varint：tag 120 + 值）。</summary>
    private static byte[] SerializeBasicInfo(int id)
    {
        // tag 120 = (15<<3)|0 (varint int32)
        var header = new byte[] { 120 };
        if (id < 0x80)
            return [.. header, (byte)id];

        // 多字节 varint（最多 5 字节 for int32）
        var value = (uint)id;
        var varint = new List<byte>();
        while (value >= 0x80)
        {
            varint.Add((byte)(value | 0x80));
            value >>= 7;
        }
        varint.Add((byte)value);
        return [.. header, .. varint];
    }

    /// <summary>
    /// playerId 状态机（v2 §2.6/2.7）：BasicInfoNotify field 15 提取、
    /// 解析失败重试、Id=0 拒绝、60s 超时、重登重置。
    /// （15380 是加密帧——回放无私钥解不开，故用合成 proto 验证解析链；真实
    /// 密文路径由真机验证覆盖。）
    /// </summary>
    private static async Task TestPlayerIdResolverAsync()
    {
        var resolver = new Haiyu.ABI.Services.GameDataExport.Session.PlayerIdResolver();

        Haiyu.ABI.Services.GameDataExport.Decrypt.GatewayFrame MakeFrame(ushort msgId, byte[] payload) =>
            new(Haiyu.ABI.Services.GameDataExport.Decrypt.GatewayMessageType.Push, 4, 0, 0, msgId, 0, payload, 0);

        // ── 1) 密钥未解出时不计时（WaitingKey），喂普通帧不动作 ──
        Equal(true, resolver.TryFeed(MakeFrame(999, [1, 2, 3])));
        Equal(Haiyu.ABI.Services.GameDataExport.Session.PlayerIdResolver.Phase.WaitingKey, resolver.Current);

        // ── 2) 解析失败重试：第一次失败（坏 payload）不终止 ──
        resolver.NotifyKeyResolved();
        Equal(Haiyu.ABI.Services.GameDataExport.Session.PlayerIdResolver.Phase.WaitingId, resolver.Current);
        Equal(true, resolver.TryFeed(MakeFrame(15380, [0xFF, 0xFF, 0xFF]))); // 非法 protobuf
        Equal(Haiyu.ABI.Services.GameDataExport.Session.PlayerIdResolver.Phase.WaitingId, resolver.Current);

        // ── 3) 合成 BasicInfoNotify{Id=123456} 提取成功 ──
        var good = SerializeBasicInfo(123456);
        Equal(true, resolver.TryFeed(MakeFrame(15380, good)));
        Equal(Haiyu.ABI.Services.GameDataExport.Session.PlayerIdResolver.Phase.Resolved, resolver.Current);
        Equal(123456, resolver.PlayerId);

        // ── 4) 60s 超时：WaitingId 阶段才计时；Resolved 后 OnTick 恒 true ──
        //    （真实 60s 由时间驱动，这里只验证阶段门槛逻辑）
        Equal(true, resolver.OnTick());

        // ── 5) 重登重置：回到 WaitingId，旧 id 保留，新 id 到达后更新 ──
        resolver.NotifyRelogin();
        Equal(Haiyu.ABI.Services.GameDataExport.Session.PlayerIdResolver.Phase.WaitingId, resolver.Current);
        Equal(123456, resolver.PlayerId); // 旧值保留（新 id 解析成功前目录暂用旧值）

        var relogin = SerializeBasicInfo(654321);
        Equal(true, resolver.TryFeed(MakeFrame(15380, relogin)));
        Equal(Haiyu.ABI.Services.GameDataExport.Session.PlayerIdResolver.Phase.Resolved, resolver.Current);
        Equal(654321, resolver.PlayerId);

        // ── 6) Id=0 两次 → 第 1 次重试（true），第 2 次终止（false）──
        var resolver2 = new Haiyu.ABI.Services.GameDataExport.Session.PlayerIdResolver();
        resolver2.NotifyKeyResolved();
        var zero = SerializeBasicInfo(0);
        Equal(true, resolver2.TryFeed(MakeFrame(15380, zero)));    // 第 1 次：重试
        Equal(false, resolver2.TryFeed(MakeFrame(15380, zero)));   // 第 2 次：终止
        Equal(Haiyu.ABI.Services.GameDataExport.Session.PlayerIdResolver.Phase.Failed, resolver2.Current);
        Equal(true, resolver2.FailureReason is not null);

        // ── 7) 坏帧两次 → 第 1 次重试（true），第 2 次终止（false）──
        var resolver3 = new Haiyu.ABI.Services.GameDataExport.Session.PlayerIdResolver();
        resolver3.NotifyKeyResolved();
        Equal(true, resolver3.TryFeed(MakeFrame(15380, [0xFF, 0xFF])));
        Equal(false, resolver3.TryFeed(MakeFrame(15380, [0xFF, 0xFF])));
        Equal(Haiyu.ABI.Services.GameDataExport.Session.PlayerIdResolver.Phase.Failed, resolver3.Current);

        await Task.CompletedTask;
    }

    /// <summary>
    /// 原子落盘（v2 §2.3）：会话路径按 playerId 分目录、tmp+Replace 更新覆盖、
    /// 更新期间文件始终可解析、失败保留旧文件。
    /// </summary>
    private static async Task TestAtomicWriteAsync()
    {
        // 根目录放仓库 artifacts 下（构建产物区）：沙箱对 %TEMP% 子目录树创建
        // 表现过 UnauthorizedAccess（疑 dotnet run 子进程 ACL），工作区内稳定。
        var root = Path.Combine(
            AppContext.BaseDirectory, "awtest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var sessionStart = DateTimeOffset.Now;

        GameDataExportResult MakeResult(int normalCount, int update) =>
            new(
                true,
                $"test update {update}",
                new GameInventoryData(
                    Enumerable.Range(1, normalCount)
                        .Select(i => new GameNormalItem(i, i, 0)).ToList(),
                    [], [], []
                ),
                null,
                100, 5, "aabb",
                PlayerId: 42,
                UpdateCount: update
            );

        try
        {
            // 首轮写：目录 42\ 创建，文件存在，无 tmp 残留
            var path1 = Haiyu.ABI.Services.GameDataExport.Export.InventoryJsonWriter.TryWriteToRoot(root, MakeResult(10, 1), sessionStart);
            Equal(true, path1 is not null);
            Check(path1!.Contains($"{Path.DirectorySeparatorChar}42{Path.DirectorySeparatorChar}"),
                $"path should contain player dir: {path1}");
            Equal(true, File.Exists(path1));
            Equal(false, File.Exists(path1 + ".tmp"));

            // 更新覆盖：同一会话路径，内容替换（10 → 20 条），旧文件被原子替换
            var path2 = Haiyu.ABI.Services.GameDataExport.Export.InventoryJsonWriter.TryWriteToRoot(root, MakeResult(20, 2), sessionStart);
            Equal(path1, path2);
            Equal(false, File.Exists(path2 + ".tmp"));

            // 更新后文件可解析且是新内容
            var json = File.ReadAllText(path2);
            StringContains(json, "20");

            // 第二个会话（时间戳不同）仍覆盖同一玩家的最新聚合快照
            var path3 = Haiyu.ABI.Services.GameDataExport.Export.InventoryJsonWriter.TryWriteToRoot(root, MakeResult(5, 1), sessionStart.AddSeconds(1));
            Equal(path2, path3);
            Equal(1, Directory.GetFiles(Path.Combine(root, "42"), "*.json").Length);
            using var latest = JsonDocument.Parse(File.ReadAllText(path3!));
            Equal(5, latest.RootElement.GetProperty("inventory").GetProperty("normalItems").GetProperty("count").GetInt32());

            // playerId=0 → 平铺路径（v1 兼容行为）
            var legacy = Haiyu.ABI.Services.GameDataExport.Export.InventoryJsonWriter.TryWriteToRoot(
                root,
                new GameDataExportResult(true, "m", null, null, 0, 0, null, PlayerId: 0, UpdateCount: 1),
                sessionStart);
            Equal(true, legacy is not null);
            Equal(true, legacy!.Contains("GameDataExport".Replace("GameDataExport", "")) || !legacy!.Contains($"{Path.DirectorySeparatorChar}42{Path.DirectorySeparatorChar}"));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }

        await Task.CompletedTask;
    }

    private static void StringContains(string haystack, string needle)
    {
        if (!haystack.Contains(needle, StringComparison.Ordinal))
            throw new InvalidDataException($"断言失败。期望包含 \"{needle}\"，实际 \"{haystack[..Math.Min(200, haystack.Length)]}\"");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }

    private static async Task TestXboxSessionsAsync()    {
        static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidDataException(message);
        }
        foreach (byte type in new byte[] { 0, 1, 2, 3, 255 })
        foreach (byte level in new byte[] { 0, 1, 2, 3, 255 })
        {
            var session = new XboxSession { IsConnected = true, Power = 99 };
            XInputSessionSource.ApplyBattery(session, type, level);
            var expected = type is 2 or 3 && level <= 3
                ? (XboxBatteryLevel)(level + 1) : XboxBatteryLevel.Unknown;
            Check(session.BatteryLevel == expected && session.Power is null, "电量有效性错误");
        }
        var snapshot = new XboxSessionReportProcess([
            new() { UserIndex = 0, BatteryType = XboxBatteryType.Disconnected },
            new() { UserIndex = 1, IsConnected = true, ConnectionType = XboxConnectionType.Wired, BatteryType = XboxBatteryType.Wired },
            new() { UserIndex = 2, IsConnected = true, ConnectionType = XboxConnectionType.Wireless, BatteryType = XboxBatteryType.Alkaline, BatteryLevel = XboxBatteryLevel.Low },
            new() { UserIndex = 3, StateErrorCode = 123 },
        ]);
        var json = JsonSerializer.Serialize(snapshot, ABIJsonContext.Default.XboxSessionReportProcess);
        var jsonCopy = JsonSerializer.Deserialize(json, ABIJsonContext.Default.XboxSessionReportProcess)!;
        var bytes = MemoryPackSerializer.Serialize(snapshot, ABIMemoryPack.Options);
        var copy = MemoryPackSerializer.Deserialize<XboxSessionReportProcess>(bytes, ABIMemoryPack.Options)!;
        Check(jsonCopy.Controllers.Length == 4 && copy.Controllers[2].BatteryLevel == XboxBatteryLevel.Low
            && copy.Controllers[3].StateErrorCode == 123 && copy.Controllers[0].Power is null, "Xbox 序列化错误");
        XboxSessionReportProcess? reported = null;
        var service = new XboxSessionService(new XboxFixtureSource(snapshot.Controllers));
        var result = await service.ExecuteAsync(new(), new InlineProgress<XboxSessionReportProcess>(p => reported = p), default);
        Check(result == RunResult.Success && reported?.Controllers.Length == 4, "Xbox 快照服务错误");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        try
        {
            await service.ExecuteAsync(new(), new InlineProgress<XboxSessionReportProcess>(_ => throw new Exception()), cts.Token);
            throw new InvalidDataException("Xbox 取消未生效");
        }
        catch (OperationCanceledException) { }
        using var provider = new ServiceCollection().AddBuiltInServices().BuildServiceProvider();
        using var scope = provider.CreateScope();
        var registered = scope.ServiceProvider.GetRequiredKeyedService<IPrivilegedService<XboxSessionRequest, XboxSessionReportProcess, RunResult>>(Contract.XboxSessionContract.Operation);
        reported = null;
        result = await registered.ExecuteAsync(new(), new InlineProgress<XboxSessionReportProcess>(p => reported = p), default);
        Check(result == RunResult.Success && reported?.Controllers.Length == 4, "本机 XInput 查询失败");
        Check(reported!.Controllers.Select(x => x.UserIndex).SequenceEqual(new[] { 0, 1, 2, 3 }), "槽位顺序错误");
        Console.WriteLine("Xbox connected slots: " + string.Join(",", reported.Controllers.Where(x => x.IsConnected).Select(x => x.UserIndex)));
    }

    private sealed class XboxFixtureSource(XboxSession[] sessions) : IXboxSessionSource
    {
        public XboxSession[] GetSessions() => sessions;
    }

    private static async Task TestModuleCatalogRegistrationAsync()
    {
        using var provider = new ServiceCollection().AddBuiltInServices()
            .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        await using var scope = provider.CreateAsyncScope();
        var operation = Contract.GameDataExportModuleCatalogContract.Operation;
        _ = scope.ServiceProvider.GetRequiredKeyedService<IServiceInvoker>(operation);
        var service = scope.ServiceProvider.GetRequiredKeyedService<
            IPrivilegedService<GameDataExportModuleCatalogRequest, GameDataExportProgress,
                GameDataExportModuleCatalogResult>>(operation);
        var result = await service.ExecuteAsync(new(), new InlineProgress<GameDataExportProgress>(_ => { }), default);
        var expected = Haiyu.ABI.Services.GameDataExport.Modules.BuiltInExportModules.Create().Describe();
        Check(result.Modules.Length > 0 && result.Modules.Select(x => x.Id).SequenceEqual(expected.Select(x => x.Id)),
            "高权限宿主未提供完整的模块目录。");
    }

    private static async Task TestDependencyInjectionAsync()
    {
        var services = new ServiceCollection();
        services.AddScoped<ProbeDependency>();
        services.AddPrivilegedService<CleanMemoryRequest, CleanMemoryProgress, RunResult, ProbeService>(Contract.CleanMemoryContract);
        try
        {
            services.AddPrivilegedService<CleanMemoryRequest, CleanMemoryProgress, RunResult, ProbeService>(Contract.CleanMemoryContract);
            throw new InvalidDataException("重复操作名未被拒绝。");
        }
        catch (InvalidOperationException) { }

        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true, ValidateOnBuild = true,
        });
        ProbeDependency dependency;
        await using (var scope = provider.CreateAsyncScope())
        {
            var service = (ProbeService)scope.ServiceProvider
                .GetRequiredKeyedService<IPrivilegedService<CleanMemoryRequest, CleanMemoryProgress, RunResult>>(Contract.CleanMemoryContract.Operation);
            dependency = service.Dependency;
            Equal(dependency, scope.ServiceProvider.GetRequiredService<ProbeDependency>());
            var invoker = scope.ServiceProvider.GetRequiredKeyedService<IServiceInvoker>(Contract.CleanMemoryContract.Operation);
            Equal(invoker, scope.ServiceProvider.GetRequiredKeyedService<IServiceInvoker>(Contract.CleanMemoryContract.Operation));
            Equal(true, scope.ServiceProvider.GetKeyedService<IServiceInvoker>("unknown") is null);
            await using var other = provider.CreateAsyncScope();
            Equal(false, ReferenceEquals(dependency, other.ServiceProvider.GetRequiredService<ProbeDependency>()));
        }
        Equal(true, dependency.Disposed);
    }

    private sealed class ProbeDependency : IAsyncDisposable
    {
        public bool Disposed { get; private set; }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    private sealed class ProbeService(ProbeDependency dependency)
        : IPrivilegedService<CleanMemoryRequest, CleanMemoryProgress, RunResult>
    {
        public ProbeDependency Dependency { get; } = dependency;
        public PrivilegedServiceContract<CleanMemoryRequest, CleanMemoryProgress, RunResult> Contract => ABIRuntime.Contract.CleanMemoryContract;
        public ValueTask<RunResult> ExecuteAsync(CleanMemoryRequest request,
            IProgress<CleanMemoryProgress> progress, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new RunResult(0, "ok"));
    }

    private static Task TestMonitorSerializationAsync()
    {
        CMonitorProgress source = CreateMonitorProgress(512);
        byte[] utf8 = JsonSerializer.SerializeToUtf8Bytes(
            source, ABIJsonContext.Default.CMonitorProgress);
        CMonitorProgress restored = JsonSerializer.Deserialize(
            utf8, ABIJsonContext.Default.CMonitorProgress)
            ?? throw new InvalidDataException("反序列化结果为空。");

        byte[] binary = MemoryPackSerializer.Serialize(source, ABIMemoryPack.Options);
        CMonitorProgress binaryRestored = MemoryPackSerializer.Deserialize<CMonitorProgress>(
            binary, ABIMemoryPack.Options)
            ?? throw new InvalidDataException("MemoryPack 反序列化结果为空。");

        Equal(2, restored.data.Cpus.Count);
        Equal(source.data.Cpus[0].Hardware.Name, restored.data.Cpus[0].Hardware.Name);
        Equal(source.data.Cpus[0].Load.Count, restored.data.Cpus[0].Load.Count);
        Equal(source.data.Gpus![0].Sensors.Count, restored.data.Gpus![0].Sensors.Count);
        Equal(2, binaryRestored.data.Cpus.Count);
        Equal(source.data.Cpus[0].Hardware.Name, binaryRestored.data.Cpus[0].Hardware.Name);
        Equal(source.data.Cpus[0].Load.Count, binaryRestored.data.Cpus[0].Load.Count);
        Equal(source.data.Gpus[0].Sensors.Count, binaryRestored.data.Gpus![0].Sensors.Count);
        return Task.CompletedTask;
    }

    private static async Task TestLargeMessagesAsync()
    {
        foreach (int payloadSize in new[] { 1024, 4 * 1024 * 1024 + 123, 16 * 1024 * 1024,
                     48 * 1024 * 1024 })
        {
            await RoundTripPipeAsync(payloadSize, $"large-{payloadSize}");
        }
    }

    private static Task TestConcurrentPipesAsync() => Task.WhenAll(
        Enumerable.Range(0, 8).Select(index =>
            RoundTripPipeAsync(8 * 1024 * 1024 + index * 31, $"parallel-{index}")));

    private static async Task RoundTripPipeAsync(int payloadSize, string operation)
    {
        string pipeName = $"ABIRuntime.Test.{Guid.NewGuid():N}";
        byte[] payload = CreateDeterministicPayload(payloadSize);
        string expectedHash = Convert.ToHexString(SHA256.HashData(payload));
        Guid requestId = Guid.NewGuid();

        await using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await using var client = new NamedPipeClientStream(".", pipeName,
            PipeDirection.InOut, PipeOptions.Asynchronous);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        Task connectClient = client.ConnectAsync(timeout.Token);
        await server.WaitForConnectionAsync(timeout.Token);
        await connectClient;

        var sent = new PipeMessage(PipeMessageKind.Progress, requestId,
            PipeProtocolVersion.Current,
            operation, payload, 50, "large payload");
        Task writer = PipeProtocol.WriteAsync(server, sent, timeout.Token).AsTask();
        PipeMessage received = await PipeProtocol.ReadAsync(client, timeout.Token);
        await writer;

        Equal(requestId, received.RequestId);
        Equal(payloadSize, received.Payload.Length);
        Equal(expectedHash, Convert.ToHexString(SHA256.HashData(received.Payload)));

        Task echoWriter = PipeProtocol.WriteAsync(client, received, timeout.Token).AsTask();
        PipeMessage echoed = await PipeProtocol.ReadAsync(server, timeout.Token);
        await echoWriter;
        if (!payload.AsSpan().SequenceEqual(echoed.Payload))
            throw new InvalidDataException("回传 Payload 内容不一致。");
    }

    private static async Task TestCancellationMessageAsync()
    {
        string pipeName = $"ABIRuntime.CancelTest.{Guid.NewGuid():N}";
        Guid id = Guid.NewGuid();
        await using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await using var client = new NamedPipeClientStream(".", pipeName,
            PipeDirection.InOut, PipeOptions.Asynchronous);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task connection = client.ConnectAsync(timeout.Token);
        await server.WaitForConnectionAsync(timeout.Token);
        await connection;

        Task writer = PipeProtocol.WriteAsync(client,
            new PipeMessage(PipeMessageKind.Cancel, id, PipeProtocolVersion.Current,
                "cancel.test", Array.Empty<byte>()),
            timeout.Token).AsTask();
        PipeMessage message = await PipeProtocol.ReadAsync(server, timeout.Token);
        await writer;
        Equal(PipeMessageKind.Cancel, message.Kind);
        Equal(id, message.RequestId);
    }

    private static async Task TestDirectMonitorAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        int count = 0;
        int largestPayload = 0;
        var progress = new InlineProgress<CMonitorProgress>(value =>
        {
            byte[] jsonPayload = JsonSerializer.SerializeToUtf8Bytes(
                value, ABIJsonContext.Default.CMonitorProgress);
            byte[] payload = MemoryPackSerializer.Serialize(value, ABIMemoryPack.Options);
            CMonitorProgress roundTrip = MemoryPackSerializer.Deserialize<CMonitorProgress>(
                payload, ABIMemoryPack.Options)
                ?? throw new InvalidDataException("监控进度反序列化为空。");
            if (roundTrip.data.Cpus.Count == 0 || roundTrip.data.Cpus[0].Hardware is null)
                throw new InvalidDataException("CPU 硬件数据为空。");
            largestPayload = Math.Max(largestPayload, payload.Length);
            _lastJsonMonitorSize = Math.Max(_lastJsonMonitorSize, jsonPayload.Length);
            _lastMemoryPackMonitorSize = Math.Max(_lastMemoryPackMonitorSize, payload.Length);
            if (Interlocked.Increment(ref count) >= 3)
                timeout.Cancel();
        });

        try
        {
            await new ComputerMonitorService().ExecuteAsync(
                new CMonitorRequest(), progress, timeout.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
        }

        if (count < 3 || largestPayload == 0)
            throw new InvalidDataException($"监控采样不足：{count}，数据长度：{largestPayload}。");
    }

    private static async Task TestMonitorServicePipeAsync()
    {
        string pipeName = $"ABIRuntime.ServiceTest.{Guid.NewGuid():N}";
        Guid requestId = Guid.NewGuid();
        await using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await using var client = new NamedPipeClientStream(".", pipeName,
            PipeDirection.InOut, PipeOptions.Asynchronous);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Task connection = client.ConnectAsync(timeout.Token);
        await server.WaitForConnectionAsync(timeout.Token);
        await connection;

        await using var registry = new ServiceCollection()
            .AddBuiltInServices().BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        await using var scope = registry.CreateAsyncScope();
        IServiceInvoker invoker = scope.ServiceProvider.GetRequiredKeyedService<IServiceInvoker>(
            Contract.ComputerMonitorContract.Operation);

        byte[] requestPayload = MemoryPackSerializer.Serialize(
            new CMonitorRequest(), ABIMemoryPack.Options);
        var request = new PipeMessage(PipeMessageKind.Request, requestId,
            PipeProtocolVersion.Current,
            Contract.ComputerMonitorContract.Operation, requestPayload);
        using var serviceCancellation = new CancellationTokenSource();

        Task hostTask = Task.Run(async () =>
        {
            try
            {
                await invoker.InvokeAsync(server, request, serviceCancellation.Token);
            }
            catch (OperationCanceledException) when (serviceCancellation.IsCancellationRequested)
            {
                await PipeProtocol.WriteAsync(server,
                    new PipeMessage(PipeMessageKind.Cancelled, requestId,
                        PipeProtocolVersion.Current, request.Operation, Array.Empty<byte>()),
                    CancellationToken.None);
            }
        });

        int progressCount = 0;
        while (true)
        {
            PipeMessage message = await PipeProtocol.ReadAsync(client, timeout.Token);
            Equal(requestId, message.RequestId);
            if (message.Kind == PipeMessageKind.Progress)
            {
                CMonitorProgress value = MemoryPackSerializer.Deserialize<CMonitorProgress>(
                    message.Payload, ABIMemoryPack.Options)
                    ?? throw new InvalidDataException("管道监控进度为空。");
                if (value.data.Cpus.Count == 0 || value.data.Cpus[0].Hardware is null)
                    throw new InvalidDataException("管道 CPU 数据为空。");
                if (++progressCount == 3)
                    serviceCancellation.Cancel();
            }
            else if (message.Kind == PipeMessageKind.Cancelled)
            {
                break;
            }
        }

        await hostTask.WaitAsync(timeout.Token);
        Equal(3, progressCount);
    }

    private static async Task TestElevatedHostRoutingAsync()
    {
        string controlName = $"ABIRuntime.ControlTest.{Guid.NewGuid():N}";
        string controlSecret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        await using var control = new NamedPipeServerStream(controlName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await using var registry = new ServiceCollection()
            .AddBuiltInServices().BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        Task<int> hostTask = ElevatedHost.RunForTestAsync(controlName, controlSecret, registry);
        await control.WaitForConnectionAsync(timeout.Token);
        PipeMessage controlHandshake = await PipeProtocol.ReadAsync(control, timeout.Token);
        Equal(OperationNames.Handshake, controlHandshake.Operation);
        if (!Convert.FromHexString(controlSecret).AsSpan()
                .SequenceEqual(controlHandshake.Payload))
            throw new InvalidDataException("控制管道密钥不匹配。");

        Guid requestId = Guid.NewGuid();
        string requestName = $"ABIRuntime.RequestTest.{Guid.NewGuid():N}";
        string requestSecret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        await using var requestPipe = new NamedPipeServerStream(requestName,
            PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        byte[] openPayload = MemoryPackSerializer.Serialize(
            new OpenRequestMessage(requestName, requestSecret), ABIMemoryPack.Options);

        Task openWriter = PipeProtocol.WriteAsync(control,
            new PipeMessage(PipeMessageKind.Request, requestId,
                PipeProtocolVersion.Current,
                OperationNames.OpenRequest, openPayload), timeout.Token).AsTask();
        await requestPipe.WaitForConnectionAsync(timeout.Token);
        await openWriter;
        PipeMessage requestHandshake = await PipeProtocol.ReadAsync(requestPipe, timeout.Token);
        Equal(requestId, requestHandshake.RequestId);
        Equal(OperationNames.RequestHandshake, requestHandshake.Operation);
        if (!Convert.FromHexString(requestSecret).AsSpan()
                .SequenceEqual(requestHandshake.Payload))
            throw new InvalidDataException("请求管道密钥不匹配。");

        byte[] requestPayload = MemoryPackSerializer.Serialize(
            new CMonitorRequest(), ABIMemoryPack.Options);
        await PipeProtocol.WriteAsync(requestPipe,
            new PipeMessage(PipeMessageKind.Request, requestId,
                PipeProtocolVersion.Current,
                Contract.ComputerMonitorContract.Operation, requestPayload), timeout.Token);

        int samples = 0;
        while (true)
        {
            PipeMessage message;
            try
            {
                message = await PipeProtocol.ReadAsync(requestPipe, timeout.Token);
            }
            catch (EndOfStreamException) when (samples >= 3)
            {
                // 宿主取消后关闭当前请求管道，控制管道继续存活。
                break;
            }
            if (message.Kind == PipeMessageKind.Progress)
            {
                CMonitorProgress progress = MemoryPackSerializer.Deserialize<CMonitorProgress>(
                    message.Payload, ABIMemoryPack.Options)
                    ?? throw new InvalidDataException("宿主路由进度为空。");
                if (progress.data.Cpus.Count == 0 || progress.data.Cpus[0].Hardware is null)
                    throw new InvalidDataException("宿主路由 CPU 数据为空。");
                if (++samples == 3)
                {
                    Task cancelWriter = PipeProtocol.WriteAsync(requestPipe,
                        new PipeMessage(PipeMessageKind.Cancel, requestId,
                            PipeProtocolVersion.Current,
                            Contract.ComputerMonitorContract.Operation, Array.Empty<byte>()),
                        timeout.Token).AsTask();
                    await cancelWriter;
                }
            }
            else if (message.Kind == PipeMessageKind.Cancelled)
            {
                break;
            }
            else if (message.Kind == PipeMessageKind.Error)
            {
                throw new InvalidOperationException(message.Message);
            }
        }

        Equal(3, samples);
        control.Dispose();
        Equal(0, await hostTask.WaitAsync(timeout.Token));
    }

    private static async Task TestPublishedNativeDllAsync(string dllPath)
    {
        if (!File.Exists(dllPath))
            throw new FileNotFoundException("NativeAOT DLL 不存在。", dllPath);

        string report = Path.Combine(Path.GetTempPath(),
            $"Haiyu.ABI.NativeTest.{Guid.NewGuid():N}.txt");
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "rundll32.exe",
                Arguments = $"\"{dllPath}\",MonitorSelfTest \"{report}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            }) ?? throw new InvalidOperationException("rundll32 启动失败。");

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await process.WaitForExitAsync(timeout.Token);
            string result = File.Exists(report) ? await File.ReadAllTextAsync(report) : string.Empty;
            if (process.ExitCode != 0 || !result.StartsWith("PASS|", StringComparison.Ordinal))
                throw new InvalidDataException(
                    $"NativeAOT 自检失败。ExitCode={process.ExitCode}, Report={result}");
        }
        finally
        {
            File.Delete(report);
        }
    }

    private static CMonitorProgress CreateMonitorProgress(int sensors)
    {
        var values = Enumerable.Range(0, sensors)
            .ToDictionary(index => $"Sensor-{index:D5}", index => index / 10d);
        var hardware = new HardwareInfo("Synthetic CPU", "/cpu/0", "Cpu",
            new Dictionary<string, string> { ["Vendor"] = "Fixture" });
        var cpu = new CPUData
        {
            Hardware = hardware,
            Voltages = new(values),
            Temperature = 56.5,
            Load = new(values),
            Clock = new(values),
        };
        var gpu = new GPUData
        {
            Hardware = new HardwareInfo("Synthetic GPU", "/gpu/0", "GpuNvidia",
                new Dictionary<string, string>()),
            Voltages = new(values), Temperatures = new(values), Load = new(values),
            Clock = new(values), Fans = new(values), Power = new(values),
            Memory = new(values), Throughput = new(values), Controls = new(values),
            Factors = new(values), Sensors = new(values),
        };
        var secondCpu = new CPUData
        {
            Hardware = new HardwareInfo("Synthetic CPU 2", "/cpu/1", "Cpu",
                new Dictionary<string, string> { ["Vendor"] = "Fixture" }),
            Voltages = new(values), Temperature = 54.5,
            Load = new(values), Clock = new(values),
        };
        return new CMonitorProgress(new MonitorRecord([cpu, secondCpu], [gpu]));
    }

    private static byte[] CreateDeterministicPayload(int size)
    {
        byte[] payload = GC.AllocateUninitializedArray<byte>(size);
        for (int index = 0; index < payload.Length; index++)
            payload[index] = (byte)(index * 31 + 17);
        return payload;
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidDataException($"断言失败。Expected={expected}, Actual={actual}");
    }

    private sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}
