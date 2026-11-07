using Haiyu.ABI.Services.GameDataExport.Aggregation.Collections;
using Haiyu.ABI.Services.GameDataExport.Aggregation.Contracts;

namespace Haiyu.ABI.Services.GameDataExport.Domains.Gacha;

/// <summary>抽卡记录只有新增，没有修改与删除。</summary>
internal abstract record GachaChange;

/// <summary>加入一条已完成归一的记录；重复键幂等。</summary>
internal sealed record GachaRecordAdded(GachaRecordState Value) : GachaChange;

/// <summary>
/// 跨会话累积的抽卡记录集合。
/// <para>
/// <b>只增语义</b>：<see cref="Aggregation.Collections.DataCollectionBase{TKey,TEntity,TChange}.Update"/>
/// 与 <see cref="Aggregation.Collections.DataCollectionBase{TKey,TEntity,TChange}.Remove"/> 保留基类默认行为
/// （缺目标返回 MissingTarget、删不存在返回 Unchanged），本集合不赋予任何业务语义。
/// </para>
/// <para>
/// 合并基线来自已落盘的旧文件。旧记录永远不被新数据删除——这正是跨会话累积的意义：
/// 官方接口只保留有限时间，过期后本地仍需保留。
/// </para>
/// </summary>
internal sealed class GachaRecordCollection(IEnumerable<GachaRecordState> baseline)
    : DataCollectionBase<GachaRecordKey, GachaRecordState, GachaChange>(
        baseline, value => value.Key, _ => true, Comparer<GachaRecordKey>.Create((a, b) => a.CompareTo(b)))
{
    /// <summary>新增即插入；同键已存在时幂等（Unchanged），绝不覆盖已有记录。</summary>
    public override ChangeResult Add(GachaRecordState entity) => Replace(entity.Key, entity);

    public override ChangeResult Update(GachaRecordKey key, GachaChange change) =>
        change is GachaRecordAdded added
            ? Replace(added.Value.Key, added.Value)
            : base.Update(key, change);

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
