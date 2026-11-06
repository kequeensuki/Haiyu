using Haiyu.ABI.Services.GameDataExport.Modules;
using Haiyu.ABI.Services.GameDataExport.Decrypt;
using Haiyu.ABI.Services.GameDataExport.Protocol;

namespace Haiyu.ABI.Services.GameDataExport.Session;

/// <summary>捕获链路产出的消息及已确认的归属；消费者不再解释登录状态。</summary>
internal sealed record MessageObservation(
    GatewayFrame Frame,
    byte[]? Plain,
    int? PlayerId,
    string SessionId,
    uint ConnectionId,
    string? Status,
    ProtocolJsonDecoder.Result? Protocol,
    bool IsCurrentSession = true, bool ConnectionVerified = true, long EventIndex = 0, DateTimeOffset? ReceivedAt = null, string? CaptureId = null);

internal interface IGameMessageObserver
{
    void Observe(MessageObservation message);
    void Bind(string sessionId, int playerId);
    void SessionChanged(ExportSessionChange change) { }
}
