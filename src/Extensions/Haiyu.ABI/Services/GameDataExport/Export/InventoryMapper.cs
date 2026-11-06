using ABI.Models;
using Haiyu.ABI.Protocol;

namespace Haiyu.ABI.Services.GameDataExport.Export;

/// <summary>
/// 把解密后的 protobuf 字节映射成 ABI 传输用的 DTO。
///
/// ★ 只做字段搬运，不做业务解释（不查配置表、不算词条价值）。
/// ★ 解析失败返回 <c>null</c>，由调用方决定是否忽略——单条消息坏掉不应让整次导出失败。
/// </summary>
public static class InventoryMapper
{
    public static List<GameNormalItem>? MapNormalItems(byte[] payload)
    {
        var message = Parse<NormalItemResponse>(payload);
        if (message is null)
            return null;

        return message
            .NormalItemList.Select(item => new GameNormalItem(item.Id, item.Count, item.ExpireTime))
            .ToList();
    }

    public static List<GameWeaponItem>? MapWeapons(byte[] payload)
    {
        var message = Parse<WeaponItemResponse>(payload);
        if (message is null)
            return null;

        return message
            .WeaponItemList.Select(item => new GameWeaponItem(
                item.Id,
                item.IncrId,
                item.WeaponLevel,
                item.WeaponExp,
                item.WeaponBreach,
                item.WeaponResonLevel,
                item.RoleId
            ))
            .ToList();
    }

    public static List<GamePhantomItem>? MapPhantoms(byte[] payload)
    {
        var message = Parse<PhantomItemResponse>(payload);
        if (message is null)
            return null;

        return message
            .PhantomItemList.Select(item => new GamePhantomItem(
                item.Id,
                item.IncrId,
                item.PhantomLevel,
                item.PhantomExp,
                MapProps(item.PhantomMainProp),
                MapProps(item.PhantomSubProp),
                item.FetterGroupId,
                item.SkinId,
                item.StackCount,
                MapProps(item.UnAckSubProp),
                item.LockPropIndex.ToArray(),
                item.AddTime,
                item.FuncValue
            ))
            .ToList();
    }

    public static List<GameValidTimeItem>? MapValidTimeItems(byte[] payload)
    {
        var message = Parse<ValidTimeItemResponse>(payload);
        if (message is null)
            return null;

        return message
            .ItemList.Select(item => new GameValidTimeItem(
                item.Id,
                item.Count,
                item.IncrId,
                item.ExpireTime
            ))
            .ToList();
    }

    private static IReadOnlyList<GamePhantomProp> MapProps(
        IEnumerable<PhantomPropInfo> props
    ) => props.Select(prop => new GamePhantomProp(prop.PhantomPropId, prop.Value)).ToList();

    /// <summary>用生成类型的 Parser 解析，不手写 protobuf 解析。</summary>
    private static T? Parse<T>(byte[] payload)
        where T : class, Google.Protobuf.IMessage<T>, new()
    {
        try
        {
            var message = new T();
            // MessageExtensions.MergeFrom 提供 byte[] 重载
            Google.Protobuf.MessageExtensions.MergeFrom(message, payload);
            return message;
        }
        catch (Google.Protobuf.InvalidProtocolBufferException)
        {
            return null;
        }
    }
}
