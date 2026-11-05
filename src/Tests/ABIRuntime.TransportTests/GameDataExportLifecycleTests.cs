using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.Cryptography;
using ABI.Models;
using ABIRuntime.Abstractions;
using ABIRuntime.Runtime;
using ABIRuntime;
using Google.Protobuf;
using Haiyu.ABI.Protocol;
using Haiyu.ABI.Services.GameDataExport.Capture;
using Haiyu.ABI.Services.GameDataExport.Decrypt;
using Haiyu.ABI.Services.GameDataExport.Export;
using Haiyu.ABI.Services.GameDataExport.Rebuild;
using Haiyu.ABI.Services.GameDataExport;
using MemoryPack;
using Microsoft.Extensions.DependencyInjection;

namespace ABIRuntime.TransportTests;

internal static partial class Program
{
    private static Task TestCaptureLimitsAsync()
    {
        var clock = new CaptureClock();
        var stream = new KcpStreamRebuilder(1024, TimeSpan.FromSeconds(30), clock);
        KcpSegment Segment(uint sn, int length) => new(1, 81, 0, 0, 0, sn, 0, (uint)length, new byte[length]);
        stream.Add(Segment(1, 100));
        Equal(0, stream.BuildIncremental().Length);
        clock.Advance(TimeSpan.FromSeconds(31));
        try { stream.ValidatePending(); throw new Exception("永久缺口未超时"); }
        catch (InvalidDataException) { }
        stream.Clear();
        stream.Add(Segment(1, 1024));
        try { stream.Add(Segment(2, 1)); throw new Exception("缓存未受限"); }
        catch (InvalidDataException) { }
        stream.Clear();
        // 缺口及时补齐后，旧缺口的计时不能污染后续视图。
        stream.Add(Segment(1, 100));
        stream.BuildIncremental();
        clock.Advance(TimeSpan.FromSeconds(20));
        stream.Add(Segment(0, 100));
        Equal(200, stream.BuildIncremental().Length);
        stream.AdvanceConsumed(200);
        stream.Compact();
        clock.Advance(TimeSpan.FromSeconds(20));
        stream.ValidatePending();
        Equal(0, stream.SegmentCount);
        return Task.CompletedTask;
    }

