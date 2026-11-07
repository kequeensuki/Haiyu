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
using Haiyu.ABI.Services.GameDataExport.Modules;
using Haiyu.ABI.Services.GameDataExport.Storage;

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
    var context = new ExportCaptureContext(new JsonExportStore(root));
    var filtered = new GameDataExportModuleDescriptor("basic-info-events", "basic", "Basic info", false, false);
    var registry = new ExportModuleRegistry([
        new(InventoryExportModule.Definition, c => new InventoryExportModule(c)),
        new(ServerMessagesExportModule.Definition, c => new ServerMessagesExportModule(c)),
        new(filtered, c => new ServerMessagesExportModule(c, descriptor: filtered,
            messageIds: new HashSet<ushort> { WavesMessageIds.BasicInfoNotify }))
    ]);
    await using var coordinator = new ExportCoordinator(context, registry, ["inventory", "server-messages", filtered.Id]);
    using var rsa = RSA.Create(2048);
    var key = RandomNumberGenerator.GetBytes(32);
    var loader = new SessionKeyLoader();
    loader.LoadPrivateKeyPem(rsa.ExportPkcs8PrivateKeyPem());
    using var capture = new CaptureConnectionState(loader)
    { Observer = coordinator, ConnectionId = 1 };
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
    capture.ConsumeRaw(Frame(WavesMessageIds.WeaponItemAddNotify,
        new WeaponItemAddNotify { WeaponItemList = { new WeaponItem { Id = 10, IncrId = 101, WeaponLevel = 1 } } }.ToByteArray()));
    capture.ConsumeRaw(Frame(WavesMessageIds.WeaponLevelUpResponse,
        new WeaponLevelUpResponse { IncId = 101, WeaponLevel = 40, WeaponExp = 123 }.ToByteArray()));
    capture.ConsumeRaw(Frame(WavesMessageIds.PhantomItemAddNotify,
        new PhantomItemAddNotify { PhantomItemList = { new PhantomItem { Id = 30, IncrId = 301, StackCount = 3, AddTime = 42 } } }.ToByteArray()));
    capture.ConsumeRaw(Frame(WavesMessageIds.PhantomIdentifyResponse,
        new PhantomIdentifyResponse { UpdateInfo = new PhantomItem { Id = 30, IncrId = 301, PhantomLevel = 10 } }.ToByteArray()));
    capture.ConsumeRaw(Frame(WavesMessageIds.MailInfosNotify, new MailInfosNotify { MailInfos = {
        new PbMailInfo { Id = "mail-001", Title = "邮件", Content = "正文", Sender = "server", ReceivedTime = 123,
            Attachments = { new PbMailAttachment { Id = 1, Count = 2 } } } } }.ToByteArray()));
    capture.ConsumeRaw(Frame(WavesMessageIds.MailReadResponse,
        new MailReadResponse { Id = "mail-001", State = 1, ReadTime = 456, ExpiryTime = 789 }.ToByteArray()));
    capture.ConsumeRaw(Frame(WavesMessageIds.MailGetAttachmentResponse,
        new MailGetAttachmentResponse { SuccessIdMap = { { "mail-001", 3 } } }.ToByteArray()));
    capture.ConsumeRaw(Frame(WavesMessageIds.AchievementInfoResponse, new AchievementInfoResponse { FinishCount = 1, StarCount = 5,
        AchievementGroupInfoList = { new AchievementGroupInfo { AchievementGroupEntry = new AchievementGroupEntry { Id = 10 },
            AchievementEntryList = { new AchievementEntry { Id = 1, FinishTime = uint.MaxValue,
                Progress = new AchievementProgress { CurProgress = 10, TotalProgress = 10 } } } } } }.ToByteArray()));
    capture.ConsumeRaw(Frame(WavesMessageIds.AchievementProgressNotify, new AchievementProgressNotify {
        AchievementEntry = new AchievementEntry { Id = 1, FinishTime = uint.MaxValue, IsReceive = true,
            Progress = new AchievementProgress { CurProgress = 10, TotalProgress = 10 } } }.ToByteArray()));
    capture.ConsumeRaw(Frame(WavesMessageIds.PbGetRoleListNotify, new PbGetRoleListNotify { RoleList = {
        new roleInfo { RoleId = 1, Level = 90, Skills = { new ArrayIntInt { Key = 100, Value = 1 } },
            BaseProp = { new ArrayIntInt { Key = 1, Value = 100 } } } } }.ToByteArray()));
    capture.ConsumeRaw(Frame(WavesMessageIds.PhantomPutOnResponse, new PhantomPutOnResponse { EquipInfoList = {
        new RolePhantomEquipInfo { RoleId = 1, PhantomItemIncrId = { 301 } } } }.ToByteArray()));
    capture.ConsumeRaw(Frame(WavesMessageIds.PbRoleSkillLevelNotify, new PbRoleSkillLevelNotify { RoleId = 1,
        SkillInfo = new ArrayIntInt { Key = 100, Value = 10 } }.ToByteArray()));
    context.RememberLegacyMetadata(capture.Session);
    var modules = await coordinator.FinishAsync(new(null, false));
    var result = coordinator.LegacyResult(modules);
    var wire = MemoryPackSerializer.Serialize(result, ABIMemoryPack.Options);
    var round = MemoryPackSerializer.Deserialize<GameDataExportResult>(wire, ABIMemoryPack.Options)!;
    if (!round.Success || round.Modules?.Length != 3 || round.EventExport?.Written != 18
        || round.Roles?.Items.Single().Skills.Single().Value != 10
        || round.Roles.Items.Single().Echoes[0].Item?.PhantomLevel != 10
        || round.Roles.Items.Single().Attributes.Single().Value != 100
        || round.Inventory?.NormalItems.Single().Count != 2
        || round.Inventory.Weapons.Single().WeaponLevel != 40 || round.Inventory.Weapons.Single().WeaponExp != 123
        || round.Inventory.Phantoms.Single().PhantomLevel != 10 || round.Inventory.Phantoms.Single().StackCount != 3
        || round.Inventory.Phantoms.Single().AddTime != 42
        || round.Mails?.Items.Single().IsRead != true || round.Mails.Items.Single().AttachmentStatus != "claimed"
        || round.Mails.Items.Single().ReadTime != 456
        || round.Achievements?.Items.Single().RewardClaimed != true || round.Achievements.Items.Single().FinishTime != uint.MaxValue
        || round.Achievements.Groups.Single().RewardClaimed || !File.Exists(round.CaptureIndexPath)
        || !File.Exists(round.OutputJsonPath)) throw new Exception("module lifecycle or AOT ABI serialization failed");
    Console.WriteLine($"PASS NativeAOT protocol smoke: messages={catalog.Messages.Count}, registered={catalog.Ids.Count}, elapsedMs={watch.ElapsedMilliseconds}, managedBytes={GC.GetTotalMemory(true)}");
}
finally
{
    if (args.Contains("--keep")) Console.WriteLine("Manifest: " + Path.Combine(root, "captures", Directory.GetDirectories(Path.Combine(root, "captures")).Select(Path.GetFileName).Single()!, "manifest.json"));
    else Directory.Delete(root, true);
}
