using System.Buffers.Binary;
using System.Text;
using Haiyu.ABI.Services.GameDataExport.Capture;
using System.Text.Json;
using ABI.Models;
using Haiyu.ABI.Protocol;
using Haiyu.ABI.Services.GameDataExport;
using Haiyu.ABI.Services.GameDataExport.Aggregation;
using Haiyu.ABI.Services.GameDataExport.Decrypt;
using Haiyu.ABI.Services.GameDataExport.Modules;
using Haiyu.ABI.Services.GameDataExport.Protocol;
using Haiyu.ABI.Services.GameDataExport.Session;
using Haiyu.ABI.Services.GameDataExport.Storage;
using MemoryPack;

namespace ABIRuntime.TransportTests;

internal static partial class Program
{
    private sealed record Rows(int[] Values);
    private static Task TestAggregateSourcesAsync()
    {
        var clock = new ExportTestClock();
        foreach (var count in new[] { 1, 2, 7 })
        {
            var state = new AggregateState<Rows>("epoch", Enumerable.Range(0, count).Select(i => new SourceDefinition(i.ToString())), r => r.Values.Length);
            for (var i = 0; i < count; i++)
            {
                Equal(false, state.Ready);
                state.Source(i.ToString()).Full(new([])); // 合法空数据也是已收到的来源。
            }
            Equal(true, state.Ready);
            var snapshot = state.Build()!;
            Equal(count, snapshot.Values.Count);
            Equal(true, state.Acknowledge(snapshot));
            Equal(true, state.Build() is null);
            state.Source("0").Full(new([4]));
            var updated = state.Build()!;
            Equal(0, snapshot.Values["0"].Values.Length);
            Equal(4, updated.Values["0"].Values[0]);
            Equal(false, state.Acknowledge(snapshot with { Epoch = "another-login", Revision = 100 }));
            Equal(true, state.Acknowledge(updated));
            Equal(false, state.Acknowledge(snapshot));
        }
        var conditional = new AggregateState<Rows>("epoch", [new("items"), new("properties", SourceRequirement.Unknown), new("optional", SourceRequirement.Optional)], r => r.Values.Length);
        conditional.Source("items").Full(new([3]));
        Equal(false, conditional.Ready);
        conditional.Source("properties").Requirement = SourceRequirement.Optional;
        Equal(true, conditional.Ready);
        var candidate = conditional.Build()!;
        Equal("Missing", candidate.Sources.Single(s => s.SourceId == "optional").State);
        conditional.Source("optional").Full(new([7]));
        var next = conditional.Build()!;
        conditional.Acknowledge(candidate);
        Equal(true, next.Revision > conditional.PersistedRevision); // V 回执不能抹掉已生成的 V+1。
        var strict = new AggregateState<Rows>("epoch", [new("a"), new("b")], r => r.Values.Length, SnapshotConsistency.StrictBatch, clock);
        strict.Source("a").Full(new([1]), 1, "round-1");
        strict.Source("b").Full(new([2]), 1, "round-2");
        Equal(false, strict.Ready);
        strict.Source("a").Full(new([3]), 2, "round-2");
        Equal(true, strict.Ready);
        strict.BeginRound("round-3");
        Equal(false, strict.Ready);
        var fresh = new AggregateState<Rows>("epoch", [new("a", MaxAge: TimeSpan.FromSeconds(5))], r => r.Values.Length, clock: clock);
        fresh.Source("a").Full(new([1]));
        clock.Advance(TimeSpan.FromSeconds(6));
        Equal(false, fresh.Ready);
        Equal("Stale", fresh.Progress()[0].State);
        return Task.CompletedTask;
    }
    private static Task TestAggregatePagesAndDeltasAsync()
    {
        var clock = new ExportTestClock();
        var source = new SourceState<Rows>(new("paged", MaxEntries: 8, MaxPages: 4, AssemblyTimeout: TimeSpan.FromSeconds(5)), r => r.Values.Length, clock);
        Rows Combine(IReadOnlyList<Rows> pages) => new(pages.SelectMany(p => p.Values).ToArray());
        source.Full(new([99]), 1);
        Equal(false, source.Page("batch-2", 1, null, true, new([2]), Combine, 2));
        Equal(99, source.Value!.Values.Single());
        Equal(false, source.Page("batch-old", 0, 2, false, new([9]), Combine, 1));
        Equal(false, source.Page("batch-2", 1, 2, true, new([2]), Combine, 2));
        Equal(true, source.Page("batch-2", 0, 2, false, new([1]), Combine, 2));
        Equal(true, source.Value!.Values.SequenceEqual(new[] { 1, 2 }));
        Equal(false, source.Page("batch-4", 0, 2, false, new([4]), Combine, 4));
        Equal(false, source.Page("batch-3", 1, 2, true, new([3]), Combine, 3));
        Equal(true, source.Page("batch-4", 1, 2, true, new([5]), Combine, 4));
        Equal(true, source.Value!.Values.SequenceEqual(new[] { 4, 5 }));
        source.Page("incomplete", 0, null, false, new([6]), Combine);
        clock.Advance(TimeSpan.FromSeconds(6));
        Equal("page_assembly_timeout", source.Progress().Error);
        Equal(true, source.Page("after-timeout", 0, 1, true, new([7]), Combine, 5));
        var bounded = new SourceState<Rows>(new("bounded", MaxEntries: 2), r => r.Values.Length, clock);
        bounded.Page("first", 0, 2, false, new([1, 2]), Combine, 1);
        Equal(false, bounded.Page("first", 1, 2, true, new([3, 4]), Combine, 1));
        Equal(true, bounded.Value is null);
        // 溢出的页没有留在缓冲里；相同页号的有效重试可正常完成。
        Equal(true, bounded.Page("first", 1, 2, true, new([]), Combine, 1));
        Equal(2, bounded.Value!.Values.Length);
        var conflict = new SourceState<Rows>(new("conflict"), r => r.Values.Length, clock);
        Equal(false, conflict.Page("batch", 0, 2, true, new([1]), Combine, 1));
        Equal("page_end_conflict", conflict.Progress().Error);
        var delta = new SourceState<Rows>(new("delta", VersionedDeltas: true, MaxPendingDeltas: 3), r => r.Values.Length, clock);
        Equal(false, delta.Delta(12, rows => new(rows.Values.Where(v => v != 1).ToArray())));
        Equal("AwaitingBaseline", delta.Progress().State);
        delta.Full(new([1, 2]), 10);
        Equal(false, delta.Available); // 11 缺口存在，不能猜测丢弃或重排。
        Equal(true, delta.Delta(11, rows => new([.. rows.Values, 3])));
        Equal(true, delta.Value!.Values.SequenceEqual(new[] { 2, 3 }));
        Equal(12L, delta.ServerVersion!.Value);
        Equal(false, delta.Delta(12, _ => new([99])));
        Equal(false, delta.Full(new([1]), 9));
        var unversioned = new SourceState<Rows>(new("unversioned"), r => r.Values.Length, clock);
        unversioned.Full(new([1]));
        Equal(false, unversioned.Delta(null, _ => new([2])));
        Equal(false, unversioned.Available);
        Equal(1, unversioned.Value!.Values.Single());
        return Task.CompletedTask;
    }
    private static async Task TestSharedOutputFencingAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "haiyu-store-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var store = new JsonExportStore(root);
            try { store.Resolve("../outside.json"); throw new Exception("路径越界未拒绝"); } catch (IOException) { }
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            store.Enqueue("blocker", "blocker", "test", 1, [], async _ => { entered.SetResult(); await release.Task; return "blocker"; }, _ => { }, false);
            await entered.Task;
            var receipts = new List<OutputReceipt>();
            store.Claim("player/snapshot.json", "old-login");
            store.Snapshot("any-module", "player/snapshot.json", "old-login", 4, Encoding.UTF8.GetBytes("{\"value\":4}"), receipts.Add);
            store.Claim("player/snapshot.json", "new-login");
            store.Snapshot("any-module", "player/snapshot.json", "new-login", 1, Encoding.UTF8.GetBytes("{\"value\":1}"), receipts.Add);
            release.SetResult();
            await store.FlushAsync();
            Equal(true, receipts[0].Superseded);
            Equal(1, JsonDocument.Parse(File.ReadAllText(store.Resolve("player/snapshot.json"))).RootElement.GetProperty("value").GetInt32());
            Equal(true, receipts[1].Path is not null);
            var before = File.ReadAllText(store.Resolve("player/snapshot.json"));
            store.Snapshot("any-module", "player/snapshot.json", "new-login", 2, [123], receipts.Add, () => throw new IOException("disk unavailable"));
            await store.FlushAsync();
            Equal(before, File.ReadAllText(store.Resolve("player/snapshot.json")));
            Equal("disk unavailable", receipts[^1].Error);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private sealed class CountingModule(ExportCaptureContext context, GameDataExportModuleDescriptor descriptor) : IGameDataExportModule
    {
        private long _count;
        private string? _error;
        private GameDataExportArtifact? _artifact;
        private GameDataExportModuleState _state = GameDataExportModuleState.Running;
        public GameDataExportModuleDescriptor Descriptor => descriptor;
        public IReadOnlySet<ushort> MessageIds { get; } = new HashSet<ushort> { WavesMessageIds.AddUnlockedGuideNotify };
        public void Consume(MessageObservation message) { _count++; }
        public void OnSessionChanged(ExportSessionChange change) { }
        public void Checkpoint(bool stopping = false) { }
        public GameDataExportModuleResult GetProgress() => new(descriptor.Id, descriptor.DisplayName, _state, _error, [new("messages", _count)], [], _artifact is null ? [] : [_artifact]);
        public async ValueTask<GameDataExportModuleResult> CompleteAsync(ExportStopContext stop)
        {
            var bytes = Encoding.UTF8.GetBytes("{\"messages\":" + _count + "}");
            context.Output.Snapshot(descriptor.Id, "count.json", context.CaptureId, 1, bytes, receipt =>
            {
                _error = receipt.Error;
                if (receipt.Path is not null) _artifact = new(descriptor.Id, null, context.CaptureId, receipt.Path, "json", 1, "EventCount", []);
            });
            await context.Output.FlushAsync();
            if (_error is not null) throw new IOException(_error);
            _state = GameDataExportModuleState.Completed;
            return GetProgress();
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private static async Task TestExportModuleRegistryAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "haiyu-modules-" + Guid.NewGuid().ToString("N"));
        try
        {
            var definition = new GameDataExportModuleDescriptor("test-downlink", "test", "Test", true, false);
            var registry = new ExportModuleRegistry([new(definition, context => new CountingModule(context, definition))]);
            var context = new ExportCaptureContext(new JsonExportStore(root));
            foreach (var invalid in new[] { Array.Empty<string>(), new[] { "unknown" }, new[] { definition.Id, definition.Id } })
            {
                try { registry.Create(invalid, context); throw new Exception("非法模块请求未拒绝"); } catch (ArgumentException) { }
            }
            await using var coordinator = new ExportCoordinator(context, registry, [definition.Id]);
            var decoder = new ProtocolJsonDecoder(ProtocolCatalog.Default.Value);
            var frame = new GatewayFrame(GatewayMessageType.Push, 0, 0, 0, WavesMessageIds.AddUnlockedGuideNotify, 0, []);
            coordinator.Observe(new(frame, [], null, "session", 1, null, decoder.Decode(frame.MsgId, [])));
            coordinator.Observe(new(frame with { MsgId = WavesMessageIds.WeaponItemResponse }, [], null, "session", 1, null, null));
            var modules = await coordinator.FinishAsync(new(null, false));
            Equal(1L, modules.Single().Counters.Single().Value);
            Equal(true, coordinator.LegacyResult(modules).Success);
            using var index = JsonDocument.Parse(File.ReadAllText(context.IndexPath));
            Equal("completed", index.RootElement.GetProperty("state").GetString());
            var round = MemoryPackSerializer.Deserialize<GameDataExportResult>(MemoryPackSerializer.Serialize(coordinator.LegacyResult(modules), ABIMemoryPack.Options), ABIMemoryPack.Options)!;
            Equal(definition.Id, round.Modules!.Single().ModuleId);
            // 通过真实捕获/解密服务注入新注册模块，证明它无需背包模块参与。
            using var peer = new ExportPeer();
            var packets = new Queue<UdpFrame>();
            var handshake = new byte[13]; handshake[0] = 0xEE;
            BinaryPrimitives.WriteUInt32LittleEndian(handshake.AsSpan(1), 1);
            BinaryPrimitives.WriteUInt32LittleEndian(handshake.AsSpan(9), 1000);
            packets.Enqueue(new("server", 13108, "client", 45678, handshake, null));
            foreach (var payload in peer.Login(111).Append(peer.Frame(WavesMessageIds.AddUnlockedGuideNotify, [42, 2, 1, 4])))
            {
                var raw = new byte[payload.Length + 5]; raw[0] = 0xEC;
                BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(1), 1);
                payload.CopyTo(raw, 5);
                packets.Enqueue(new("server", 13108, "client", 45678, raw, null));
            }
            using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            var service = new GameDataExportService(() => new ReplayCapture(packets),
                loader => loader.LoadPrivateKeyPem(peer.Rsa.ExportPkcs8PrivateKeyPem()), registry, Path.Combine(root, "engine"));
            var result = await service.ExecuteAsync(new(GameDataExportScope.None, [definition.Id]),
                new InlineProgress<GameDataExportProgress>(_ => { }), stop.Token);
            Equal(true, result.Success);
            Equal(true, result.Inventory is null);
            Equal(1L, result.Modules!.Single().Counters.Single().Value);
            Equal(1, result.Modules.Single().Artifacts.Length);
            Equal(true, File.Exists(result.Modules.Single().Artifacts.Single().Path));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private static async Task TestInventoryModuleIsolationAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "haiyu-aggregate-inventory-" + Guid.NewGuid().ToString("N"));
        var clock = new ExportTestClock();
        try
        {
            var context = new ExportCaptureContext(new JsonExportStore(root), clock);
            await using var coordinator = new ExportCoordinator(context, BuiltInExportModules.Create(), ["inventory"]);
            var decoder = new ProtocolJsonDecoder(ProtocolCatalog.Default.Value);
            void Feed(string session, int? player, ushort id, byte[]? bytes = null, bool current = true)
            {
                bytes ??= [];
                coordinator.Observe(new(new(GatewayMessageType.Push, 0, 0, 0, id, 0, bytes), bytes, player, session, 1, null,
                    decoder.Decode(id, bytes), IsCurrentSession: current));
            }
            void Full(string session, int? player)
            {
                Feed(session, player, WavesMessageIds.NormalItemResponse, [26, 4, 8, 1, 16, 2]);
                Feed(session, player, WavesMessageIds.WeaponItemResponse);
                Feed(session, player, WavesMessageIds.PhantomItemResponse);
                Feed(session, player, WavesMessageIds.ValidTimeItemResponse);
            }
            Full("A1", null);
            coordinator.Checkpoint();
            await context.Output.FlushAsync();
            Equal(0, coordinator.Progress().Single().Artifacts.Length);
            coordinator.SessionChanged(new(ExportSessionChangeKind.IdentityResolved, "A1", "A1", 111, 1));
            coordinator.Checkpoint();
            await context.Output.FlushAsync();
            Full("B1", 222);
            coordinator.Checkpoint();
            await context.Output.FlushAsync();
            Full("A2", 111);
            coordinator.Checkpoint();
            await context.Output.FlushAsync();
            Feed("A1", 111, WavesMessageIds.NormalItemResponse, [26, 4, 8, 9, 16, 9], false);
            var modules = await coordinator.FinishAsync(new(null, false));
            var result = coordinator.LegacyResult(modules);
            Equal(true, result.Success);
            Equal(2, result.PlayerSummaries!.Single(p => p.PlayerId == 111).UpdateCount);
            Equal(1, result.PlayerSummaries.Single(p => p.PlayerId == 222).UpdateCount);
            Equal(1, result.Inventory!.NormalItems.Single().Id);
            Equal("LatestComplete", modules[0].Artifacts.Last().Consistency);
            Equal(8, modules[0].Artifacts.Last().Sources.Length);
            Equal(111, result.PlayerId);
            Equal(1L, modules.Single().Counters.Single(c => c.PlayerId == 111 && c.Name == "normalItems").Value);
            Equal(0L, modules.Single().Counters.Single(c => c.PlayerId == 222 && c.Name == "weapons").Value);

            // 新捕获会话仍更新同一玩家文件；全量基线未齐时保留旧快照和旧格式文件。
            var latest = Path.Combine(root, "111", "game-data.json");
            Equal(latest, modules.Single().Artifacts.Single(a => a.PlayerId == 111).Path);
            var before = File.ReadAllText(latest);
            var legacy = Path.Combine(root, "111", "game-data-20260101-120000.json");
            var unrelated = Path.Combine(root, "111", "game-data-notes.json");
            var otherLegacy = Path.Combine(root, "222", "game-data-20260101-120000.json");
            File.WriteAllText(legacy, before);
            File.WriteAllText(unrelated, "keep");
            File.WriteAllText(otherLegacy, "other player");
            clock.Advance(TimeSpan.FromDays(1));
            var nextContext = new ExportCaptureContext(new JsonExportStore(root), clock);
            await using var nextCapture = new ExportCoordinator(nextContext, BuiltInExportModules.Create(), ["inventory"]);
            void Next(ushort id, byte[] bytes)
            {
                nextCapture.Observe(new(new(GatewayMessageType.Push, 0, 0, 0, id, 0, bytes), bytes, 111, "new-capture", 1,
                    null, decoder.Decode(id, bytes)));
            }
            Next(WavesMessageIds.NormalItemResponse, [26, 4, 8, 9, 16, 7]);
            nextCapture.Checkpoint();
            await nextContext.Output.FlushAsync();
            Equal(before, File.ReadAllText(latest));
            Equal(true, File.Exists(legacy));
            Next(WavesMessageIds.WeaponItemResponse, []);
            Next(WavesMessageIds.PhantomItemResponse, []);
            Next(WavesMessageIds.ValidTimeItemResponse, []);
            var nextModules = await nextCapture.FinishAsync(new(null, false));
            Equal(GameDataExportModuleState.Completed, nextModules.Single().State);
            Equal(latest, nextModules.Single().Artifacts.Single().Path);
            Equal(false, File.Exists(legacy));
            Equal("keep", File.ReadAllText(unrelated));
            Equal("other player", File.ReadAllText(otherLegacy));
            Equal(true, File.Exists(Path.Combine(root, "222", "game-data.json")));
            using var latestJson = JsonDocument.Parse(File.ReadAllText(latest));
            Equal(9, latestJson.RootElement.GetProperty("inventory").GetProperty("normalItems")
                .GetProperty("items")[0].GetProperty("id").GetInt32());
            using var index = JsonDocument.Parse(File.ReadAllText(nextContext.IndexPath));
            Equal(latest, index.RootElement.GetProperty("modules")[0].GetProperty("artifacts")[0].GetProperty("path").GetString());

        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private static async Task TestModuleCompletionStatesAsync()
    {
        foreach (var selected in new[] { new[] { "inventory" }, new[] { "server-messages" }, new[] { "inventory", "server-messages" } })
        {
            var root = Path.Combine(Path.GetTempPath(), "haiyu-states-" + Guid.NewGuid().ToString("N"));
            try
            {
                var context = new ExportCaptureContext(new JsonExportStore(root));
                await using var coordinator = new ExportCoordinator(context, BuiltInExportModules.Create(), selected);
                var id = selected.Length == 1 && selected[0] == "server-messages" ? (ushort)65535 : WavesMessageIds.NormalItemResponse;
                var decoded = new ProtocolJsonDecoder(ProtocolCatalog.Default.Value).Decode(id, []);
                coordinator.Observe(new(new(GatewayMessageType.Push, 0, 0, 0, id, 0, []), [], 123, "login", 1, null, decoded));
                var modules = await coordinator.FinishAsync(new(null, false));
                Equal(selected.Length, modules.Length);
                foreach (var module in modules)
                    Equal(module.ModuleId == "inventory" ? GameDataExportModuleState.Incomplete : GameDataExportModuleState.Completed, module.State);
                Equal(selected.Length == 1 && selected[0] == "server-messages", coordinator.LegacyResult(modules).Success);
                if (!selected.Contains("inventory")) Equal(false, Directory.Exists(Path.Combine(root, "123")) && File.Exists(Path.Combine(root, "123", "game-data.json")));
                if (selected.Contains("server-messages"))
                {
                    var result = coordinator.LegacyResult(modules);
                    Equal(true, result.EventExport!.Complete);
                    Equal(context.CaptureId, result.EventExport.CaptureId);
                    using var index = JsonDocument.Parse(File.ReadAllText(context.IndexPath));
                    Equal(selected.Length == 1 ? "completed" : "incomplete", index.RootElement.GetProperty("state").GetString());
                }
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }
        var switchedRoot = Path.Combine(Path.GetTempPath(), "haiyu-login-missing-" + Guid.NewGuid().ToString("N"));
        try
        {
            var context = new ExportCaptureContext(new JsonExportStore(switchedRoot));
            await using var coordinator = new ExportCoordinator(context, BuiltInExportModules.Create(), ["inventory"]);
            var decoder = new ProtocolJsonDecoder(ProtocolCatalog.Default.Value);
            foreach (var id in new[] { WavesMessageIds.NormalItemResponse, WavesMessageIds.WeaponItemResponse, WavesMessageIds.PhantomItemResponse, WavesMessageIds.ValidTimeItemResponse })
                coordinator.Observe(new(new(GatewayMessageType.Push, 0, 0, 0, id, 0, []), [], 111, "old", 1, null, decoder.Decode(id, [])));
            coordinator.Checkpoint();
            await context.Output.FlushAsync();
            coordinator.SessionChanged(new(ExportSessionChangeKind.LoginStarted, "new", "old", null, 2));
            coordinator.SessionChanged(new(ExportSessionChangeKind.IdentityResolved, "new", "new", 222, 2));
            var modules = await coordinator.FinishAsync(new(null, false));
            Equal(GameDataExportModuleState.Incomplete, modules.Single().State);
            Equal(false, coordinator.LegacyResult(modules).Success);
            Equal(111, coordinator.LegacyResult(modules).PlayerSummaries!.Single().PlayerId);
        }
        finally { if (Directory.Exists(switchedRoot)) Directory.Delete(switchedRoot, true); }
        var emptyRoot = Path.Combine(Path.GetTempPath(), "haiyu-no-data-" + Guid.NewGuid().ToString("N"));
        try
        {
            var context = new ExportCaptureContext(new JsonExportStore(emptyRoot));
            await using var coordinator = new ExportCoordinator(context, BuiltInExportModules.Create(), ["inventory"]);
            var modules = await coordinator.FinishAsync(new(null, false));
            Equal(GameDataExportModuleState.NoData, modules.Single().State);
            Equal(false, coordinator.LegacyResult(modules).Success);
        }
        finally { if (Directory.Exists(emptyRoot)) Directory.Delete(emptyRoot, true); }
    }
    private static async Task TestInventoryPendingVersionAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "haiyu-pending-version-" + Guid.NewGuid().ToString("N"));
        try
        {
            var clock = new ExportTestClock();
            var context = new ExportCaptureContext(new JsonExportStore(root), clock);
            await using var coordinator = new ExportCoordinator(context, BuiltInExportModules.Create(), ["inventory"]);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            context.Output.Enqueue("test", "blocker", "test", 0, [], async _ => { entered.SetResult(); await release.Task; return "blocker"; }, _ => { }, false);
            await entered.Task;
            var decoder = new ProtocolJsonDecoder(ProtocolCatalog.Default.Value);
            void Feed(ushort id, byte[] data) => coordinator.Observe(new(new(GatewayMessageType.Push, 0, 0, 0, id, 0, data), data,
                123, "login", 1, null, decoder.Decode(id, data)));
            Feed(WavesMessageIds.NormalItemResponse, [26, 4, 8, 1, 16, 1]);
            Feed(WavesMessageIds.WeaponItemResponse, []); Feed(WavesMessageIds.PhantomItemResponse, []); Feed(WavesMessageIds.ValidTimeItemResponse, []);
            coordinator.Checkpoint();
            Feed(WavesMessageIds.NormalItemResponse, [26, 4, 8, 2, 16, 2]);
            coordinator.Checkpoint();
            release.SetResult();
            await context.Output.FlushAsync();
            var first = coordinator.LegacyResult(coordinator.Progress());
            Equal(1, first.Inventory!.NormalItems.Single().Id);
            clock.Advance(TimeSpan.FromSeconds(2));
            coordinator.Checkpoint();
            await context.Output.FlushAsync();
            var modules = await coordinator.FinishAsync(new(null, false));
            var latest = coordinator.LegacyResult(modules);
            Equal(2, latest.UpdateCount);
            Equal(2, latest.Inventory!.NormalItems.Single().Id);
            using var json = JsonDocument.Parse(File.ReadAllText(latest.OutputJsonPath!));
            Equal(2, json.RootElement.GetProperty("inventory").GetProperty("normalItems").GetProperty("items")[0].GetProperty("id").GetInt32());
            var artifact = modules.Single().Artifacts.Single();
            Equal(2L, artifact.Revision);
            Equal(true, artifact.Sources.All(s => s.SessionId == "login" && s.PlayerId == 123 && (!s.Required || s.ReceivedAt is not null)));
            var missingMail = artifact.Sources.Single(s => s.SourceId == "mails");
            Equal("Missing", missingMail.State);
            Equal(false, missingMail.Required);
            Equal(true, missingMail.ReceivedAt is null); // 不能为未收到的来源伪造时间。

        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private static async Task TestFilteredMessageModulesAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "haiyu-filtered-modules-" + Guid.NewGuid().ToString("N"));
        try
        {
            var filtered = new GameDataExportModuleDescriptor("guide-events", "guides", "Guide events", false, false);
            var registry = new ExportModuleRegistry([
                new(ServerMessagesExportModule.Definition, context => new ServerMessagesExportModule(context)),
                new(filtered, context => new ServerMessagesExportModule(context, descriptor: filtered,
                    messageIds: new HashSet<ushort> { WavesMessageIds.AddUnlockedGuideNotify }))
            ]);
            var context = new ExportCaptureContext(new JsonExportStore(root));
            await using var coordinator = new ExportCoordinator(context, registry, [ServerMessagesExportModule.Definition.Id, filtered.Id]);
            var decoder = new ProtocolJsonDecoder(ProtocolCatalog.Default.Value);
            foreach (var id in new[] { WavesMessageIds.NormalItemResponse, WavesMessageIds.AddUnlockedGuideNotify, WavesMessageIds.AddUnlockedGuideNotify, (ushort)65535 })
                coordinator.Observe(new(new(GatewayMessageType.Push, 0, 0, 0, id, 0, []), [], 111, "login", 1, null, decoder.Decode(id, [])));
            var modules = await coordinator.FinishAsync(new(null, false));
            Equal(true, coordinator.LegacyResult(modules).Success);
            Equal(4L, coordinator.LegacyResult(modules).EventExport!.Written);
            var guide = modules.Single(m => m.ModuleId == filtered.Id);
            Equal(2L, guide.Counters.Single(c => c.Name == "written" && c.PlayerId is null).Value);
            Equal(2L, guide.Counters.Single(c => c.Name == "written" && c.PlayerId == 111).Value);
            Equal(4, modules.SelectMany(m => m.Artifacts).Select(a => a.Path).Distinct().Count());
            var lines = File.ReadAllLines(guide.Artifacts.Single(a => a.Format == "jsonl").Path);
            Equal(2, lines.Length);
            for (var i = 0; i < lines.Length; i++)
            {
                using var json = JsonDocument.Parse(lines[i]);
                Equal(i + 1, json.RootElement.GetProperty("eventIndex").GetInt32());
                Equal(i + 2, json.RootElement.GetProperty("captureEventIndex").GetInt32());
            }
            Equal(true, guide.Artifacts.All(a => a.Path.Contains(Path.Combine("modules", filtered.Id))));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private sealed class SessionAuditModule(List<ExportSessionChange> changes) : IGameDataExportModule
    {
        internal static readonly GameDataExportModuleDescriptor Definition = new("session-audit", "audit", "Session audit", false, false);
        public GameDataExportModuleDescriptor Descriptor => Definition;
        public IReadOnlySet<ushort> MessageIds { get; } = new HashSet<ushort>();
        public void Consume(MessageObservation message) { }
        public void OnSessionChanged(ExportSessionChange change) => changes.Add(change);
        public void Checkpoint(bool stopping = false) { }
        public GameDataExportModuleResult GetProgress() => new(Definition.Id, Definition.DisplayName, GameDataExportModuleState.Running, null, [], [], []);
        public ValueTask<GameDataExportModuleResult> CompleteAsync(ExportStopContext stop) => ValueTask.FromResult(GetProgress() with { State = GameDataExportModuleState.Completed });
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class ResumeReplayCapture(Queue<(UdpFrame Packet, int Generation)> packets) : IAdaptiveUdpCapture
    {
        public int NetworkGeneration { get; private set; }
        public void Start() { }
        public void Stop() { }
        public void Dispose() { }
        public void ConfirmInterface(UdpFrame packet) { }
        public string? TakeDiagnostic() => null;
        public UdpFrame? TryRead()
        {
            if (!packets.TryDequeue(out var item)) return null;
            NetworkGeneration = item.Generation;
            return item.Packet;
        }
    }
    private static async Task TestConfirmedResumeNotificationAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "haiyu-resume-event-" + Guid.NewGuid().ToString("N"));
        using var peer = new ExportPeer();
        var packets = new Queue<(UdpFrame, int)>();
        void Packet(byte[] bytes, int generation, int port = 45678) => packets.Enqueue((new("server", 13108, "client", port, bytes, null), generation));
        void Handshake(uint conv, int generation, int port)
        {
            var bytes = new byte[13]; bytes[0] = 0xEE;
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(1), conv);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(9), 1000);
            Packet(bytes, generation, port);
        }
        void Raw(uint conv, byte[] frame, int generation, int port)
        {
            var bytes = new byte[frame.Length + 5]; bytes[0] = 0xEC;
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(1), conv); frame.CopyTo(bytes, 5);
            Packet(bytes, generation, port);
        }
        Handshake(1, 0, 45678);
        foreach (var frame in peer.Login(111)) Raw(1, frame, 0, 45678);
        Handshake(2, 1, 45679);
        Raw(2, peer.Frame(65535, [8, 1]), 1, 45679); // 不能确认的候选流量。
        Raw(2, peer.Frame(WavesMessageIds.AddUnlockedGuideNotify, [42, 2, 1, 4]), 1, 45679);
        Handshake(3, 1, 45680);
        var newKey = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        var keyResponse = new Haiyu.ABI.Protocol.ProtoKeyResponse
        { Key = Google.Protobuf.ByteString.CopyFrom(peer.Rsa.Encrypt(newKey, System.Security.Cryptography.RSAEncryptionPadding.Pkcs1)) };
        Raw(3, peer.Frame(WavesMessageIds.ProtoKeyResponse, Google.Protobuf.MessageExtensions.ToByteArray(keyResponse)), 1, 45680);
        var changes = new List<ExportSessionChange>();
        try
        {
            var registry = new ExportModuleRegistry([new(SessionAuditModule.Definition, _ => new SessionAuditModule(changes))]);
            using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            var service = new GameDataExportService(() => new ResumeReplayCapture(packets),
                loader => loader.LoadPrivateKeyPem(peer.Rsa.ExportPkcs8PrivateKeyPem()), registry, root);
            var result = await service.ExecuteAsync(new(GameDataExportScope.None, [SessionAuditModule.Definition.Id]),
                new InlineProgress<GameDataExportProgress>(_ => { }), stop.Token);
            Equal(true, result.Success);
            var resumed = changes.Where(c => c.Kind == ExportSessionChangeKind.TransportResumed).ToArray();
            Equal(1, resumed.Length);
            Equal(true, resumed[0].Verified && resumed[0].IsCurrent);
            Equal(2U, resumed[0].ConnectionId);
            Equal(1U, resumed[0].PreviousConnectionId!.Value);
            Equal(111, resumed[0].PlayerId!.Value);
            Equal(resumed[0].SessionId, resumed[0].PreviousSessionId);
            Equal(true, changes.Any(c => c.Kind == ExportSessionChangeKind.ConnectionRetired && c.ConnectionId == 1));
            Equal(true, changes.Count(c => c.Kind == ExportSessionChangeKind.LoginStarted) >= 2);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
