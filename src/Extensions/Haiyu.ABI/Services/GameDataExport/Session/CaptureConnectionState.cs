using ABI.Models;
using Haiyu.ABI.Protocol;
using Haiyu.ABI.Services.GameDataExport.Decrypt;
using Haiyu.ABI.Services.GameDataExport.Modules;
using Haiyu.ABI.Services.GameDataExport.Protocol;
using Haiyu.ABI.Services.GameDataExport.Rebuild;

namespace Haiyu.ABI.Services.GameDataExport.Session;

/// <summary>单消费者传输管线：切帧、验证、解密，向已注册消费者分发不可变协议模型。</summary>
internal sealed class CaptureConnectionState : IDisposable
{
    private readonly HashSet<string> _rawSeen = new();
    private readonly Queue<string> _rawOrder = new();
    private ProtocolJsonDecoder? _decoder;

    internal CaptureConnectionState(SessionKeyLoader keyLoader) : this(new GameSessionContext(keyLoader)) { }

    private CaptureConnectionState(GameSessionContext session) => Session = session;
    internal GameSessionContext Session { get; }
    internal uint ConnectionId { get; set; }
    internal IGameMessageObserver? Observer { get; set; }

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
                var previousSession = Session.SessionId;
                var resolved = Session.ResolveKey(frame);
                if (previousSession != Session.SessionId) Changed(ExportSessionChangeKind.LoginStarted, previousSession);
                Observe(frame, frame.Payload, resolved ? null : "key_resolution_failed");
                continue;
            }
            if (Session.SessionKey is null)
            {
                Observe(frame, null, "key_unavailable");
                continue;
            }
            if (!Session.TryDecode(frame, out var plain, out var status))
            {
                Observe(frame, null, status);
                continue;
            }
            var previousIdentity = Session.SessionId;
            if (Session.BeforeMessage(frame)) Changed(ExportSessionChangeKind.LoginStarted, previousIdentity);
            if (frame.MsgId == WavesMessageIds.BasicInfoNotify)
            {
                try
                {
                    previousIdentity = Session.SessionId;
                    Session.AcceptIdentity(frame with { Payload = plain! });
                    if (Session.ConfirmedPlayerId is not null) Changed(ExportSessionChangeKind.IdentityResolved, previousIdentity);
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
                var previousSession = Session.SessionId;
                Session.Logout();
                Changed(ExportSessionChangeKind.Logout, previousSession);
            }

        }
        return offset;
    }

    private bool Observe(GatewayFrame frame, byte[]? plain, string? status)
    {
        ProtocolJsonDecoder.Result? protocol = null;
        if (status is null && plain is not null && frame.Type != GatewayMessageType.Exception
            )
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
        Observer?.Observe(new(frame, plain, player, session, ConnectionId, status, protocol, ConnectionVerified: Session.HasVerifiedTraffic || verified, ReceivedAt: DateTimeOffset.UtcNow));
        return verified;
    }

    private void Changed(ExportSessionChangeKind kind, string? previous) => Observer?.SessionChanged(
        new(kind, Session.SessionId, previous, Session.ConfirmedPlayerId, ConnectionId, Verified: Session.HasVerifiedTraffic));

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
        var next = new CaptureConnectionState(Session.ForkForReconnect()) { Observer = Observer };
        foreach (var fingerprint in _rawOrder)
        {
            next._rawSeen.Add(fingerprint);
            next._rawOrder.Enqueue(fingerprint);
        }
        return next;
    }

    public void Dispose() => Session.Dispose();
}
