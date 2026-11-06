using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text.Json;
using ABI.Models;
using ABIRuntime.Runtime;
using ABIRuntime;
using Google.Protobuf;
using Haiyu.ABI.Protocol;
using Haiyu.ABI.Services.GameDataExport.Capture;
using Haiyu.ABI.Services.GameDataExport.Decrypt;
using Haiyu.ABI.Services.GameDataExport.Events;
using Haiyu.ABI.Services.GameDataExport.Export;
using Haiyu.ABI.Services.GameDataExport.Protocol;
using Haiyu.ABI.Services.GameDataExport.Rebuild;
using Haiyu.ABI.Services.GameDataExport.Session;
using Haiyu.ABI.Services.GameDataExport;
using MemoryPack;

namespace ABIRuntime.TransportTests;

internal static partial class Program
{
    private sealed class TestMessageObserver(Action<MessageObservation> observe) : IGameMessageObserver
    {
        public void Observe(MessageObservation message) => observe(message);
        public void Bind(string sessionId, int playerId) { }
    }

    private static void PublishEvent(EventExportSession events, GatewayFrame frame, byte[]? plain,
        int? player, string session, uint connection, string? status = null)
    {
        var decoded = status is null && plain is not null && frame.Type != GatewayMessageType.Exception
            ? new ProtocolJsonDecoder(ProtocolCatalog.Default.Value).Decode(frame.MsgId, plain) : null;
        events.Observe(new(frame, plain, player, session, connection, status, decoded));
    }

    private sealed class ExportPeer(int rsaBits = 2048) : IDisposable
    {
        internal readonly RSA Rsa = RSA.Create(rsaBits);
        internal readonly byte[] Key = RandomNumberGenerator.GetBytes(32);
        private uint _sequence;

        internal SessionKeyLoader CreateLoader()
        {
            var loader = new SessionKeyLoader();
            loader.LoadPrivateKeyPem(Rsa.ExportPkcs8PrivateKeyPem());
            return loader;
        }

