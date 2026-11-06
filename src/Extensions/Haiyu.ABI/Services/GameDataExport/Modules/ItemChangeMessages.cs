using ABI.Models;
using Haiyu.ABI.Protocol;

namespace Haiyu.ABI.Services.GameDataExport.Modules;

/// <summary>道具新增调研覆盖的下行事件；角色、皮肤、奖励和预览只记录事实，不投影成背包入账。</summary>
internal static class ItemChangeMessages
{
    internal static readonly GameDataExportModuleDescriptor Definition = new(
        "item-changes", "Toolkit_GameDataExportItemChanges", "道具与角色变更事件", false, false);

    internal static readonly IReadOnlySet<ushort> MessageIds = InventoryMessageProjection.Rules.Keys.Concat(new ushort[]
    {
        WavesMessageIds.PushDataCompleteNotify,
        WavesMessageIds.PbGetRoleListNotify,
        WavesMessageIds.PbRoleActiveNotify,
        WavesMessageIds.RoleChangeNotify,
        WavesMessageIds.RoleChangeUnlockNotify,
        WavesMessageIds.RoleSkinResponse,
        WavesMessageIds.RoleSkinAddNotify,
        WavesMessageIds.WeaponSkinAddNotify,
        WavesMessageIds.PhantomSkinAddNotify,
        WavesMessageIds.PhantomUpdateNotify,
        WavesMessageIds.ItemRewardNotify,
        WavesMessageIds.PhantomVicePolishResponse,
        // 文档提及的旧协议仅保留原始事件，不参与当前背包状态。
        WavesMessageIds.WeaponAddNotify,
        WavesMessageIds.WeaponAllInfoNotify,
        WavesMessageIds.WeaponRemoveNotify,
    }).ToHashSet();
}