    private static async Task TestExportPersistenceRetryAsync()
    {
        using var rsa = RSA.Create(2048);
        var key = RandomNumberGenerator.GetBytes(32);
        var packets = new Queue<UdpFrame>();
        UdpFrame Packet(byte[] bytes) => new("server", 13108, "client", 45678, bytes, null);
        var handshake = new byte[13];
        handshake[0] = 0xEE;
        BinaryPrimitives.WriteUInt32LittleEndian(handshake.AsSpan(1), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(handshake.AsSpan(9), 1000);
        packets.Enqueue(Packet(handshake));
        void Frame(ushort id, byte[] payload)
        {
            if (id != 112)
            {
                // seq=0，与生产的按序号 XOR 解密相匹配。
                if (payload.Length > 0) payload[0] ^= key[0];
                using var aes = Aes.Create();
                aes.Key = key;
                payload = aes.EncryptEcb(payload, PaddingMode.PKCS7);
            }
            var frame = new byte[14 + payload.Length];
            var length = frame.Length - 3;
            frame[0] = (byte)length; frame[1] = (byte)(length >> 8); frame[2] = (byte)(length >> 16);
            frame[3] = 4;
            BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(8), id);
            BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(10), Crc32.Compute(payload));
            payload.CopyTo(frame, 14);
            var raw = new byte[5 + frame.Length];
            raw[0] = 0xEC;
            BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(1), 1);
            frame.CopyTo(raw, 5);
            packets.Enqueue(Packet(raw));
        }
        Frame(112, new ProtoKeyResponse { Key = ByteString.CopyFrom(rsa.Encrypt(key, RSAEncryptionPadding.Pkcs1)) }.ToByteArray());
        Frame(15380, new BasicInfoNotify { Id = 123456 }.ToByteArray());
        var normal = new NormalItemResponse();
        normal.NormalItemList.Add(new NormalItem { Id = 99, Count = 7 });
        Frame(22445, normal.ToByteArray());
        Frame(27089, new WeaponItemResponse().ToByteArray());
        Frame(26275, new PhantomItemResponse().ToByteArray());
        Frame(20447, new ValidTimeItemResponse().ToByteArray());
        var root = Path.Combine(Path.GetTempPath(), "haiyu-retry-" + Guid.NewGuid().ToString("N"));
        // 真实目录创建失败，第一次尝试应返回 null；恢复后无需新数据即可自动补写。
        File.WriteAllText(root, "blocked directory");
        var capture = new ReplayCapture(packets);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var attempts = 0;
        var ready = false;
        var dataUpdated = false;
        try
        {
            var service = new GameDataExportService(() => capture,
                loader => loader.LoadPrivateKeyPem(rsa.ExportPkcs8PrivateKeyPem()),
                (result, timestamp) =>
                {
                    attempts++;
                    if (attempts == 2) File.Delete(root);
                    var path = InventoryJsonWriter.TryWriteToRoot(root, result, timestamp);
                    if (path is not null) timeout.Cancel();
                    return path;
                });
            var result = await service.ExecuteAsync(new(GameDataExportScope.Inventory),
                new InlineProgress<GameDataExportProgress>(p => { ready |= p.CaptureReady; dataUpdated |= p.HasDataUpdate; }), timeout.Token);
            Equal(2, attempts);
            Equal(true, ready && dataUpdated && capture.Disposed);
            Equal(true, result.Success);
            Equal(1, result.UpdateCount);
            Equal(4, result.DecodedFrames);
            Equal(123456, result.PlayerId);
            Equal(true, File.Exists(result.OutputJsonPath));
        }
        finally
        {
            if (File.Exists(root)) File.Delete(root);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static async Task TestExportStopResultAsync()
    {
        // 使用真实 ElevatedHost + 命名管道：取消控制帧 -> 服务收尾 -> Result。
        var controlName = "Haiyu.Stop.Control." + Guid.NewGuid().ToString("N");
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        await using var control = new NamedPipeServerStream(controlName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await using var provider = new ServiceCollection()
            .AddPrivilegedService<GameDataExportRequest, GameDataExportProgress, GameDataExportResult, StopExportService>(Contract.GameDataExportContract)
            .BuildServiceProvider();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var host = ElevatedHost.RunForTestAsync(controlName, secret, provider);
        await control.WaitForConnectionAsync(timeout.Token);
        await PipeProtocol.ReadAsync(control, timeout.Token);
        var requestId = Guid.NewGuid();
        var requestName = "Haiyu.Stop.Request." + Guid.NewGuid().ToString("N");
        await using var request = new NamedPipeServerStream(requestName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await PipeProtocol.WriteAsync(control, new(PipeMessageKind.Request, requestId, 2,
            OperationNames.OpenRequest, MemoryPackSerializer.Serialize(new OpenRequestMessage(requestName, secret))), timeout.Token);
        await request.WaitForConnectionAsync(timeout.Token);
        await PipeProtocol.ReadAsync(request, timeout.Token);
        await PipeProtocol.WriteAsync(request, new(PipeMessageKind.Request, requestId, 2,
            Contract.GameDataExportContract.Operation, MemoryPackSerializer.Serialize(new GameDataExportRequest(GameDataExportScope.Inventory))), timeout.Token);
        // 服务先确认已开始，确保取消真的发生在业务执行中。
        Equal(PipeMessageKind.Progress, (await PipeProtocol.ReadAsync(request, timeout.Token)).Kind);
        await PipeProtocol.WriteAsync(request, new(PipeMessageKind.Cancel, requestId, 2,
            Contract.GameDataExportContract.Operation, []), timeout.Token);
        var message = await PipeProtocol.ReadAsync(request, timeout.Token);
        Equal(PipeMessageKind.Result, message.Kind);
        var result = MemoryPackSerializer.Deserialize<GameDataExportResult>(message.Payload)!;
        Equal(true, result.Success);
        Equal(123456, result.PlayerId);
        control.Dispose();
        Equal(0, await host.WaitAsync(timeout.Token));
    }

    public sealed class StopExportService : IPrivilegedService<GameDataExportRequest, GameDataExportProgress, GameDataExportResult>
    {
        public PrivilegedServiceContract<GameDataExportRequest, GameDataExportProgress, GameDataExportResult> Contract => ABIRuntime.Contract.GameDataExportContract;
        public async ValueTask<GameDataExportResult> ExecuteAsync(GameDataExportRequest request, IProgress<GameDataExportProgress> progress, CancellationToken token)
        {
            progress.Report(new(GameDataExportStage.Capturing, 5, "ready", true));
            try { await Task.Delay(Timeout.Infinite, token); } catch (OperationCanceledException) { }
            return new(true, "已收尾", null, null, 0, 4, null, 123456, 1);
        }
    }
    private sealed class ReplayCapture(Queue<UdpFrame> packets) : IUdpCapture
    {
        internal bool Disposed;
        public void Start() { }
        public UdpFrame? TryRead() => packets.TryDequeue(out var packet) ? packet : null;
        public void Stop() { }
        public void Dispose() => Disposed = true;
    }
    private sealed class CaptureClock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        internal void Advance(TimeSpan elapsed) => _ticks += elapsed.Ticks;
    }
}
