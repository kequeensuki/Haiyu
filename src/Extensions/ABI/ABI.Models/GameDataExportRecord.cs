using System.Text.Json.Serialization;
using MemoryPack;

namespace ABI.Models;

/*
 游戏数据导出（GameDataExport）的 ABI 交换模型
 命名空间固定 ABI.Models，与其余服务保持一致
 */

#region Request / Progress

/// <summary>
/// 导出请求。
/// 输出目录写死（见 <c>GameDataExportPaths.OutputDirectory</c>），不作为参数。
/// </summary>
[MemoryPackable]
public sealed partial record GameDataExportRequest(
    [property: JsonPropertyName("scope")] GameDataExportScope Scope
);

/// <summary>导出范围。首版只实现 <see cref="Inventory"/>，其余为预留。</summary>
[Flags]
public enum GameDataExportScope
{
    None = 0,

    /// <summary>背包：普通道具 / 武器 / 声骸 / 限时道具</summary>
    Inventory = 1 << 0,

    // 预留（首版不实现）
    // Gacha  = 1 << 1,
    // Player = 1 << 2,
}

/// <summary>导出阶段。</summary>
public enum GameDataExportStage
{
    /// <summary>抓包中（等待握手与 KCP 数据）</summary>
    Capturing = 0,

    /// <summary>已拿到会话密钥，逐帧解密</summary>
    Decrypting = 1,

    /// <summary>组装 DTO 并落盘</summary>
    Exporting = 2,

    Completed = 3,
    Failed = 4,

    /// <summary>快照已导出，抓包会话仍在运行。</summary>
    Monitoring = 5,
}

/// <summary>导出进度。</summary>
[MemoryPackable]
public sealed partial record GameDataExportProgress(
    [property: JsonPropertyName("stage")] GameDataExportStage Stage,
    [property: JsonPropertyName("percentage")] int Percentage,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("captureReady")] bool CaptureReady = false,
    [property: JsonPropertyName("hasDataUpdate")] bool HasDataUpdate = false,
    [property: JsonPropertyName("playerId")] int PlayerId = 0,
    [property: JsonPropertyName("outputJsonPath")] string? OutputJsonPath = null,
    [property: JsonPropertyName("updateCount")] int UpdateCount = 0
);

#endregion

#region Result

/// <summary>
/// 导出结果。
/// 不复用 <see cref="RunResult"/>：背包数据体量大，需随结果一并返回。
/// </summary>
[MemoryPackable]
public sealed partial record GameDataExportResult(
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("inventory")] GameInventoryData? Inventory,
    [property: JsonPropertyName("outputJsonPath")] string? OutputJsonPath,
    [property: JsonPropertyName("capturedFrames")] int CapturedFrames,
    [property: JsonPropertyName("decodedFrames")] int DecodedFrames,
    [property: JsonPropertyName("sessionKeyHex")] string? SessionKeyHex,
    // ── v2 持续监控新增（尾部追加，两端同包部署无兼容负担）──
    [property: JsonPropertyName("playerId")] int PlayerId = 0,
    [property: JsonPropertyName("updateCount")] int UpdateCount = 0
);

#endregion

#region Inventory DTO

/// <summary>背包全量数据。</summary>
[MemoryPackable]
public sealed partial record GameInventoryData(
    [property: JsonPropertyName("normalItems")] IReadOnlyList<GameNormalItem> NormalItems,
    [property: JsonPropertyName("weapons")] IReadOnlyList<GameWeaponItem> Weapons,
    [property: JsonPropertyName("phantoms")] IReadOnlyList<GamePhantomItem> Phantoms,
    [property: JsonPropertyName("validTimeItems")] IReadOnlyList<GameValidTimeItem> ValidTimeItems
);

/// <summary>普通道具（对应 NormalItem）。</summary>
[MemoryPackable]
public sealed partial record GameNormalItem(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("count")] int Count,
    [property: JsonPropertyName("expireTime")] long ExpireTime
);

/// <summary>武器（对应 WeaponItem）。</summary>
[MemoryPackable]
public sealed partial record GameWeaponItem(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("incrId")] int IncrId,
    [property: JsonPropertyName("weaponLevel")] int WeaponLevel,
    [property: JsonPropertyName("weaponExp")] int WeaponExp,
    [property: JsonPropertyName("weaponBreach")] int WeaponBreach,
    [property: JsonPropertyName("weaponResonLevel")] int WeaponResonLevel,
    [property: JsonPropertyName("roleId")] int RoleId
);

/// <summary>声骸（对应 PhantomItem，只保留导出关心的字段）。</summary>
[MemoryPackable]
public sealed partial record GamePhantomItem(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("incrId")] int IncrId,
    [property: JsonPropertyName("phantomLevel")] int PhantomLevel,
    [property: JsonPropertyName("phantomExp")] int PhantomExp,
    [property: JsonPropertyName("phantomMainProp")] IReadOnlyList<GamePhantomProp> MainProps,
    [property: JsonPropertyName("phantomSubProp")] IReadOnlyList<GamePhantomProp> SubProps,
    [property: JsonPropertyName("fetterGroupId")] int FetterGroupId,
    [property: JsonPropertyName("skinId")] int SkinId,
    [property: JsonPropertyName("stackCount")] int StackCount
);

/// <summary>声骸词条（对应 PhantomPropInfo）。</summary>
[MemoryPackable]
public sealed partial record GamePhantomProp(
    [property: JsonPropertyName("propId")] int PropId,
    [property: JsonPropertyName("value")] int Value
);

/// <summary>限时道具（对应 ValidTimeItem）。</summary>
[MemoryPackable]
public sealed partial record GameValidTimeItem(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("count")] int Count,
    [property: JsonPropertyName("incrId")] int IncrId,
    [property: JsonPropertyName("expireTime")] long ExpireTime
);

#endregion
