using ABI.Models;
using Haiyu.ABI.Services.GameDataExport.Decrypt;
using Haiyu.ABI.Services.GameDataExport.Rebuild;

namespace Haiyu.ABI.Services.GameDataExport.Export;

/// <summary>单消费者：完整帧先解密、更新快照，再推进并回收 KCP 缓冲。</summary>
internal sealed class InventoryCaptureState(SessionKeyLoader keyLoader, GameDataExportScope scope) : IDisposable
{
    internal byte[]? SessionKey => keyLoader.SessionKey;
    public void Dispose() => keyLoader.Dispose();
    internal PlayerIdResolver PlayerId { get; } = new();
    internal Dictionary<ushort, List<GameNormalItem>> NormalItems { get; } = new();
    internal Dictionary<ushort, List<GameWeaponItem>> Weapons { get; } = new();
    internal Dictionary<ushort, List<GamePhantomItem>> Phantoms { get; } = new();
    internal Dictionary<ushort, List<GameValidTimeItem>> ValidTimeItems { get; } = new();
    internal int DecodedFrames { get; private set; }
    internal int Version { get; private set; }
    internal int ParsedCategories => NormalItems.Count + Weapons.Count + Phantoms.Count + ValidTimeItems.Count;
    internal bool Ready => PlayerId.Current == PlayerIdResolver.Phase.Resolved
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
                    if (keyLoader.TryResolve(frame))
                        PlayerId.NotifyKeyResolved();
                    continue;
                }
                if (keyLoader.SessionKey is null)
                    return before; // 不清理尚未能解密的完整帧。
                PlayerId.NotifyKeyResolved();
                var plain = FrameCipher.Decrypt(frame, keyLoader.SessionKey);
                if (frame.MsgId == MsgIdFilter.BasicInfoNotify)
                {
                    // GatewayFrame.Payload 是密文，状态机只接收解密/解压后的 protobuf。
                    if (!PlayerId.TryFeed(frame with { Payload = plain }))
                        throw new InvalidDataException(PlayerId.FailureReason);
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
                DecodedFrames++;
                Version++;
            }
            else if (offset == before)
                break; // 半帧：保留在流中，下次补齐后再消费。
        }
        return offset;
    }

    private static bool Store<T>(Dictionary<ushort, List<T>> map, ushort id, List<T>? items)
    {
        if (items is null)
            return false;
        map[id] = items;
        return true;
    }
}
