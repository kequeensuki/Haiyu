using System.Buffers.Binary;
using System.Security.Cryptography;
using ABI.Models;
using Google.Protobuf;
using Haiyu.ABI.Protocol;
using Haiyu.ABI.Services.GameDataExport.Decrypt;
using Haiyu.ABI.Services.GameDataExport.Events;
using Haiyu.ABI.Services.GameDataExport.Protocol;
using Haiyu.ABI.Services.GameDataExport.Rebuild;
using Haiyu.ABI.Services.GameDataExport.Session;
using MemoryPack;

var watch = System.Diagnostics.Stopwatch.StartNew();
ABIMemoryPack.EnsureFormatters();
var catalog = ProtocolCatalog.Default.Value;
var decoder = new ProtocolJsonDecoder(catalog);
var normal = Haiyu.ABI.Protocol.NormalItemResponse.Parser.ParseFrom(new byte[] { 26, 4, 8, 1, 16, 2 });
if (normal.NormalItemList.Single().Count != 2)
    throw new Exception("typed parser failed");
var id = catalog.Ids.Single(p => p.Value == "Aki.Protocol.NormalItemResponse").Key;
if (decoder.Decode(id, [26, 4, 8, 1, 16, 2]).Status != "decoded")
    throw new Exception("generic decoder failed");
var root = Path.Combine(Path.GetTempPath(), "haiyu-protocol-smoke-" + Guid.NewGuid().ToString("N"));
try
{
    var events = new EventExportSession(root);
    using var rsa = RSA.Create(2048);
    var key = RandomNumberGenerator.GetBytes(32);
    var loader = new SessionKeyLoader();
    loader.LoadPrivateKeyPem(rsa.ExportPkcs8PrivateKeyPem());
    using var capture = new CaptureConnectionState(loader, GameDataExportScope.Inventory | GameDataExportScope.AllServerMessages)
    { Observer = events, ConnectionId = 1 };
    uint sequence = 0;
    byte[] Frame(ushort msgId, byte[] plain)
    {
        var seq = ++sequence;
        var payload = plain.ToArray();
        if (msgId != WavesMessageIds.ProtoKeyResponse)
        {
            if (payload.Length > 0) payload[seq % (uint)payload.Length] ^= key[seq % 32];
            using var aes = Aes.Create(); aes.Key = key;
            payload = aes.EncryptEcb(payload, PaddingMode.PKCS7);
        }
        var bytes = new byte[14 + payload.Length]; var size = bytes.Length - 3;
        bytes[0] = (byte)size; bytes[1] = (byte)(size >> 8); bytes[2] = (byte)(size >> 16); bytes[3] = 4;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), seq);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(8), msgId);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(10), Crc32.Compute(payload));
        payload.CopyTo(bytes, 14); return bytes;
    }
    capture.ConsumeRaw(Frame(WavesMessageIds.ProtoKeyResponse,
        new ProtoKeyResponse { Key = ByteString.CopyFrom(rsa.Encrypt(key, RSAEncryptionPadding.Pkcs1)) }.ToByteArray()));
    capture.ConsumeRaw(Frame(WavesMessageIds.BasicInfoNotify, [120, 123]));
    capture.ConsumeRaw(Frame((ushort)id, [26, 4, 8, 1, 16, 2]));
    capture.ConsumeRaw(Frame(WavesMessageIds.WeaponItemResponse, []));
    capture.ConsumeRaw(Frame(WavesMessageIds.PhantomItemResponse, []));
    capture.ConsumeRaw(Frame(WavesMessageIds.ValidTimeItemResponse, []));
    if (!capture.Ready || capture.Inventory.DecodedFrames != 4 || capture.Session.PlayerId.PlayerId != 123)
        throw new Exception("capture session pipeline failed");
    var stats = await events.FinishAsync(null);
    var wire = MemoryPackSerializer.Serialize(stats, ABIMemoryPack.Options);
    var round = MemoryPackSerializer.Deserialize<GameDataExportEventStats>(wire, ABIMemoryPack.Options)!;
    if (!round.Complete || round.Written != 6 || !File.Exists(round.ManifestPath))
        throw new Exception("event or AOT ABI serialization failed");
    Console.WriteLine($"PASS NativeAOT protocol smoke: messages={catalog.Messages.Count}, registered={catalog.Ids.Count}, elapsedMs={watch.ElapsedMilliseconds}, managedBytes={GC.GetTotalMemory(true)}");
}
finally
{
    if (args.Contains("--keep")) Console.WriteLine("Manifest: " + Path.Combine(root, "captures", Directory.GetDirectories(Path.Combine(root, "captures")).Select(Path.GetFileName).Single()!, "manifest.json"));
    else Directory.Delete(root, true);
}
