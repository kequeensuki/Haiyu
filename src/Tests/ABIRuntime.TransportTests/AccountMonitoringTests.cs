using System.Buffers.Binary;
using System.Security.Cryptography;
using ABI.Models;
using Google.Protobuf;
using Haiyu.ABI.Protocol;
using Haiyu.ABI.Services.GameDataExport;
using Haiyu.ABI.Services.GameDataExport.Capture;
using Haiyu.ABI.Services.GameDataExport.Export;
using Haiyu.ABI.Services.GameDataExport.Events;

namespace ABIRuntime.TransportTests;

internal static partial class Program
{
    private static async Task TestAccountMonitoringAsync()
    {
        foreach (var switchBack in new[] { false, true })
            await VerifyAccountMonitoringAsync(switchBack);
        await VerifyAccountMonitoringAsync(false, failSecond: true);
    }

    private static async Task VerifyAccountMonitoringAsync(bool switchBack, bool failSecond = false)
    {
        using var rsa = RSA.Create(2048);
        var firstKey = RandomNumberGenerator.GetBytes(32);
        var secondKey = RandomNumberGenerator.GetBytes(32);
        var packets = new Queue<UdpFrame>();
        uint sequence = 0;
        UdpFrame Packet(byte[] bytes) => new("server", 13108, "client", 45678, bytes, null);
        byte[] Frame(ushort id, byte[] payload, byte[] key)
        {
            var seq = sequence++;
            if (id != 112)
            {
                if (payload.Length > 0) payload[seq % (uint)payload.Length] ^= key[seq % 32];
                using var aes = Aes.Create(); aes.Key = key;
                payload = aes.EncryptEcb(payload, PaddingMode.PKCS7);
            }
            var bytes = new byte[14 + payload.Length]; var length = bytes.Length - 3;
            bytes[0] = (byte)length; bytes[1] = (byte)(length >> 8); bytes[2] = (byte)(length >> 16);
            bytes[3] = 4; BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), seq);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(8), id); payload.CopyTo(bytes, 14);
            return bytes;
        }
        void Raw(uint conv, ushort id, byte[] payload, byte[] key)
        {
            var frame = Frame(id, payload, key); var bytes = new byte[5 + frame.Length]; bytes[0] = 0xEC;
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(1), conv); frame.CopyTo(bytes, 5);
            packets.Enqueue(Packet(bytes));
        }
        void Login(uint conv, int player, byte[] key)
        {
            var handshake = new byte[13]; handshake[0] = 0xEE;
            BinaryPrimitives.WriteUInt32LittleEndian(handshake.AsSpan(1), conv);
            BinaryPrimitives.WriteUInt32LittleEndian(handshake.AsSpan(9), 1000);
            packets.Enqueue(Packet(handshake));
            Raw(conv, 112, new ProtoKeyResponse { Key = ByteString.CopyFrom(rsa.Encrypt(key, RSAEncryptionPadding.Pkcs1)) }.ToByteArray(), key);
            Raw(conv, 15380, new BasicInfoNotify { Id = player }.ToByteArray(), key);
            var normal = new NormalItemResponse(); normal.NormalItemList.Add(new NormalItem { Id = player, Count = 7 });
            Raw(conv, 22445, normal.ToByteArray(), key);
            Raw(conv, 27089, new WeaponItemResponse().ToByteArray(), key);
            Raw(conv, 26275, new PhantomItemResponse().ToByteArray(), key);
            Raw(conv, 20447, new ValidTimeItemResponse().ToByteArray(), key);
        }
        Login(1, 111111, firstKey);
        var root = Path.Combine(Path.GetTempPath(), "haiyu-account-ui-" + Guid.NewGuid().ToString("N"));
        var reports = new List<GameDataExportProgress>();
        var writes = 0;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        try
        {
            var service = new GameDataExportService(() => new ReplayCapture(packets),
                loader => loader.LoadPrivateKeyPem(rsa.ExportPkcs8PrivateKeyPem()), (result, timestamp) =>
                {
                    writes++;
                    if (failSecond && writes == 2) throw new IOException("模拟后续账号导出异常");
                    var path = InventoryJsonWriter.TryWriteToRoot(root, result, timestamp);
                    if (writes == 1) Login(2, 222222, secondKey);
                    else if (writes == 2 && switchBack) Login(3, 111111, firstKey);
                    else
                    {
                        // 旧连接后台数据不能把当前账号状态切回去。
                        var frame = Frame(999, new byte[] { 1, 2, 3 }, firstKey);
                        var old = new byte[24 + frame.Length]; BinaryPrimitives.WriteUInt32LittleEndian(old, 1); old[4] = 81;
                        BinaryPrimitives.WriteUInt32LittleEndian(old.AsSpan(20), (uint)frame.Length); frame.CopyTo(old, 24);
                        packets.Enqueue(Packet(old));
                        if (!switchBack)
                        {
                            var lateNormal = new NormalItemResponse();
                            lateNormal.NormalItemList.Add(new NormalItem { Id = 111111, Count = 999 });
                            Raw(1, 22445, lateNormal.ToByteArray(), firstKey);
                        }
                        timeout.CancelAfter(TimeSpan.FromMilliseconds(2400));
                    }
                    return path;
                }, () => new EventExportSession(root));
            var result = await service.ExecuteAsync(new(GameDataExportScope.Inventory | GameDataExportScope.AllServerMessages),
                new InlineProgress<GameDataExportProgress>(reports.Add), timeout.Token);
            if (failSecond)
            {
                Equal(false, result.Success);
                Equal(false, result.EventExport!.Complete);
                Equal(true, File.Exists(result.EventExport.ManifestPath));
                Equal(1, result.PlayerSummaries!.Length);
                Equal(111111, result.PlayerSummaries[0].PlayerId);
                Equal(true, File.Exists(result.PlayerSummaries[0].OutputJsonPath));
                return;
            }
            Equal(true, result.Success);
            Equal(true, result.EventExport!.Complete);
            Equal(result.EventExport.Received, result.EventExport.Written);
            Equal(0L, result.EventExport.Unresolved);
            Equal(2, result.EventExport.Players.Length);
            using (var manifest = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(result.EventExport.ManifestPath)))
            {
                Equal("completed", manifest.RootElement.GetProperty("state").GetString());
                Equal(switchBack ? 3 : 2, manifest.RootElement.GetProperty("sessionBindings").EnumerateObject().Count());
                Equal(true, manifest.RootElement.GetProperty("files").GetArrayLength() >= 3);
            }
            Equal(switchBack ? 3 : 2, writes);
            Equal(2, result.PlayerSummaries!.Length);
            var first = result.PlayerSummaries.Single(s => s.PlayerId == 111111);
            var second = result.PlayerSummaries.Single(s => s.PlayerId == 222222);
            Equal(switchBack ? 2 : 1, first.UpdateCount);
            Equal(1, second.UpdateCount);
            foreach (var summary in result.PlayerSummaries)
            {
                Equal(1, summary.NormalItemCount);
                Equal(0, summary.WeaponCount);
                Equal(0, summary.PhantomCount);
                Equal(0, summary.ValidTimeItemCount);
                Equal(true, File.Exists(summary.OutputJsonPath));
                Equal(true, summary.OutputJsonPath.Contains(summary.PlayerId.ToString()));
            }
            var packed = MemoryPack.MemoryPackSerializer.Serialize(result, ABIMemoryPack.Options);
            var restored = MemoryPack.MemoryPackSerializer.Deserialize<GameDataExportResult>(packed, ABIMemoryPack.Options)!;
            Equal(true, result.PlayerSummaries.SequenceEqual(restored.PlayerSummaries!));
            var json = System.Text.Json.JsonSerializer.Serialize(result, ABIJsonContext.Default.GameDataExportResult);
            var jsonResult = System.Text.Json.JsonSerializer.Deserialize(json, ABIJsonContext.Default.GameDataExportResult)!;
            Equal(true, result.PlayerSummaries.SequenceEqual(jsonResult.PlayerSummaries!));
            var acquired = reports.Single(p => p.SessionKeyAcquired);
            var wire = MemoryPack.MemoryPackSerializer.Serialize(acquired);
            Equal(true, MemoryPack.MemoryPackSerializer.Deserialize<GameDataExportProgress>(wire)!.SessionKeyAcquired);
            var monitoring = reports.Where(p => p.Stage == GameDataExportStage.Monitoring).ToArray();
            Equal(111111, monitoring[0].PlayerId);
            Equal(switchBack ? 111111 : 222222, monitoring[^1].PlayerId);
            Equal(switchBack ? 2 : 1, monitoring[^1].UpdateCount);
            Equal(1, monitoring[0].UpdateCount);
            Equal(true, monitoring.Where(p => p.PlayerId == 222222).All(p => p.UpdateCount == 1));
            Equal(switchBack ? 2 : 1, result.UpdateCount);
            Equal(result.OutputJsonPath!, monitoring[^1].OutputJsonPath!);
            Equal(true, monitoring.Count(p => p.PlayerId == result.PlayerId) >= 2); // 写入和后续心跳均更新。
            Equal(false, reports.Any(p => p.Stage == GameDataExportStage.Completed)); // 单次快照不是会话完成。
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
