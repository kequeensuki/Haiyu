using ABI.Models;
using Haiyu.ABI.Services.GameDataExport.Protocol;
using Haiyu.ABI.Services.GameDataExport.Events;
using Haiyu.ABI.Services.GameDataExport.Decrypt;
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
    events.Publish(new(GatewayMessageType.Push, 4, 1, 0, (ushort)id, 0, []), [26, 4, 8, 1, 16, 2], 123, "login", 1);
    var stats = await events.FinishAsync(null);
    var wire = MemoryPackSerializer.Serialize(stats, ABIMemoryPack.Options);
    var round = MemoryPackSerializer.Deserialize<GameDataExportEventStats>(wire, ABIMemoryPack.Options)!;
    if (!round.Complete || round.Written != 1 || !File.Exists(round.ManifestPath))
        throw new Exception("event or AOT ABI serialization failed");
    Console.WriteLine($"PASS NativeAOT protocol smoke: messages={catalog.Messages.Count}, registered={catalog.Ids.Count}, elapsedMs={watch.ElapsedMilliseconds}, managedBytes={GC.GetTotalMemory(true)}");
}
finally
{
    if (args.Contains("--keep")) Console.WriteLine("Manifest: " + Path.Combine(root, "captures", Directory.GetDirectories(Path.Combine(root, "captures")).Select(Path.GetFileName).Single()!, "manifest.json"));
    else Directory.Delete(root, true);
}
