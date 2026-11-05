using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using ABI.Models;
using Google.Protobuf;
using Haiyu.ABI.Protocol;
using Haiyu.ABI.Services.GameDataExport.Decrypt;
using Haiyu.ABI.Services.GameDataExport.Events;
using Haiyu.ABI.Services.GameDataExport.Export;
using Haiyu.ABI.Services.GameDataExport.Protocol;
using Haiyu.ABI.Services.GameDataExport.Rebuild;
using Haiyu.ABI.Services.GameDataExport.Session;

namespace ABIRuntime.TransportTests;
internal static partial class Program
{
    private static Task TestFullProtocolAsync()
    {
        var catalog = ProtocolCatalog.Default.Value;
        Equal(true, catalog.Ids.Values.All(catalog.Messages.ContainsKey));
        Equal(true, catalog.Messages.Count > 5000 && catalog.Ids.Count > 3000);
        var decoder = new ProtocolJsonDecoder(catalog);
        var plaintextFrame = new GatewayFrame(GatewayMessageType.Push, 4, 1, 0, 1, Crc32.Compute(new byte[] { 8, 1 }), new byte[]{8,1});
        Equal(true, FrameCipher.TryDecrypt(plaintextFrame, new byte[32], out var plaintext, out _));
        Equal(true, plaintext!.SequenceEqual(plaintextFrame.Payload));
        // 独立手写 wire 向量：sint32(-1)、fixed32(0x12345678)、sfixed32(-2)、int64(2^53+1)。
        byte[] numeric = [32, 1, 77, 0x78, 0x56, 0x34, 0x12, 93, 0xfe, 0xff, 0xff, 0xff, 48, 0x81, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x10];
        var result = decoder.DecodeType("Aki.Protocol.NetTestData", numeric);
        Equal("decoded", result.Status);
        using (var doc = JsonDocument.Parse(result.Json!))
        {
            Equal(-1, doc.RootElement.GetProperty("dataSInt32").GetInt32());
            Equal(0x12345678U, doc.RootElement.GetProperty("dataFixed32").GetUInt32());
            Equal(-2, doc.RootElement.GetProperty("dataSFixed32").GetInt32());
            Equal("9007199254740993", doc.RootElement.GetProperty("dataInt64").GetString());
        }

        // packed sint32 字段 5；不能误用 int32 解码为正数 1。
        var guide = catalog.Ids.Single(p => p.Value == "Aki.Protocol.AddUnlockedGuideNotify").Key;
        result = decoder.Decode(guide, [42, 2, 1, 4]);
        using (var doc = JsonDocument.Parse(result.Json!))
        {
            Equal(-1, doc.RootElement.GetProperty("unlockedGuideIds")[0].GetInt32());
            Equal(2, doc.RootElement.GetProperty("unlockedGuideIds")[1].GetInt32());
        }

        // map<int32,int32>，相同 key 后值覆盖；额外未知字段必须被标记。
        result = decoder.DecodeType("Aki.Protocol.RoguelikeGiveUpGainResponse", [66, 4, 8, 7, 16, 9, 66, 4, 8, 7, 16, 10, 0xf8, 0x3e, 1]);
        Equal("decoded_with_unknown_fields", result.Status);
        using (var doc = JsonDocument.Parse(result.Json!))
            Equal(10, doc.RootElement.GetProperty("itemMap").GetProperty("7").GetInt32());
        // oneof 从 scalar 切至 message 后旧分支不保留。
        result = decoder.DecodeType("Aki.Protocol.EntityStaticHookMoveRequest", [16, 1, 122, 0]);
        using (var doc = JsonDocument.Parse(result.Json!))
        {
            Equal(false, doc.RootElement.TryGetProperty("targetEntityId", out _));
            Equal(JsonValueKind.Object, doc.RootElement.GetProperty("targetPos").ValueKind);
        }

        Equal("unknown_message", decoder.Decode(65535, [8, 1]).Status);
        Equal("parse_failed", decoder.Decode(guide, [42, 127, 1]).Status);
        Equal("parse_failed", decoder.Decode(guide, [0]).Status);
        using var state = new CaptureConnectionState(new SessionKeyLoader(), GameDataExportScope.None);
        var faults = new List<string>();
        state.Observer = new TestMessageObserver(m => faults.Add(m.Status!));
        var invalid = new byte[14]; invalid[0] = 11;
        state.ConsumeRaw(invalid);
        state.ConsumeRaw(new byte[]{1,2});
        Equal(true, faults.SequenceEqual(new[]{"invalid_frame", "incomplete_raw_frame"}));
        return Task.CompletedTask;
    }

