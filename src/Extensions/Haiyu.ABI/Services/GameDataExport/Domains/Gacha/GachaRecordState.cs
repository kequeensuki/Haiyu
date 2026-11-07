namespace Haiyu.ABI.Services.GameDataExport.Domains.Gacha;

/// <summary>持久身份不包含本地化名称、类型文字和池标签。Occurrence 区分同秒同物品抽取。</summary>
internal readonly record struct GachaRecordKey(
    int CardPoolType, string Time, int ResourceId, int QualityLevel, int Count, int Occurrence)
    : IComparable<GachaRecordKey>
{
    public int CompareTo(GachaRecordKey other)
    {
        var result = string.CompareOrdinal(other.Time, Time);
        if (result == 0) result = CardPoolType.CompareTo(other.CardPoolType);
        if (result == 0) result = ResourceId.CompareTo(other.ResourceId);
        if (result == 0) result = QualityLevel.CompareTo(other.QualityLevel);
        if (result == 0) result = Count.CompareTo(other.Count);
        return result == 0 ? Occurrence.CompareTo(other.Occurrence) : result;
    }
}

/// <summary>展示字段与稳定身份分离；重复查询不改已有历史。</summary>
internal sealed record GachaRecordState(GachaRecordKey Key, string PoolLabel, string ResourceType, string Name)
{
    internal int CardPoolType => Key.CardPoolType;
    internal string Time => Key.Time;
    internal int ResourceId => Key.ResourceId;
    internal int QualityLevel => Key.QualityLevel;
    internal int Count => Key.Count;
    internal int Occurrence => Key.Occurrence;
}
