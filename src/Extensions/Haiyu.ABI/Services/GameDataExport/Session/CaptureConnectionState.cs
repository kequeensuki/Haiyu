using ABI.Models;
using Haiyu.ABI.Protocol;
using Haiyu.ABI.Services.GameDataExport.Decrypt;
using Haiyu.ABI.Services.GameDataExport.Export;
using Haiyu.ABI.Services.GameDataExport.Protocol;
using Haiyu.ABI.Services.GameDataExport.Rebuild;

namespace Haiyu.ABI.Services.GameDataExport.Session;

/// <summary>单消费者传输管线：切帧、验证、解密，向独立的背包和事件消费者分发。</summary>
internal sealed class CaptureConnectionState : IDisposable
{
    private readonly HashSet<string> _rawSeen = new();
    private readonly Queue<string> _rawOrder = new();
    private readonly bool _exportEvents;
    private ProtocolJsonDecoder? _decoder;

    internal CaptureConnectionState(SessionKeyLoader keyLoader, GameDataExportScope scope)
        : this(new GameSessionContext(keyLoader), new InventoryCaptureState(scope), scope.HasFlag(GameDataExportScope.AllServerMessages)) { }

    private CaptureConnectionState(GameSessionContext session, InventoryCaptureState inventory, bool exportEvents)
    {
        Session = session;
        Inventory = inventory;
        _exportEvents = exportEvents;
    }

    internal GameSessionContext Session { get; }
    internal InventoryCaptureState Inventory { get; }
    internal uint ConnectionId { get; set; }
    internal IGameMessageObserver? Observer { get; set; }
    internal bool Ready => Session.HasVerifiedTraffic && Session.IsIdentityConfirmed && Inventory.IsComplete;

    internal void Consume(KcpStreamRebuilder stream)
    {
        var bytes = stream.BuildIncremental();
        stream.AdvanceConsumed(Consume(bytes));
        stream.Compact();
    }

    internal int Consume(byte[] bytes)
    {
        var offset = 0;
        while (offset < bytes.Length)
        {
            var before = offset;
            if (!FrameSplitter.TryTake(bytes, ref offset, out var frame) || frame is null)
            {
                if (offset == before) break;
                Reject(bytes.AsSpan(before, offset - before).ToArray(), "invalid_frame");
                continue;
            }
            if (!FrameIntegrity.IsValid(frame))
            {
                Observe(frame, null, "crc_failed");
                continue;
            }
            if (frame.Type == GatewayMessageType.Exception)
            {
                Observe(frame, frame.Payload, "transport_exception");
                continue;
            }
            if (frame.MsgId == WavesMessageIds.ProtoKeyResponse)
            {
                var identity = Session.IdentityVersion;
                var resolved = Session.ResolveKey(frame);
                if (identity != Session.IdentityVersion) Inventory.Reset();
                Observe(frame, frame.Payload, resolved ? null : "key_resolution_failed");
                continue;
            }
            if (Session.SessionKey is null)
            {
                if (!_exportEvents) return before;
                Observe(frame, null, "key_unavailable");
                continue;
            }
            if (!Session.TryDecode(frame, out var plain, out var status))
            {
                Observe(frame, null, status);
                continue;
            }
            if (Session.BeforeMessage(frame)) Inventory.Reset();
            if (frame.MsgId == WavesMessageIds.BasicInfoNotify)
            {
                try
                {
                    if (Session.AcceptIdentity(frame with { Payload = plain! })) Inventory.Reset();
                }
                catch (InvalidDataException)
                {
                    Observe(frame, plain, "identity_failed");
                    throw;
                }
            }
            if (Session.ConfirmedPlayerId is { } player) Observer?.Bind(Session.SessionId, player);
            if (Observe(frame, plain, null)) Session.MarkVerified();
            if (frame.MsgId == WavesMessageIds.LogoutNotify)
            {
                Session.Logout();
                Inventory.Reset();
            }
            var applied = Inventory.Apply(frame.MsgId, plain!);
            if (applied == InventoryMessageResult.Updated) Session.MarkVerified();
            else if (applied == InventoryMessageResult.Invalid && !_exportEvents)
                throw new InvalidDataException($"背包消息 {frame.MsgId} 解密或 protobuf 解析失败，未更新导出文件。");
        }
        return offset;
    }

    private bool Observe(GatewayFrame frame, byte[]? plain, string? status)
    {
        ProtocolJsonDecoder.Result? protocol = null;
        if (status is null && plain is not null && frame.Type != GatewayMessageType.Exception
            && (_exportEvents || !Session.HasVerifiedTraffic))
        {
            _decoder ??= new ProtocolJsonDecoder(ProtocolCatalog.Default.Value);
            protocol = _decoder.Decode(frame.MsgId, plain);
        }
        var verified = protocol?.Status.StartsWith("decoded", StringComparison.Ordinal) == true;
        var player = Session.ConfirmedPlayerId;
        var session = Session.SessionId;
        if (status == "key_resolution_failed")
        {
            player = null;
            session += "-unverified-" + frame.SeqNo;
        }
        if (!Session.HasVerifiedTraffic && !verified)
        {
            player = null;
            session = "unverified-" + session + "-" + ConnectionId;
        }
        Observer?.Observe(new(frame, plain, player, session, ConnectionId, status, protocol));
        return verified;
    }

    private void Reject(byte[] bytes, string status) =>
        Observe(new(GatewayMessageType.Invalid, 0, 0, 0, 0, 0, bytes), null, status);

    internal void ConsumeRaw(byte[] bytes)
    {
        var fingerprint = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
        if (!_rawSeen.Add(fingerprint)) return;
        _rawOrder.Enqueue(fingerprint);
        if (_rawOrder.Count > 256) _rawSeen.Remove(_rawOrder.Dequeue());
        var consumed = Consume(bytes);
        if (consumed < bytes.Length) Reject(bytes.AsSpan(consumed).ToArray(), "incomplete_raw_frame");
    }

    internal CaptureConnectionState ForkForReconnect()
    {
        var next = new CaptureConnectionState(Session.ForkForReconnect(), Inventory.ForkForReconnect(), _exportEvents) { Observer = Observer };
        foreach (var fingerprint in _rawOrder)
        {
            next._rawSeen.Add(fingerprint);
            next._rawOrder.Enqueue(fingerprint);
        }
        return next;
    }

    public void Dispose() => Session.Dispose();
}
