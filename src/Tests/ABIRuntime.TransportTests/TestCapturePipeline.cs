using ABI.Models;
using Haiyu.ABI.Services.GameDataExport.Decrypt;
using Haiyu.ABI.Services.GameDataExport.Export;
using Haiyu.ABI.Services.GameDataExport.Modules;
using Haiyu.ABI.Services.GameDataExport.Rebuild;
using Haiyu.ABI.Services.GameDataExport.Session;

namespace ABIRuntime.TransportTests;

/// <summary>旧线协议回归夹具：通过观察者组合诊断快照，不向生产传输类添加业务 API。</summary>
internal sealed class TestCapturePipeline : IGameMessageObserver, IDisposable
{
    private readonly CaptureConnectionState _capture;
    private readonly bool _events;
    internal InventoryCaptureState Inventory { get; }
    internal GameSessionContext Session => _capture.Session;
    internal uint ConnectionId { get => _capture.ConnectionId; set => _capture.ConnectionId = value; }
    internal IGameMessageObserver? Observer { get; set; }
    internal bool Ready => Session.HasVerifiedTraffic && Session.IsIdentityConfirmed && Inventory.IsComplete;
    internal TestCapturePipeline(SessionKeyLoader loader, GameDataExportScope scope)
        : this(new CaptureConnectionState(loader), new InventoryCaptureState(scope), scope.HasFlag(GameDataExportScope.AllServerMessages)) { }
    private TestCapturePipeline(CaptureConnectionState capture, InventoryCaptureState inventory, bool events)
    { _capture = capture; Inventory = inventory; _events = events; capture.Observer = this; }
    internal void ConsumeRaw(byte[] bytes) => _capture.ConsumeRaw(bytes);
    internal int Consume(byte[] bytes) => _capture.Consume(bytes);
    internal void Consume(KcpStreamRebuilder stream) => _capture.Consume(stream);
    public void Observe(MessageObservation message)
    {
        Observer?.Observe(message);
        if (message.Status is not null || message.Plain is null) return;
        var result = Inventory.Apply(message.Frame.MsgId, message.Plain);
        if (result == InventoryMessageResult.Invalid && !_events) throw new InvalidDataException("诊断快照解析失败。");
    }
    public void Bind(string sessionId, int playerId) => Observer?.Bind(sessionId, playerId);
    public void SessionChanged(ExportSessionChange change)
    {
        if (change.Kind is ExportSessionChangeKind.LoginStarted or ExportSessionChangeKind.Logout
            || change.Kind == ExportSessionChangeKind.IdentityResolved && change.SessionId != change.PreviousSessionId) Inventory.Reset();
        Observer?.SessionChanged(change);
    }
    internal TestCapturePipeline ForkForReconnect() => new(_capture.ForkForReconnect(), Inventory.ForkForReconnect(), _events) { Observer = Observer };
    public void Dispose() => _capture.Dispose();
}
