using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using ABI.Models;
using Google.Protobuf;
using Haiyu.ABI.Protocol;
using Haiyu.ABI.Services.GameDataExport.Capture;
using Haiyu.ABI.Services.GameDataExport.Export;
using Haiyu.ABI.Services.GameDataExport.Rebuild;
using Haiyu.ABI.Services.GameDataExport;

namespace ABIRuntime.TransportTests;

internal static partial class Program
{
    private static async Task TestKeyTimeoutCleanupAsync()
    {
        foreach (var handshakeOnly in new[] { false, true })
        {
            using var rsa = RSA.Create(2048);
            var clock = new ExportTestClock();
            var changes = new TestNetworkChanges();
            var opened = new List<TestInterfaceCapture>();
            using var capture = new MultiInterfaceUdpCapture(
                () => new[] { new CaptureDevice("wired", "wired", "Ethernet"), new CaptureDevice("wifi", "wifi", "WLAN") },
                d =>
                {
                    var c = new TestInterfaceCapture(d.Id, []);
                    if (handshakeOnly && d.Id == "wired")
                    {
                        var bytes = new byte[13]; bytes[0] = 0xEE;
                        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(1), 123);
                        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(9), 1000);
                        c.Packets.Enqueue(new("server", 13108, "client", 45678, bytes, null));
                    }
                    opened.Add(c);
                    return c;
                }, changes);
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var service = new GameDataExportService(() => capture, loader => loader.LoadPrivateKeyPem(rsa.ExportPkcs8PrivateKeyPem()), (_, _) => throw new Exception("未取得密钥不应导出"));
            await using var launcher = new Haiyu.Services.GameDataExportLauncher((p, ct) =>
                service.ExecuteAsync(new(GameDataExportScope.Inventory), new InlineProgress<GameDataExportProgress>(data =>
                {
                    p.Report(new ABIRuntime.Abstractions.PrivilegedProgress<GameDataExportProgress>(
                        ABIRuntime.Abstractions.PrivilegedStage.Executing, data.Percentage, data.Message, data));
                    if (data.CaptureReady) ready.TrySetResult();
                }), ct).AsTask(), () => true, clock);
            launcher.TryStart(out var session);
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(2));
            clock.Advance(TimeSpan.FromMinutes(29));
            await Task.Delay(40);
            Equal(false, session.IsCompleted);
            Equal(2, opened.Count);
            Equal(true, opened.All(c => c.Disposed == 0));
            clock.Advance(TimeSpan.FromMinutes(1));
            var result = await session.WaitAsync(TimeSpan.FromSeconds(2));
            Equal(false, result.Success);
            Equal(true, result.Message.Contains("会话密钥超时"));
            Equal(true, opened.All(c => c.Disposed == 1));
            Equal(0, changes.Subscribers);
        }
    }

    private static Task TestNetworkCandidatesAsync()
    {
        var ids = Enumerable.Range(0, 6).Select(_ => Guid.NewGuid()).ToArray();
        var devices = ids.Select((id, i) => new CaptureDevice(i.ToString(), @"\Device\NPF_{" + id.ToString().ToUpperInvariant() + "}", "unknown vendor " + i)).ToArray();
        var interfaces = new[]
        {
            new InterfaceAddress(ids[0].ToString(), false, false, [IPAddress.Parse("192.168.1.2")]), // 未插网线
            new InterfaceAddress(ids[1].ToString(), true, false, [IPAddress.Parse("192.168.1.3")]),  // 无线
            new InterfaceAddress(ids[2].ToString(), true, false, [IPAddress.Parse("10.0.0.2")]),     // 有效虚拟接口
            new InterfaceAddress(ids[3].ToString(), true, true, [IPAddress.Loopback]),
            new InterfaceAddress(ids[4].ToString(), true, false, [IPAddress.IPv6Loopback]),
            new InterfaceAddress(ids[5].ToString(), true, false, [IPAddress.Parse("169.254.1.2")]),
        };
        var selected = NpcapDeviceCatalog.Select(devices, interfaces);
        Equal(true, selected.Select(d => d.Id).SequenceEqual(new[] { "1", "2" }));
        return Task.CompletedTask;
    }

    private static Task TestMultiInterfaceCaptureAsync()
    {
        var wired = new CaptureDevice("wired", "wired", "Ethernet");
        var wifi = new CaptureDevice("wifi", "wifi", "Intel Wireless");
        CaptureDevice[] available = [wired, wifi];
        var clock = new CaptureClock();
        var changes = new TestNetworkChanges();
        var opened = new List<TestInterfaceCapture>();
        var operations = new List<string>();
        var enumerationFails = false;
        using var manager = new MultiInterfaceUdpCapture(
            () => enumerationFails ? throw new IOException("temporary") : available,
            d => { var c = new TestInterfaceCapture(d.Id, operations); opened.Add(c); return c; }, changes, clock);
        manager.Start();
        Equal(2, opened.Count);
        opened[0].Packets.Enqueue(ProbePacket());
        opened[1].Packets.Enqueue(ProbePacket());
        Equal("wired", manager.TryRead()!.InterfaceId);
        var wirelessFrame = manager.TryRead()!;
        Equal("wifi", wirelessFrame.InterfaceId);
        Equal(0, opened[0].Disposed); // 单凭收到了包不能锁定。
        manager.ConfirmInterface(wirelessFrame);
        Equal(1, opened[0].Disposed);
        Equal(0, opened[1].Disposed);
        changes.Raise();
        Equal(2, opened.Count); // 系统回调不能增删句柄。
        clock.Advance(TimeSpan.FromMilliseconds(200));
        changes.Raise(); // 连续事件合并，按最后一次事件防抖。
        clock.Advance(TimeSpan.FromMilliseconds(400));
        manager.TryRead();
        Equal(2, opened.Count);
        clock.Advance(TimeSpan.FromMilliseconds(200));
        manager.TryRead();
        Equal(3, opened.Count);
        Equal(1, manager.NetworkGeneration);
        Equal(0, opened[1].Disposed); // 原接口仍可用时保留原句柄。
        enumerationFails = true;
        changes.Raise(); clock.Advance(TimeSpan.FromMilliseconds(600)); manager.TryRead();
        Equal(0, opened[1].Disposed);
        enumerationFails = false;
        available = [wired];
        clock.Advance(TimeSpan.FromMilliseconds(600)); manager.TryRead();
        Equal(1, opened[1].Disposed);
        Equal(true, operations.IndexOf("start:wired:2") < operations.IndexOf("dispose:wifi:1"));
        opened[2].FailRead = true;
        manager.TryRead();
        Equal(1, opened[2].Disposed);
        clock.Advance(TimeSpan.FromMilliseconds(600)); manager.TryRead();
        Equal(4, opened.Count); // 捕获错误也会重新探测，无需等待系统事件。
        opened[3].Packets.Enqueue(ProbePacket());
        manager.ConfirmInterface(manager.TryRead()!);
        manager.Dispose();
        Equal(true, opened.All(c => c.Disposed == 1));
        Equal(0, changes.Subscribers);

        var failedSource = new TestNetworkChanges();
        using var empty = new MultiInterfaceUdpCapture(() => [], _ => throw new Exception(), failedSource, clock);
        try { empty.Start(); throw new Exception("没有候选接口却报告成功"); }
        catch (InvalidOperationException) { }
        Equal(0, failedSource.Subscribers);
        // 一张网卡打不开不妨碍其他有效接口启动。
        var partial = new List<TestInterfaceCapture>();
        using var fallback = new MultiInterfaceUdpCapture(() => new[] { wired, wifi }, d =>
        {
            var c = new TestInterfaceCapture(d.Id, []) { FailStart = d.Id == "wired" };
            partial.Add(c); return c;
        }, new TestNetworkChanges(), clock);
        fallback.Start();
        Equal(1, partial[0].Disposed);
        Equal(0, partial[1].Disposed);
        return Task.CompletedTask;
    }

    private static async Task TestNetworkReconnectAsync()
    {
        foreach (var newConnection in new[] { false, true })
        {
            using var rsa = RSA.Create(2048);
            var key = RandomNumberGenerator.GetBytes(32);
            var wired = new CaptureDevice("wired", "wired", "Ethernet");
            var wifi = new CaptureDevice("wifi", "wifi", "WLAN");
            CaptureDevice[] available = [wired, wifi];
            var clock = new CaptureClock();
            var changes = new TestNetworkChanges();
            var captures = new List<TestInterfaceCapture>();
            uint sequence = 0;
            byte[] Frame(ushort id, byte[] payload)
            {
                var seq = sequence++;
                if (id != 112)
                {
                    if (payload.Length > 0) payload[seq % (uint)payload.Length] ^= key[seq % 32];
                    using var aes = Aes.Create(); aes.Key = key;
                    payload = aes.EncryptEcb(payload, PaddingMode.PKCS7);
                }
                var frame = new byte[14 + payload.Length]; var length = frame.Length - 3;
                frame[0] = (byte)length; frame[1] = (byte)(length >> 8); frame[2] = (byte)(length >> 16);
                frame[3] = 4; BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(4), seq);
                BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(8), id);
                BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(10), Crc32.Compute(payload));
                payload.CopyTo(frame, 14);
                return frame;
            }
            UdpFrame Packet(byte[] bytes, int port = 45678) => new("203.0.113.10", 13108, "192.168.1.2", port, bytes, null);
            UdpFrame Handshake(uint conv, int port = 45678)
            {
                var bytes = new byte[13]; bytes[0] = 0xEE;
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(1), conv);
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(9), 1000);
                return Packet(bytes, port);
            }
            UdpFrame Raw(uint conv, byte[] frame, int port = 45678)
            {
                var bytes = new byte[5 + frame.Length]; bytes[0] = 0xEC;
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(1), conv); frame.CopyTo(bytes, 5);
                return Packet(bytes, port);
            }
            byte[] Normal(int count)
            {
                var response = new NormalItemResponse(); response.NormalItemList.Add(new NormalItem { Id = 99, Count = count });
                return response.ToByteArray();
            }
            var firstNormal = Raw(1, Frame(22445, Normal(7)));
            var initial = new Queue<UdpFrame>(new[]
            {
                Handshake(1),
                Raw(1, Frame(112, new ProtoKeyResponse { Key = ByteString.CopyFrom(rsa.Encrypt(key, RSAEncryptionPadding.Pkcs1)) }.ToByteArray())),
                Raw(1, Frame(15380, new BasicInfoNotify { Id = 123456 }.ToByteArray())),
                firstNormal,
                Raw(1, Frame(27089, new WeaponItemResponse().ToByteArray())),
                Raw(1, Frame(26275, new PhantomItemResponse().ToByteArray())),
                Raw(1, Frame(20447, new ValidTimeItemResponse().ToByteArray()))
            });
            var afterSwitch = new Queue<UdpFrame>();
            afterSwitch.Enqueue(firstNormal); // 换接口后的镜像重复裸帧，不能重复更新/锁定。
            if (newConnection)
            {
                afterSwitch.Enqueue(Handshake(2, 45679));
                afterSwitch.Enqueue(Raw(2, Frame(22445, Normal(21)), 45679)); // 不重新发送 112 / playerId。
            }
            else
            {
                // 现有连接只收到新的 KCP 数据，既无握手也无 112。
                var frame = Frame(22445, Normal(21));
                var bytes = new byte[24 + frame.Length];
                BinaryPrimitives.WriteUInt32LittleEndian(bytes, 1); bytes[4] = 81;
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20), (uint)frame.Length);
                frame.CopyTo(bytes, 24); afterSwitch.Enqueue(Packet(bytes));
            }
            using var manager = new MultiInterfaceUdpCapture(() => available, d =>
            {
                var c = new TestInterfaceCapture(d.Id, []);
                if (d.Id == "wired") while (initial.TryDequeue(out var packet)) c.Packets.Enqueue(packet);
                else if (captures.Any(x => x.Id == "wifi")) while (afterSwitch.TryDequeue(out var packet)) c.Packets.Enqueue(packet);
                else c.Packets.Enqueue(Handshake(77, 45680)); // 看似握手但没有有效游戏密钥，不允许锁定。
                captures.Add(c); return c;
            }, changes, clock);
            var root = Path.Combine(Path.GetTempPath(), "haiyu-network-" + Guid.NewGuid().ToString("N"));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var writes = 0; var dataUpdates = 0; string? firstPath = null;
            try
            {
                var service = new GameDataExportService(() => manager,
                    loader => loader.LoadPrivateKeyPem(rsa.ExportPkcs8PrivateKeyPem()), (result, timestamp) =>
                    {
                        writes++;
                        Equal(123456, result.PlayerId);
                        Equal(Convert.ToHexString(key).ToLowerInvariant(), result.SessionKeyHex);
                        var path = InventoryJsonWriter.TryWriteToRoot(root, result, timestamp);
                        if (writes == 1)
                        {
                            firstPath = path;
                            // 增加尚未写入的旧接口快照；切换后不能反过来覆盖更新的数据。
                            captures.First(c => c.Id == "wired").Packets.Enqueue(Raw(1, Frame(22445, Normal(9))));
                        }
                        else
                        {
                            Equal(firstPath!, path!);
                            if (writes > 2) throw new Exception("旧传输的快照覆盖了重连后的新快照");
                            timeout.CancelAfter(TimeSpan.FromMilliseconds(1200));
                        }
                        return path;
                    });
                var result = await service.ExecuteAsync(new(GameDataExportScope.Inventory),
                    new InlineProgress<GameDataExportProgress>(p =>
                    {
                        if (!p.HasDataUpdate) return;
                        dataUpdates++;
                        if (writes == 1 && dataUpdates == 5)
                        {
                            available = [wifi]; changes.Raise(); clock.Advance(TimeSpan.FromMilliseconds(600));
                        }
                    }), timeout.Token);
                Equal(true, result.Success);
                Equal(2, writes);
                Equal(6, result.DecodedFrames);
                Equal(6, dataUpdates); // 不把复制会话或重复包计作新的背包数据。
                using var json = JsonDocument.Parse(File.ReadAllText(result.OutputJsonPath!));
                Equal(21, json.RootElement.GetProperty("inventory").GetProperty("normalItems").GetProperty("items")[0].GetProperty("count").GetInt32());
                Equal(true, captures.All(c => c.Disposed == 1));
                Equal(0, changes.Subscribers);
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }
    }

    private static UdpFrame ProbePacket() => new("server", 1, "client", 2, new byte[] { 0xEE }, null);
    private sealed class TestNetworkChanges : INetworkChangeSource
    {
        public event Action? Changed;
        internal int Subscribers => Changed?.GetInvocationList().Length ?? 0;
        public void Start() { }
        public void Dispose() { }
        internal void Raise() => Changed?.Invoke();
    }
    private sealed class TestInterfaceCapture(string id, List<string> operations) : IUdpCapture
    {
        internal string Id => id;
        internal readonly Queue<UdpFrame> Packets = new();
        internal bool FailRead;
        internal bool FailStart;
        internal int Disposed;
        public void Start()
        {
            operations.Add("start:" + id + ":" + (operations.Count(x => x.StartsWith("start:" + id + ":")) + 1));
            if (FailStart) throw new IOException("start failed");
        }
        public UdpFrame? TryRead()
        {
            if (Disposed > 0) throw new Exception("读取了已关闭句柄");
            if (FailRead) throw new IOException("read failed");
            return Packets.TryDequeue(out var packet) ? packet : null;
        }
        public void Stop() => Dispose();
        public void Dispose()
        {
            Disposed++;
            operations.Add("dispose:" + id + ":" + Disposed);
            if (Disposed > 1) throw new Exception("重复关闭句柄");
        }
    }
}
