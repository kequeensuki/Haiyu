using Haiyu.ABI.Protocol;
using Haiyu.ABI.Services.GameDataExport.Decrypt;

namespace Haiyu.ABI.Services.GameDataExport.Session;

/// <summary>登录身份与密钥上下文；不持有导出业务状态、文件或捕获接口。</summary>
internal sealed class GameSessionContext(SessionKeyLoader keyLoader) : IDisposable
{
    private readonly FrameCipher _cipher = new();
    private bool _identityPending;
    private bool _awaitingLoginAck = true;

    internal byte[]? SessionKey => keyLoader.SessionKey;
    internal PlayerIdResolver PlayerId { get; } = new();
    internal string SessionId { get; private set; } = Guid.NewGuid().ToString("N");
    internal bool HasVerifiedTraffic { get; private set; }
    internal int VerifiedFrames { get; private set; }
    internal int IdentityVersion { get; private set; }
    internal bool IsIdentityConfirmed => !_identityPending && PlayerId.Current == PlayerIdResolver.Phase.Resolved;
    internal int? ConfirmedPlayerId => IsIdentityConfirmed ? PlayerId.PlayerId : null;
    private string? _diagnostic;

    internal bool ResolveKey(GatewayFrame frame)
    {
        var generation = keyLoader.Generation;
        if (!keyLoader.TryResolve(frame))
        {
            _diagnostic = "新会话密钥解析失败，保留之前的密钥和已导出文件。";
            return false;
        }
        MarkVerified();
        if (keyLoader.Generation != generation)
        {
            IdentityVersion++;
            SessionId = Guid.NewGuid().ToString("N");
            _identityPending = true;
            _awaitingLoginAck = true;
            PlayerId.NotifyRelogin();
            _diagnostic = "已取得新会话密钥，等待新的玩家身份。";
        }
        return true;
    }

    internal bool TryDecode(GatewayFrame frame, out byte[]? plain, out string? status) =>
        _cipher.TryDecode(frame, SessionKey, out plain, out status);

    /// <summary>返回是否开始新的身份代次，调用者据此隔离业务快照。</summary>
    internal bool BeforeMessage(GatewayFrame frame)
    {
        var reset = false;
        if (frame.MsgId == WavesMessageIds.LoginResponse)
        {
            // BasicInfo 的裸 UDP 推送可能先于首次 KCP 登录确认到达。
            if (!_awaitingLoginAck && PlayerId.Current == PlayerIdResolver.Phase.Resolved)
            {
                if (!_identityPending) SessionId = Guid.NewGuid().ToString("N");
                _identityPending = true;
                PlayerId.NotifyRelogin();
                IdentityVersion++;
                reset = true;
            }
            else if (_identityPending && PlayerId.Current == PlayerIdResolver.Phase.WaitingKey)
                PlayerId.NotifyRelogin();
            _awaitingLoginAck = false;
        }
        if (!(_identityPending && _awaitingLoginAck)) PlayerId.NotifyKeyResolved();
        return reset;
    }

    internal bool AcceptIdentity(GatewayFrame frame)
    {
        var previousId = PlayerId.PlayerId;
        var wasResolved = PlayerId.Current == PlayerIdResolver.Phase.Resolved;
        if (!PlayerId.TryFeed(frame)) throw new InvalidDataException(PlayerId.FailureReason);
        if (PlayerId.Current != PlayerIdResolver.Phase.Resolved) return false;

        MarkVerified();
        var changed = previousId != PlayerId.PlayerId;
        if (changed)
        {
            if (wasResolved)
            {
                _awaitingLoginAck = true;
                if (!_identityPending) SessionId = Guid.NewGuid().ToString("N");
            }
            IdentityVersion++;
            _diagnostic = $"已切换至玩家 {PlayerId.PlayerId}，继续接收服务器数据。";
        }
        _identityPending = false;
        return changed && previousId > 0 && wasResolved;
    }

    internal void Logout()
    {
        SessionId = Guid.NewGuid().ToString("N");
        _identityPending = true;
        _awaitingLoginAck = true;
        // 停留在登录界面不消耗等待身份的期限，也不继续暴露旧身份。
        PlayerId.WaitForLogin();
    }

    internal void MarkVerified()
    {
        HasVerifiedTraffic = true;
        VerifiedFrames++;
    }

    internal GameSessionContext ForkForReconnect()
    {
        var next = new GameSessionContext(keyLoader.CreateSession(resume: true))
        {
            IdentityVersion = IdentityVersion,
            SessionId = SessionId,
            _identityPending = _identityPending,
            _awaitingLoginAck = _awaitingLoginAck
        };
        next.PlayerId.RestoreFrom(PlayerId);
        return next;
    }

    internal string? TakeDiagnostic()
    {
        var value = _diagnostic;
        _diagnostic = null;
        return value;
    }

    public void Dispose()
    {
        _cipher.Dispose();
        keyLoader.Dispose();
    }
}
