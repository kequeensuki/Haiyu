using Haiyu.ABI.Protocol;
using ABI.Models;

namespace Haiyu.ABI.Services.GameDataExport.Export;

/// <summary>
/// 目标 msgId 白名单。
///
/// 数值取自游戏 <c>EMessageId</c>（反编译源码可查），不是硬编码猜测：
/// <code>
///   NormalItemRequest        = 22983
///   NormalItemResponse       = 22445
///   WeaponItemRequest        = 25468
///   WeaponItemResponse       = 27089
///   PhantomItemRequest       = 24659
///   PhantomItemResponse      = 26275
///   ValidTimeItemRequest     = 16123
///   ValidTimeItemResponse    = 20447
///   PushDataCompleteNotify   = 113
/// </code>
/// </summary>
public static class MsgIdFilter
{
    public const ushort NormalItemResponse = WavesMessageIds.NormalItemResponse;
    public const ushort WeaponItemResponse = WavesMessageIds.WeaponItemResponse;
    public const ushort PhantomItemResponse = WavesMessageIds.PhantomItemResponse;
    public const ushort ValidTimeItemResponse = WavesMessageIds.ValidTimeItemResponse;

    /// <summary>会话密钥协商响应（明文），解密前置条件。</summary>
    public const ushort ProtoKeyResponse = WavesMessageIds.ProtoKeyResponse;

    /// <summary>
    /// 玩家基础信息推送（登录后立即下发），<c>Id</c> 字段（field 15）即玩家 playerId。
    /// 反编译实证：EMessageId.cs:81 BasicInfoNotify = 15380。
    /// ★ 元数据：始终跟踪但<b>不计入</b>背包「收齐」判定。
    /// </summary>
    public const ushort BasicInfoNotify = WavesMessageIds.BasicInfoNotify;

    /// <summary>推送完成标志，官方在此之后才发全量背包请求。</summary>
    public const ushort LoginResponse = WavesMessageIds.LoginResponse;
    public const ushort LogoutNotify = WavesMessageIds.LogoutNotify;

    public const ushort PushDataCompleteNotify = WavesMessageIds.PushDataCompleteNotify;

    private static readonly ushort[] InventoryResponses =
    [
        NormalItemResponse,
        WeaponItemResponse,
        PhantomItemResponse,
        ValidTimeItemResponse,
    ];

    /// <summary>
    /// 该范围对应的全部目标 msgId。
    /// 用于「收齐了就提前结束抓包」的完成判定——少收一条都要继续等。
    /// </summary>
    public static ushort[] GetTargetMsgIds(GameDataExportScope scope)
    {
        if (scope.HasFlag(GameDataExportScope.Inventory))
            return InventoryResponses;

        return [];
    }

    /// <summary>该 msgId 是否属于指定导出范围。</summary>
    public static bool IsTarget(ushort msgId, GameDataExportScope scope)
    {
        if (scope.HasFlag(GameDataExportScope.Inventory))
            return Array.IndexOf(InventoryResponses, msgId) >= 0;

        return false;
    }

    /// <summary>是否为需要额外处理的协议控制帧（明文的 112）。</summary>
    public static bool IsKeyNegotiation(ushort msgId) => msgId == ProtoKeyResponse;
}