        internal byte[] Frame(ushort id, byte[] body, bool compressed = false)
        {
            var sequence = ++_sequence;
            var payload = body.ToArray();
            if (compressed)
            {
                using var output = new MemoryStream();
                using (var zlib = new System.IO.Compression.ZLibStream(output, System.IO.Compression.CompressionLevel.Fastest, true))
                    zlib.Write(payload);
                payload = output.ToArray();
            }
            if (id != MsgIdFilter.ProtoKeyResponse)
            {
                if (payload.Length > 0) payload[sequence % (uint)payload.Length] ^= Key[sequence % 32];
                using var aes = Aes.Create();
                aes.Key = Key;
                payload = aes.EncryptEcb(payload, PaddingMode.PKCS7);
            }
            var header = compressed ? 18 : 14;
            var result = new byte[header + payload.Length];
            var size = result.Length - 3;
            result[0] = (byte)size; result[1] = (byte)(size >> 8); result[2] = (byte)(size >> 16);
            result[3] = compressed ? (byte)0x14 : (byte)4;
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), sequence);
            BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(8), id);
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(10), Crc32.Compute(payload));
            if (compressed) BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(14), (uint)body.Length);
            payload.CopyTo(result, header);
            return result;
        }

        internal byte[] KeyFrame() => Frame(112, new ProtoKeyResponse
        {
            Key = ByteString.CopyFrom(Rsa.Encrypt(Key, RSAEncryptionPadding.Pkcs1))
        }.ToByteArray());

        internal byte[][] Inventory(int item = 1)
        {
            var normal = new NormalItemResponse();
            normal.NormalItemList.Add(new NormalItem { Id = item, Count = 3 });
            return [Frame(MsgIdFilter.NormalItemResponse, normal.ToByteArray()),
                Frame(MsgIdFilter.WeaponItemResponse, []), Frame(MsgIdFilter.PhantomItemResponse, []),
                Frame(MsgIdFilter.ValidTimeItemResponse, [])];
        }

        internal byte[][] Login(int player) => [KeyFrame(),
            Frame(MsgIdFilter.LoginResponse, []),
            Frame(MsgIdFilter.BasicInfoNotify, new BasicInfoNotify { Id = player }.ToByteArray()),
            .. Inventory(player)];

        public void Dispose() => Rsa.Dispose();
    }

    private static Task TestLoginIsolationAsync()
    {
        foreach (var scope in new[] { GameDataExportScope.Inventory, GameDataExportScope.Inventory | GameDataExportScope.AllServerMessages })
        {
            using var peer = new ExportPeer();
            using var state = new TestCapturePipeline(peer.CreateLoader(), scope);
            if (scope.HasFlag(GameDataExportScope.AllServerMessages)) state.Observer = new TestMessageObserver(_ => { });
            foreach (var frame in peer.Login(111)) state.ConsumeRaw(frame);
            Equal(true, state.Ready);
            state.ConsumeRaw(peer.Frame(MsgIdFilter.LogoutNotify, []));
            Equal(PlayerIdResolver.Phase.WaitingKey, state.Session.PlayerId.Current);
            state.ConsumeRaw(peer.Frame(MsgIdFilter.LoginResponse, []));
            Equal(PlayerIdResolver.Phase.WaitingId, state.Session.PlayerId.Current);
            foreach (var frame in peer.Inventory(222)) state.ConsumeRaw(frame);
            Equal(false, state.Ready);
            state.ConsumeRaw(peer.Frame(MsgIdFilter.BasicInfoNotify, new BasicInfoNotify { Id = 222 }.ToByteArray()));
            foreach (var frame in peer.Inventory(222)) state.ConsumeRaw(frame);
            Equal(true, state.Ready);
            Equal(222, state.Session.PlayerId.PlayerId);
            Equal(222, state.Inventory.NormalItems[MsgIdFilter.NormalItemResponse][0].Id);
        }
        return Task.CompletedTask;
    }

    private static Task TestExportConsumerBoundariesAsync()
    {
        // 快照本身只需要明文 protobuf；不创建密钥、会话或捕获源。
        var inventory = new InventoryCaptureState(GameDataExportScope.Inventory);
        Equal(InventoryMessageResult.NotHandled, inventory.Apply(65535, []));
        Equal(InventoryMessageResult.Updated, inventory.Apply(MsgIdFilter.NormalItemResponse, []));
        Equal(InventoryMessageResult.Updated, inventory.Apply(MsgIdFilter.WeaponItemResponse, []));
        Equal(InventoryMessageResult.Updated, inventory.Apply(MsgIdFilter.PhantomItemResponse, []));
        Equal(InventoryMessageResult.Updated, inventory.Apply(MsgIdFilter.ValidTimeItemResponse, []));
        Equal(true, inventory.IsComplete);
        var resumed = inventory.ForkForReconnect();
        inventory.Reset();
        Equal(false, inventory.IsComplete);
        Equal(true, resumed.IsComplete);
        Equal(InventoryMessageResult.Invalid, resumed.Apply(MsgIdFilter.NormalItemResponse, [0xFF]));
        Equal(4, resumed.Version);

        // 是否确认重连由协议管线决定，不依赖事件写入器的返回值。
        using var peer = new ExportPeer();
        using var connection = new TestCapturePipeline(peer.CreateLoader(), GameDataExportScope.Inventory);
        foreach (var frame in peer.Login(111)) connection.ConsumeRaw(frame);
        connection.Observer = new TestMessageObserver(_ => { });
        try
        {
            connection.ConsumeRaw(peer.Frame(MsgIdFilter.NormalItemResponse, [0xFF]));
            throw new Exception("观察器改变了默认背包模式的错误处理规则");
        }
        catch (InvalidDataException) { }
        using var candidate = connection.ForkForReconnect();
        candidate.Observer = null;
        Equal(false, candidate.Ready);
        candidate.ConsumeRaw(peer.Frame(WavesMessageIds.AddUnlockedGuideNotify, [42, 2, 1, 4]));
        Equal(true, candidate.Ready);
        Equal(111, candidate.Session.PlayerId.PlayerId);
        return Task.CompletedTask;
    }

    private static async Task TestFrameValidationAsync()
    {
        using var peer = new ExportPeer();
        using (var pending = new TestCapturePipeline(peer.CreateLoader(), GameDataExportScope.Inventory))
        {
            var corruptKey = peer.KeyFrame();
            corruptKey[10] ^= 1;
            pending.ConsumeRaw(corruptKey);
            Equal(true, pending.Session.SessionKey is null);
            Equal(false, pending.Session.HasVerifiedTraffic);
        }
        using var state = new TestCapturePipeline(peer.CreateLoader(), GameDataExportScope.Inventory);
        foreach (var frame in peer.Login(111)) state.ConsumeRaw(frame);
        var failures = new List<string>();
        state.Observer = new TestMessageObserver(m => { if (m.Status is not null) failures.Add(m.Status); });
        foreach (var compressed in new[] { false, true })
        {
            var body = new NormalItemResponse(); body.NormalItemList.Add(new NormalItem { Id = 999 });
            var corrupt = peer.Frame(MsgIdFilter.NormalItemResponse, body.ToByteArray(), compressed);
            corrupt[10] ^= 1;
            state.ConsumeRaw(corrupt);
            Equal(111, state.Inventory.NormalItems[MsgIdFilter.NormalItemResponse][0].Id);
        }
        Equal(true, failures.SequenceEqual(new[] { "crc_failed", "crc_failed" }));

        var badInner = peer.Inventory(999)[0];
        badInner[10] ^= 1;
        var datagram = new byte[24 + badInner.Length + 4];
        datagram[0] = 1; datagram[4] = 81;
        BinaryPrimitives.WriteUInt32LittleEndian(datagram.AsSpan(20), (uint)badInner.Length);
        badInner.CopyTo(datagram, 24);
        BinaryPrimitives.WriteUInt32LittleEndian(datagram.AsSpan(datagram.Length - 4), Crc32.Compute(datagram.AsSpan(0, datagram.Length - 4)));
        Equal(true, KcpSegment.TryParseMany(datagram, datagram, true, out var segments));
        var stream = new KcpStreamRebuilder();
        foreach (var segment in segments) stream.Add(segment);
        state.Consume(stream);
        Equal(3, failures.Count);
        Equal(111, state.Inventory.NormalItems[MsgIdFilter.NormalItemResponse][0].Id);

        using var bigPeer = new ExportPeer(4096);
        using var bigLoader = bigPeer.CreateLoader();
        var keyBytes = bigPeer.KeyFrame(); var offset = 0;
        Equal(true, FrameSplitter.TryTake(keyBytes, ref offset, out var keyFrame));
        Equal(true, bigLoader.TryResolve(keyFrame!));
        Equal(true, bigPeer.Key.SequenceEqual(bigLoader.SessionKey!));

        var root = Path.Combine(Path.GetTempPath(), "haiyu-control-" + Guid.NewGuid().ToString("N"));
        try
        {
            var events = new EventExportSession(root);
            using var noKey = new TestCapturePipeline(new SessionKeyLoader(), GameDataExportScope.AllServerMessages);
            noKey.Observer = events;
            byte[] control = [11, 0, 0, 3, 1, 0, 0, 0, 2, 0, 0x39, 0x30, 0, 0];
            noKey.ConsumeRaw(control);
            var stats = await events.FinishAsync(null);
            Equal(1L, stats.Written);
            var file = Directory.GetFiles(root, "*.jsonl", SearchOption.AllDirectories).Single();
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            Equal(12345U, doc.RootElement.GetProperty("errorCode").GetUInt32());
            Equal("transport_exception", doc.RootElement.GetProperty("decodeStatus").GetString());
            Equal("transport_control", doc.RootElement.GetProperty("rawEncoding").GetString());
        }
        finally { Directory.Delete(root, true); }
    }

    private static Task TestParallelDecryptionAsync()
    {
        Parallel.For(0, 8, worker =>
        {
            using var peer = new ExportPeer();
            var body = new byte[] { 8, 7, 16, 3 };
            var encoded = peer.Frame(999, body); var offset = 0;
            Equal(true, FrameSplitter.TryTake(encoded, ref offset, out var frame));
            using var cipher = new FrameCipher();
            for (var i = 0; i < 200; i++)
            {
                Equal(true, cipher.TryDecode(frame!, peer.Key, out var plain, out _));
                Equal(true, body.SequenceEqual(plain!));
                Equal(true, FrameCipher.DecryptRaw(frame!, peer.Key).SequenceEqual(body));
            }
        });
        return Task.CompletedTask;
    }

    private static async Task TestExportResultIsolationAsync()
    {
        foreach (var continueWithSecondPlayer in new[] { false, true })
        {
            using var peer = new ExportPeer();
            var packets = new Queue<UdpFrame>();
            void Login(uint conv, int player)
            {
                var handshake = new byte[13]; handshake[0] = 0xEE;
                BinaryPrimitives.WriteUInt32LittleEndian(handshake.AsSpan(1), conv);
                BinaryPrimitives.WriteUInt32LittleEndian(handshake.AsSpan(9), 1000);
                packets.Enqueue(new("server", 13108, "client", 45678, handshake, null));
                foreach (var frame in peer.Login(player))
                {
                    var raw = new byte[5 + frame.Length]; raw[0] = 0xEC;
                    BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(1), conv);
                    frame.CopyTo(raw, 5);
                    packets.Enqueue(new("server", 13108, "client", 45678, raw, null));
                }
            }
            Login(1, 111);
            var root = Path.Combine(Path.GetTempPath(), "haiyu-partial-" + Guid.NewGuid().ToString("N"));
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var secondQueued = false;
            try
            {
                var service = new GameDataExportService(() => new ReplayCapture(packets),
                    loader => loader.LoadPrivateKeyPem(peer.Rsa.ExportPkcs8PrivateKeyPem()),
                    (snapshot, _) =>
                    {
                        if (snapshot.PlayerId == 111)
                        {
                            if (!continueWithSecondPlayer) stop.Cancel();
                            else if (!secondQueued) { secondQueued = true; Login(2, 222); }
                            return null;
                        }
                        stop.Cancel();
                        return "/fake/222";
                    }, () => new EventExportSession(root));
                var result = await service.ExecuteAsync(new(GameDataExportScope.Inventory | GameDataExportScope.AllServerMessages),
                    new InlineProgress<GameDataExportProgress>(_ => { }), stop.Token);
                Equal(false, result.Success);
                Equal(true, result.EventExport!.Written > 0);
                Equal(true, result.EventExport.Complete);
                Equal(continueWithSecondPlayer ? 1 : 0, result.PlayerSummaries!.Length);
                Equal(true, result.Message.Contains("111") && result.Message.Contains("写入失败"));
            }
            finally { Directory.Delete(root, true); }
        }
    }

    private static async Task TestConnectionEndpointIsolationAsync()
    {
        using var peer = new ExportPeer();
        var packets = new Queue<UdpFrame>();
        void Login(string server, int player)
        {
            void Packet(byte[] data) => packets.Enqueue(new(server, 13108, "client", 45678, data, null));
            var handshake = new byte[13]; handshake[0] = 0xEE; handshake[1] = 1;
            BinaryPrimitives.WriteUInt32LittleEndian(handshake.AsSpan(9), 1000); Packet(handshake);
            uint sn = 0;
            foreach (var frame in peer.Login(player))
            {
                var segment = new byte[24 + frame.Length]; segment[0] = 1; segment[4] = 81;
                BinaryPrimitives.WriteUInt32LittleEndian(segment.AsSpan(12), sn++);
                BinaryPrimitives.WriteUInt32LittleEndian(segment.AsSpan(20), (uint)frame.Length);
                frame.CopyTo(segment, 24); Packet(segment);
            }
        }
        Login("server-a", 111);
        var writes = new List<int>();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var service = new GameDataExportService(() => new ReplayCapture(packets),
            loader => loader.LoadPrivateKeyPem(peer.Rsa.ExportPkcs8PrivateKeyPem()), (result, _) =>
            {
                writes.Add(result.PlayerId);
                if (writes.Count == 1) Login("server-b", 222);
                else stop.Cancel();
                return "/fake/" + result.PlayerId;
            });
        await service.ExecuteAsync(new(GameDataExportScope.Inventory), new InlineProgress<GameDataExportProgress>(_ => { }), stop.Token);
        Equal(true, writes.SequenceEqual(new[] { 111, 222 }));
    }

    private static async Task TestSlowExportDrainAsync()
    {
        ABIMemoryPack.EnsureFormatters();
        Equal(true, Contract.GameDataExportContract.WaitForCompletionOnCancellation);
        Equal(false, Contract.ComputerMonitorContract.WaitForCompletionOnCancellation);
        var name = "Haiyu.SlowDrain." + Guid.NewGuid().ToString("N");
        await using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await Task.WhenAll(server.WaitForConnectionAsync(), client.ConnectAsync());
        var request = Guid.NewGuid();
        var operation = Contract.GameDataExportContract.Operation;
        var host = Task.Run(async () =>
        {
            var cancel = await PipeProtocol.ReadAsync(server, CancellationToken.None);
            Equal(PipeMessageKind.Cancel, cancel.Kind);
            await Task.Delay(5500);
            await PipeProtocol.WriteAsync(server, new(PipeMessageKind.Result, request, PipeProtocolVersion.Current,
                operation, MemoryPackSerializer.Serialize(new GameDataExportResult(true, "drained", null, null, 0, 0, null))), CancellationToken.None);
        });
        await using var launcher = new Haiyu.Services.GameDataExportLauncher((_, token) =>
            PrivilegedRuntime.ReadResponseAsync<GameDataExportProgress, GameDataExportResult>(client, request,
                operation, null, token, Contract.GameDataExportContract.WaitForCompletionOnCancellation).AsTask(),
            () => false, stopGrace: TimeSpan.FromMilliseconds(100));
        launcher.TryStart(out var task);
        var stopped = await launcher.StopAsync();
        Equal(false, stopped!.Success);
        Equal(true, launcher.IsSessionActive);
        await Task.Delay(5100);
        Equal(false, task.IsCompleted);
        var result = await task.WaitAsync(TimeSpan.FromSeconds(3));
        Equal(true, result.Success);
        Equal(false, launcher.IsSessionActive);
        await host;
    }
}