    private static async Task TestEventFilesAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "haiyu-events-" + Guid.NewGuid().ToString("N"));
        try
        {
            var events = new EventExportSession(root, new(QueueBytes: 1024 * 1024, ShardBytes: 700, CaptureBytes: 1024 * 1024));
            var frame = new GatewayFrame(GatewayMessageType.Push, 4, 1, 0, 65535, 0, [8, 1]);
            PublishEvent(events, frame, frame.Payload, null, "login-a", 1);
            events.Bind("login-a", 111);
            PublishEvent(events, frame with { SeqNo = 2 }, frame.Payload, 111, "login-a", 1);
            PublishEvent(events, frame with { SeqNo = 3 }, frame.Payload, 222, "login-b", 2);
            var stats = await events.FinishAsync(null);
            Equal(true, stats.Complete);
            Equal(3L, stats.Written);
            Equal(0L, stats.Unresolved);
            Equal(2L, stats.Players.Single(p => p.PlayerId == 111).Written);
            using (var manifest = JsonDocument.Parse(File.ReadAllBytes(stats.ManifestPath)))
                foreach (var p in manifest.RootElement.GetProperty("files").EnumerateArray())
                {
                    var file = Path.Combine(root, p.GetString()!);
                    foreach (var line in File.ReadLines(file))
                    {
                        using var doc = JsonDocument.Parse(line);
                        Equal("unknown_message", doc.RootElement.GetProperty("decodeStatus").GetString());
                        Equal("CAE=", doc.RootElement.GetProperty("rawPayload").GetString());
                    }
                }

            var overflow = new EventExportSession(Path.Combine(root, "overflow"), new(QueueBytes: 1));
            try
            {
                PublishEvent(overflow, frame, frame.Payload, null, "x", 1);
                throw new Exception("队列上限未触发");
            }
            catch (IOException)
            {
            }

            var failed = await overflow.FinishAsync(null);
            Equal(false, failed.Complete);
            Equal(1L, failed.Dropped);
            Equal(0L, failed.Written);
            var disk = new EventExportSession(Path.Combine(root, "disk"), new(CaptureBytes: 1));
            PublishEvent(disk, frame, frame.Payload, null, "x", 1);
            failed = await disk.FinishAsync(null);
            Equal(false, failed.Complete);
            Equal(0L, failed.Written);
            Equal(true, failed.Error != null);
            // 合成持续流量：不把全部事件留在内存，也不能按相同 payload 合并合法消息。
            // 重连候选上的未知帧不能借用旧玩家；有效的非背包消息也能确认新连接。
            using var rsa = RSA.Create(2048);
            var keyLoader = new SessionKeyLoader();
            keyLoader.LoadPrivateKeyPem(rsa.ExportPkcs8PrivateKeyPem());
            keyLoader.TryResolve(new(GatewayMessageType.Push,4,1,0,112,0,
                new ProtoKeyResponse { Key = ByteString.CopyFrom(rsa.Encrypt(new byte[32], RSAEncryptionPadding.Pkcs1)) }.ToByteArray()));
            using var parent = new CaptureConnectionState(keyLoader, GameDataExportScope.Inventory | GameDataExportScope.AllServerMessages);
            parent.Session.PlayerId.TryFeed(new(GatewayMessageType.Push,4,1,0,15380,0,new BasicInfoNotify{Id=123}.ToByteArray()));
            uint gatewaySequence = 0;
            parent.Observer = new TestMessageObserver(_ => { });
            parent.ConsumeRaw(Gateway(WavesMessageIds.LoginResponse, []));
            Equal(PlayerIdResolver.Phase.Resolved, parent.Session.PlayerId.Current);
            parent.ConsumeRaw(Gateway(WavesMessageIds.LogoutNotify, []));
            parent.ConsumeRaw(Gateway(WavesMessageIds.BasicInfoNotify, new BasicInfoNotify{Id=456}.ToByteArray()));
            parent.ConsumeRaw(Gateway(WavesMessageIds.LoginResponse, []));
            Equal(PlayerIdResolver.Phase.Resolved, parent.Session.PlayerId.Current);
            Equal(456, parent.Session.PlayerId.PlayerId);
            using var candidate = parent.ForkForReconnect();
            var candidateEvents = new EventExportSession(Path.Combine(root,"candidate"));
            candidate.Observer = candidateEvents;
            candidate.ConnectionId = 2;
            byte[] Gateway(ushort id, byte[] body)
            {
                var bytes=new byte[14+body.Length];var size=bytes.Length-3;
                bytes[0]=(byte)size;bytes[1]=(byte)(size>>8);bytes[2]=(byte)(size>>16);bytes[3]=4;
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4),++gatewaySequence);
                System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(8),id);
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(10),Crc32.Compute(body));
                body.CopyTo(bytes,14);return bytes;
            }
            candidate.ConsumeRaw(Gateway(65535,[8,1]));Equal(false,candidate.Session.HasVerifiedTraffic);
            var guideId=(ushort)ProtocolCatalog.Default.Value.Ids.Single(p=>p.Value=="Aki.Protocol.AddUnlockedGuideNotify").Key;
            candidate.ConsumeRaw(Gateway(guideId,[42,2,1,4]));Equal(true,candidate.Session.HasVerifiedTraffic);
            var candidateStats=await candidateEvents.FinishAsync(null);
            Equal(1L,candidateStats.Unresolved);Equal(1L,candidateStats.Players.Single().Written);
            var memoryBefore = GC.GetTotalMemory(true);
            var timer = System.Diagnostics.Stopwatch.StartNew();
            var stress = new EventExportSession(Path.Combine(root, "stress"));
            for (var i = 0; i < 50_000; i++)
            {
                PublishEvent(stress, frame with { SeqNo = (uint)i + 1 }, frame.Payload, 123, "stress-login", 1);
                if (i % 16 == 0) await Task.Delay(1);
            }
            var sustained = await stress.FinishAsync(null);
            var retained = GC.GetTotalMemory(true) - memoryBefore;
            Equal(true, sustained.Complete); Equal(50_000L, sustained.Written);
            Equal(true, retained < 16 * 1024 * 1024);
            Console.WriteLine($"INFO event stress: rows={sustained.Written}, bytes={sustained.BytesWritten}, elapsedMs={timer.ElapsedMilliseconds}, retainedBytes={retained}");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
