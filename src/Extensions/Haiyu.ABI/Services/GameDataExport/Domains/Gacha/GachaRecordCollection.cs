using Haiyu.ABI.Services.GameDataExport.Aggregation.Collections;
using Haiyu.ABI.Services.GameDataExport.Aggregation.Contracts;

namespace Haiyu.ABI.Services.GameDataExport.Domains.Gacha;

/// <summary>抽卡记录只有新增，没有修改与删除。</summary>
internal abstract record GachaChange;

/// <summary>加入一条已完成归一的记录；重复键幂等。</summary>
internal sealed record GachaRecordAdded(GachaRecordState Value) : GachaChange;

/// <summary>只增历史：已有身份保留，修改和删除显式拒绝。</summary>
internal sealed class GachaRecordCollection(IEnumerable<GachaRecordState> baseline)
    : DataCollectionBase<GachaRecordKey, GachaRecordState, GachaChange>(
        baseline, value => value.Key, key => key.CardPoolType > 0 && key.ResourceId > 0
            && key.QualityLevel > 0 && key.Count > 0 && key.Occurrence >= 0 && !string.IsNullOrWhiteSpace(key.Time), Comparer<GachaRecordKey>.Create((a, b) => a.CompareTo(b)))
{
    public override ChangeResult Add(GachaRecordState entity) => base.Add(entity);

    public override ChangeResult Update(GachaRecordKey key, GachaChange change) =>
        ChangeResult.Invalid("抽卡历史不支持修改。");

    public override ChangeResult Remove(GachaRecordKey key) =>
        ChangeResult.Invalid("抽卡历史不支持删除。");

    protected override GachaRecordState Merge(GachaRecordState current, GachaChange change) =>
        throw new InvalidDataException("抽卡记录不支持合并变更。");

    /// <summary>记录整体相等比较，避免相同内容重复通知产生新落盘版本。</summary>
    protected override bool Equivalent(GachaRecordState first, GachaRecordState second) =>
        first.Key.Equals(second.Key) && string.Equals(first.PoolLabel, second.PoolLabel, StringComparison.Ordinal);

    /// <summary>
    /// 把一次接口响应并入集合。occurrence 由调用方按"本次响应内同键序号"生成，
    /// 因此重复拉取同一批数据得到相同键，天然幂等。
    /// </summary>
    internal ChangeResult ApplyFetched(IEnumerable<GachaRecordState> fetched)
    {
        var changed = false;
        foreach (var item in fetched)
        {
            var result = base.Add(item);
            if (!result.Succeeded) return result;
            if (result.Status == ChangeStatus.Changed) changed = true;
        }
        return changed ? ChangeResult.Changed : ChangeResult.Unchanged;
    }
}
