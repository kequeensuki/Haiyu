using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using ABI.Models;
using Google.Protobuf;
using Haiyu.ABI.Protocol;
using Haiyu.ABI.Services.GameDataExport.Decrypt;
using Haiyu.ABI.Services.GameDataExport.Export;
using Haiyu.ABI.Services.GameDataExport.Rebuild;

namespace ABIRuntime.TransportTests;

internal static partial class Program
{
    private static Task TestInventoryCaptureAsync()
    {
        using var rsa = RSA.Create(2048);
        using var loader = new SessionKeyLoader();
        loader.LoadPrivateKeyPem(rsa.ExportPkcs8PrivateKeyPem());
        var key = RandomNumberGenerator.GetBytes(32);
        using var state = new InventoryCaptureState(loader, GameDataExportScope.Inventory);
        var stream = new KcpStreamRebuilder();
        uint sn = 0;
        uint seq = 0;
        byte[] Frame(ushort id, byte[] plain, bool compressed = false)
        {
            var sequence = ++seq;
            var payload = plain.ToArray();
            if (compressed)
            {
                using var output = new MemoryStream();
                using (var zlib = new ZLibStream(output, CompressionLevel.Fastest, true))
                    zlib.Write(payload);
                payload = output.ToArray();
            }
            if (id != 112)
            {
                if (payload.Length > 0)
                    payload[sequence % (uint)payload.Length] ^= key[sequence % 32];
                using var aes = Aes.Create();
                aes.Key = key;
                payload = aes.EncryptEcb(payload, PaddingMode.PKCS7);
            }
            var headerLength = compressed ? 15 : 11;
            var bodyLength = headerLength + payload.Length;
            var bytes = new byte[bodyLength + 3];
            bytes[0] = (byte)bodyLength;
            bytes[1] = (byte)(bodyLength >> 8);
            bytes[2] = (byte)(bodyLength >> 16);
            bytes[3] = compressed ? (byte)0x14 : (byte)4;
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), sequence);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(8), id);
            if (compressed)
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(14), (uint)plain.Length);
            payload.CopyTo(bytes, headerLength + 3);
            return bytes;
        }
        KcpSegment Segment(uint number, byte[] data) =>
            new(1, KcpSegment.CmdPush, 0, 0, 0, number, 0, (uint)data.Length, data);
        void Feed(byte[] bytes)
        {
            stream.Add(Segment(sn++, bytes));
            state.Consume(stream);
        }
        Feed(Frame(112, new ProtoKeyResponse
        {
            Key = ByteString.CopyFrom(rsa.Encrypt(key, RSAEncryptionPadding.Pkcs1))
        }.ToByteArray()));
        Equal(true, loader.SessionKey!.SequenceEqual(key));

        var normal = new NormalItemResponse();
        for (var i = 0; i < 4096; i++)
            normal.NormalItemList.Add(new NormalItem { Id = i + 1, Count = 7 });
        var inventoryFrame = Frame(MsgIdFilter.NormalItemResponse, normal.ToByteArray(), true);
        Feed(inventoryFrame[..20]);
        Equal(0, state.DecodedFrames); // 半帧不能算已收齐，不能提前丢弃。
        Feed(inventoryFrame[20..]);
        Equal(1, state.DecodedFrames);
        Equal(4096, state.NormalItems[MsgIdFilter.NormalItemResponse].Count);
        Equal(0, stream.SegmentCount); // 快照独立持有解析结果，Compact 不会清空它。
        stream.Add(Segment(sn - 1, inventoryFrame[20..]));
        state.Consume(stream);
        Equal(1, state.DecodedFrames); // 已回收段的重传不能再解析。

        Feed(Frame(MsgIdFilter.WeaponItemResponse, new WeaponItemResponse().ToByteArray()));
        Feed(Frame(MsgIdFilter.PhantomItemResponse, new PhantomItemResponse().ToByteArray()));
        Feed(Frame(MsgIdFilter.ValidTimeItemResponse, new ValidTimeItemResponse().ToByteArray()));
        Equal(4, state.ParsedCategories);
        Equal(false, state.Ready); // 即使 4/4，也必须等 playerId。
        Feed(Frame(MsgIdFilter.BasicInfoNotify, new BasicInfoNotify { Id = 123456 }.ToByteArray(), true));
        Equal(123456, state.PlayerId.PlayerId);
        Equal(true, state.Ready);
        Equal(4, state.DecodedFrames);

        // 乱序：后半段先到，缺段补齐前不能破坏半帧边界。
        var update = new NormalItemResponse();
        update.NormalItemList.Add(new NormalItem { Id = 99, Count = 10 });
        var updatedFrame = Frame(MsgIdFilter.NormalItemResponse, update.ToByteArray());
        stream.Add(Segment(sn + 1, updatedFrame[10..]));
        state.Consume(stream);
        Equal(4, state.DecodedFrames);
        stream.Add(Segment(sn, updatedFrame[..10]));
        state.Consume(stream);
        sn += 2;
        Equal(5, state.DecodedFrames);
        Equal(1, state.NormalItems[MsgIdFilter.NormalItemResponse].Count);
        Equal(99, state.NormalItems[MsgIdFilter.NormalItemResponse][0].Id);

        var root = Path.Combine(Path.GetTempPath(), "haiyu-capture-" + Guid.NewGuid().ToString("N"));
        try
        {
            var result = new GameDataExportResult(true, "ok",
                new GameInventoryData(state.NormalItems.Values.SelectMany(x => x).ToList(), [], [], []),
                null, 10, state.DecodedFrames, null, state.PlayerId.PlayerId, 1);
            var path = InventoryJsonWriter.TryWriteToRoot(root, result, DateTimeOffset.Now)!;
            Equal("123456", Path.GetFileName(Path.GetDirectoryName(path)));
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            Equal(5, json.RootElement.GetProperty("stats").GetProperty("decodedFrames").GetInt32());
            Equal(99, json.RootElement.GetProperty("inventory").GetProperty("normalItems")
                .GetProperty("items")[0].GetProperty("id").GetInt32());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
        // 无效 protobuf 不计数、不覆盖此前有效快照。
        try
        {
            state.Consume(Frame(MsgIdFilter.NormalItemResponse, [0xFF, 0xFF]));
            throw new Exception("无效 protobuf 被误判为已解析");
        }
        catch (InvalidDataException) { }
        Equal(5, state.DecodedFrames);
        // 保存旧玩家快照，后续切号写入不得覆盖它。
        var oldSnapshot = new GameDataExportResult(true, "ok",
            new GameInventoryData(state.NormalItems.Values.SelectMany(x => x).ToList(), [], [], []),
            null, 10, state.DecodedFrames, null, state.PlayerId.PlayerId, 1);
        // 新的 112 换钥并隔离旧玩家快照；相同 112 重传不应再次清空快照。
        key = RandomNumberGenerator.GetBytes(32);
        var newKeyFrame = Frame(112, new ProtoKeyResponse
        {
            Key = ByteString.CopyFrom(rsa.Encrypt(key, RSAEncryptionPadding.Pkcs1))
        }.ToByteArray());
        Feed(newKeyFrame);
        Equal(true, loader.SessionKey!.SequenceEqual(key));
        Equal(false, state.Ready);
        Equal(0, state.ParsedCategories);
        var switchedItems = new NormalItemResponse();
        switchedItems.NormalItemList.Add(new NormalItem { Id = 777, Count = 12 });
        Feed(Frame(MsgIdFilter.NormalItemResponse, switchedItems.ToByteArray()));
        Feed(Frame(MsgIdFilter.BasicInfoNotify, new BasicInfoNotify { Id = 654321 }.ToByteArray()));
        Equal(654321, state.PlayerId.PlayerId);
        Equal(1, state.NormalItems[MsgIdFilter.NormalItemResponse].Count);
        Feed(Frame(MsgIdFilter.WeaponItemResponse, new WeaponItemResponse().ToByteArray()));
        Feed(Frame(MsgIdFilter.PhantomItemResponse, new PhantomItemResponse().ToByteArray()));
        Feed(Frame(MsgIdFilter.ValidTimeItemResponse, new ValidTimeItemResponse().ToByteArray()));
        Equal(true, state.Ready);
        var version = state.Version;
        Feed(newKeyFrame);
        Equal(true, state.Ready);
        Equal(version, state.Version);
        // 损坏的协商不能替换有效密钥或清除已解析快照。
        Feed(Frame(112, new ProtoKeyResponse { Key = ByteString.CopyFrom(new byte[256]) }.ToByteArray()));
        Equal(true, loader.SessionKey!.SequenceEqual(key));
        Equal(true, state.Ready);
        var switchRoot = Path.Combine(Path.GetTempPath(), "haiyu-switch-" + Guid.NewGuid().ToString("N"));
        try
        {
            var timestamp = DateTimeOffset.Now;
            var oldPath = InventoryJsonWriter.TryWriteToRoot(switchRoot, oldSnapshot, timestamp)!;
            var newSnapshot = oldSnapshot with
            {
                PlayerId = state.PlayerId.PlayerId,
                Inventory = new GameInventoryData(state.NormalItems.Values.SelectMany(x => x).ToList(), [], [], [])
            };
            var newPath = InventoryJsonWriter.TryWriteToRoot(switchRoot, newSnapshot, timestamp)!;
            Equal("123456", Path.GetFileName(Path.GetDirectoryName(oldPath)));
            Equal("654321", Path.GetFileName(Path.GetDirectoryName(newPath)));
            using var oldJson = JsonDocument.Parse(File.ReadAllText(oldPath));
            using var newJson = JsonDocument.Parse(File.ReadAllText(newPath));
            Equal(99, oldJson.RootElement.GetProperty("inventory").GetProperty("normalItems").GetProperty("items")[0].GetProperty("id").GetInt32());
            Equal(777, newJson.RootElement.GetProperty("inventory").GetProperty("normalItems").GetProperty("items")[0].GetProperty("id").GetInt32());
        }
        finally { if (Directory.Exists(switchRoot)) Directory.Delete(switchRoot, true); }
        return Task.CompletedTask;
    }
}
