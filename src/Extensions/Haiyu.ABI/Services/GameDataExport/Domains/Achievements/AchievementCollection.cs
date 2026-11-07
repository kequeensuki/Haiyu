using Haiyu.ABI.Services.GameDataExport.Aggregation.Collections;
using Haiyu.ABI.Services.GameDataExport.Aggregation.Contracts;

namespace Haiyu.ABI.Services.GameDataExport.Domains.Achievements;

internal sealed class AchievementCollection(IEnumerable<AchievementState> entries)
    : DataCollectionBase<(int Kind, int Id), AchievementState, AchievementChanged>(entries,
        entry => ((int)entry.Kind, entry.Id), key => key.Kind is >= 0 and <= 2 && (key.Kind == 0 ? key.Id == 0 : key.Id > 0),
        Comparer<(int Kind, int Id)>.Default)
{
    // 客户端会按配置懒创建初次收到进度的成就；完整通知可以新增条目，不能误判为漏收。
    public override ChangeResult Update((int Kind, int Id) key, AchievementChanged change)
    {
        var value = change.Value;
        if (Items.TryGetValue(key, out var previous) && value.Kind == AchievementKind.Entry && value.GroupId is null)
            value = value with { GroupId = previous.GroupId };
        return Replace(key, value);
    }
    protected override AchievementState Merge(AchievementState current, AchievementChanged change) => change.Value;
}
