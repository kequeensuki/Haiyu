using System.Text.Json.Serialization;
using MemoryPack;

namespace ABI.Models;

[MemoryPackable]
public sealed partial record GameAchievementData(
    [property: JsonPropertyName("finishCount")] int FinishCount,
    [property: JsonPropertyName("starCount")] int StarCount,
    [property: JsonPropertyName("items")] IReadOnlyList<GameAchievementItem> Items,
    [property: JsonPropertyName("groups")] IReadOnlyList<GameAchievementGroup> Groups);

[MemoryPackable]
public sealed partial record GameAchievementItem(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("groupId")] int? GroupId,
    [property: JsonPropertyName("finishTime")] uint FinishTime,
    [property: JsonPropertyName("isReceive")] bool IsReceive,
    [property: JsonPropertyName("currentProgress")] int? CurrentProgress,
    [property: JsonPropertyName("totalProgress")] int? TotalProgress,
    [property: JsonPropertyName("isCompleted")] bool IsCompleted,
    [property: JsonPropertyName("rewardClaimed")] bool RewardClaimed,
    [property: JsonPropertyName("rewardStatus")] string RewardStatus);

[MemoryPackable]
public sealed partial record GameAchievementGroup(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("finishTime")] uint FinishTime,
    [property: JsonPropertyName("isReceive")] bool IsReceive,
    [property: JsonPropertyName("isCompleted")] bool IsCompleted,
    [property: JsonPropertyName("rewardClaimed")] bool RewardClaimed,
    [property: JsonPropertyName("rewardStatus")] string RewardStatus);
