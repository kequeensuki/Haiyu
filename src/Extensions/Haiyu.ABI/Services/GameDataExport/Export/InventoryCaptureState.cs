using ABI.Models;
using Haiyu.ABI.Services.GameDataExport.Decrypt;
using Haiyu.ABI.Services.GameDataExport.Rebuild;

namespace Haiyu.ABI.Services.GameDataExport.Export;

/// <summary>单消费者：完整帧先解密、更新快照，再推进并回收 KCP 缓冲。</summary>
internal sealed class InventoryCaptureState(SessionKeyLoader keyLoader, GameDataExportScope scope) : IDisposable
{
    private readonly HashSet<string> _rawSeen = new();
    private readonly Queue<string> _rawOrder = new();
    internal byte[]? SessionKey => keyLoader.SessionKey;
    internal bool HasVerifiedTraffic { get; private set; }
    internal int VerifiedFrames { get; private set; }
    public void Dispose() => keyLoader.Dispose();
    internal PlayerIdResolver PlayerId { get; } = new();
    internal Dictionary<ushort, List<GameNormalItem>> NormalItems { get; } = new();
    internal Dictionary<ushort, List<GameWeaponItem>> Weapons { get; } = new();
    internal Dictionary<ushort, List<GamePhantomItem>> Phantoms { get; } = new();
    internal Dictionary<ushort, List<GameValidTimeItem>> ValidTimeItems { get; } = new();
    internal int DecodedFrames { get; private set; }
    internal int DecodedOnConnection { get; private set; }
    internal int Version { get; private set; }
    internal string? Diagnostic { get; private set; }
    internal int ParsedCategories => NormalItems.Count + Weapons.Count + Phantoms.Count + ValidTimeItems.Count;
    internal bool Ready => HasVerifiedTraffic && PlayerId.Current == PlayerIdResolver.Phase.Resolved
        && ParsedCategories == MsgIdFilter.GetTargetMsgIds(scope).Length && DecodedFrames > 0;

    internal void Consume(KcpStreamRebuilder stream)
    {
        var bytes = stream.BuildIncremental();
        var consumed = Consume(bytes);
        stream.AdvanceConsumed(consumed);
        stream.Compact();
    }

    internal int Consume(byte[] bytes)
    {
        var offset = 0;
        while (offset < bytes.Length)
        {
            var before = offset;
            if (FrameSplitter.TryTake(bytes, ref offset, out var frame) && frame is not null)
            {
                if (frame.MsgId == MsgIdFilter.ProtoKeyResponse)
                {
                    var generation = keyLoader.Generation;
                    if (!keyLoader.TryResolve(frame))
                        Diagnostic = "新会话密钥解析失败，保留之前的密钥和已导出文件。";
                    else
                    {
                        HasVerifiedTraffic = true;
                        VerifiedFrames++;
                        if (keyLoader.Generation != generation)
                        {
                            ClearSnapshots();
                            PlayerId.NotifyRelogin();
                            Diagnostic = "已取得新会话密钥，等待玩家身份及完整背包。";
                        }
                    }
                    continue;
                }
                if (keyLoader.SessionKey is null)
                    return before; // 不清理尚未能解密的完整帧。
                PlayerId.NotifyKeyResolved();
                var plain = FrameCipher.Decrypt(frame, keyLoader.SessionKey);
                if (frame.MsgId == MsgIdFilter.BasicInfoNotify)
                {
                    // GatewayFrame.Payload 是密文，状态机只接收解密/解压后的 protobuf。
                    var previousId = PlayerId.PlayerId;
                    var previouslyResolved = PlayerId.Current == PlayerIdResolver.Phase.Resolved;
                    if (!PlayerId.TryFeed(frame with { Payload = plain }))
                        throw new InvalidDataException(PlayerId.FailureReason);
                    if (PlayerId.Current == PlayerIdResolver.Phase.Resolved)
                    {
                        HasVerifiedTraffic = true;
                        VerifiedFrames++;
                    }
                    if (PlayerId.Current == PlayerIdResolver.Phase.Resolved && previousId != PlayerId.PlayerId)
                    {
                        if (previousId > 0 && previouslyResolved)
                            ClearSnapshots();
                        Diagnostic = $"已切换至玩家 {PlayerId.PlayerId}，等待完整背包。";
                    }
                }
                if (!MsgIdFilter.IsTarget(frame.MsgId, scope))
                    continue;
                bool parsed;
                switch (frame.MsgId)
                {
                    case MsgIdFilter.NormalItemResponse:
                        parsed = Store(NormalItems, frame.MsgId, InventoryMapper.MapNormalItems(plain)); break;
                    case MsgIdFilter.WeaponItemResponse:
                        parsed = Store(Weapons, frame.MsgId, InventoryMapper.MapWeapons(plain)); break;
                    case MsgIdFilter.PhantomItemResponse:
                        parsed = Store(Phantoms, frame.MsgId, InventoryMapper.MapPhantoms(plain)); break;
                    case MsgIdFilter.ValidTimeItemResponse:
                        parsed = Store(ValidTimeItems, frame.MsgId, InventoryMapper.MapValidTimeItems(plain)); break;
                    default: parsed = false; break;
                }
                if (!parsed)
                    throw new InvalidDataException($"背包消息 {frame.MsgId} 解密或 protobuf 解析失败，未更新导出文件。");
                HasVerifiedTraffic = true;
                VerifiedFrames++;
                DecodedFrames++;
                DecodedOnConnection++;
                Version++;
            }
            else if (offset == before)
                break; // 半帧：保留在流中，下次补齐后再消费。
        }
        return offset;
    }

    internal void ConsumeRaw(byte[] bytes)
    {
        // 探测期间同一裸帧可能同时出现在物理/虚拟接口；缓存有上限，不跨帧累积。
        var fingerprint = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
        if (!_rawSeen.Add(fingerprint))
            return;
        _rawOrder.Enqueue(fingerprint);
        if (_rawOrder.Count > 256)
            _rawSeen.Remove(_rawOrder.Dequeue());
        Consume(bytes);
    }

    internal InventoryCaptureState ForkForReconnect()
    {
        // 新传输连接有独立 sn/缓冲；会话副本在验证收到的游戏数据前不允许写盘。
        var next = new InventoryCaptureState(keyLoader.CreateSession(resume: true), scope)
        {
            DecodedFrames = DecodedFrames,
            Version = Version
        };
        next.PlayerId.RestoreFrom(PlayerId);
        foreach (var pair in NormalItems) next.NormalItems[pair.Key] = pair.Value.ToList();
        foreach (var pair in Weapons) next.Weapons[pair.Key] = pair.Value.ToList();
        foreach (var pair in Phantoms) next.Phantoms[pair.Key] = pair.Value.ToList();
        foreach (var pair in ValidTimeItems) next.ValidTimeItems[pair.Key] = pair.Value.ToList();
        foreach (var fingerprint in _rawOrder)
        {
            next._rawSeen.Add(fingerprint);
            next._rawOrder.Enqueue(fingerprint);
        }
        return next;
    }

    internal string? TakeDiagnostic()
    {
        var value = Diagnostic;
        Diagnostic = null;
        return value;
    }

    private void ClearSnapshots()
    {
        NormalItems.Clear();
        Weapons.Clear();
        Phantoms.Clear();
        ValidTimeItems.Clear();
    }

    private static bool Store<T>(Dictionary<ushort, List<T>> map, ushort id, List<T>? items)
    {
        if (items is null)
            return false;
        map[id] = items;
        return true;
    }
}
