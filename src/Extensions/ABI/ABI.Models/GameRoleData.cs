using System.Text.Json.Serialization;
using MemoryPack;

namespace ABI.Models;

[MemoryPackable]
public sealed partial record GameRoleData(
    [property: JsonPropertyName("items")] IReadOnlyList<GameRoleItem> Items);

[MemoryPackable]
public sealed partial record GameRoleItem(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("level")] int Level,
    [property: JsonPropertyName("exp")] int Exp,
    [property: JsonPropertyName("breakthrough")] int Breakthrough,
    [property: JsonPropertyName("skills")] IReadOnlyList<GameRoleValue> Skills,
    [property: JsonPropertyName("skillNodes")] IReadOnlyList<GameRoleSkillNode> SkillNodes,
    [property: JsonPropertyName("baseProps")] IReadOnlyList<GameRoleValue> BaseProps,
    [property: JsonPropertyName("addProps")] IReadOnlyList<GameRoleValue> AddProps,
    [property: JsonPropertyName("attributes")] IReadOnlyList<GameRoleValue> Attributes,
    [property: JsonPropertyName("equipmentStatus")] string EquipmentStatus,
    [property: JsonPropertyName("echoes")] IReadOnlyList<GameRoleEcho> Echoes,
    [property: JsonPropertyName("echoBaseProps")] IReadOnlyList<GameRoleValue>? EchoBaseProps,
    [property: JsonPropertyName("echoAddProps")] IReadOnlyList<GameRoleValue>? EchoAddProps);

/// <summary>属性为服务器原始整数单位；技能为技能ID及其等级。</summary>
[MemoryPackable]
public sealed partial record GameRoleValue(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("value")] long Value);

[MemoryPackable]
public sealed partial record GameRoleSkillNode(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("isActive")] bool IsActive,
    [property: JsonPropertyName("skillId")] int SkillId);

[MemoryPackable]
public sealed partial record GameRoleEcho(
    [property: JsonPropertyName("slot")] int Slot,
    [property: JsonPropertyName("isMain")] bool IsMain,
    [property: JsonPropertyName("incrId")] int IncrId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("item"), RoleEchoItemFormatter] GamePhantomItem? Item);
