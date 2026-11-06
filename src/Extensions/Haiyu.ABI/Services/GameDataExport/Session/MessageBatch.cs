using Haiyu.ABI.Services.GameDataExport.Modules;

namespace Haiyu.ABI.Services.GameDataExport.Session;

/// <summary>先确认传输所属会话，再按原顺序分发，避免候选连接污染当前模块状态。</summary>
internal sealed class MessageBatch(Action? onObserved = null) : IGameMessageObserver
{
    private sealed record Binding(string Session, int Player);
    private readonly List<object> _items = [];
    internal int DecodedCount { get; private set; }
    public void Observe(MessageObservation message)
    {
        onObserved?.Invoke();
        _items.Add(message);
        if (message.Protocol?.DecodeStatus.StartsWith("decoded", StringComparison.Ordinal) == true) DecodedCount++;
    }
    public void Bind(string sessionId, int playerId) => _items.Add(new Binding(sessionId, playerId));
    public void SessionChanged(ExportSessionChange change) => _items.Add(change);
    internal void Dispatch(IGameMessageObserver observer, bool current, bool verified)
    {
        foreach (var item in _items)
            switch (item)
            {
                case MessageObservation message: observer.Observe(message with { IsCurrentSession = current, ConnectionVerified = verified && message.ConnectionVerified }); break;
                case ExportSessionChange change: observer.SessionChanged(change with { IsCurrent = current, Verified = verified && change.Verified }); break;
                case Binding binding: observer.Bind(binding.Session, binding.Player); break;
            }
    }
}
