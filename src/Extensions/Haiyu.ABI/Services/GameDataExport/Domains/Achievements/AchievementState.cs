namespace Haiyu.ABI.Services.GameDataExport.Domains.Achievements;

internal enum AchievementKind { Summary, Group, Entry }

/// <summary>完成与领取是独立服务器字段；进度相等不能代替完成时间或领取标志。</summary>
internal sealed record AchievementState(AchievementKind Kind, int Id, uint FinishTime = 0, bool IsReceive = false,
    int? CurrentProgress = null, int? TotalProgress = null, int? GroupId = null, int FinishCount = 0, int StarCount = 0)
{
    internal bool IsCompleted => IsReceive || FinishTime > 0;
    internal string RewardStatus => IsReceive ? "claimed" : FinishTime > 0 ? "unclaimed" : "not_available";
}

internal sealed record AchievementChanged(AchievementState Value);
