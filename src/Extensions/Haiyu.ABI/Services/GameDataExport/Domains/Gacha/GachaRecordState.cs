namespace Haiyu.ABI.Services.GameDataExport.Domains.Gacha;

/// <summary>
/// 一条抽卡记录的业务身份。
/// <para>
/// <b>为什么需要 <paramref name="Occurrence"/>：</b>接口没有唯一 ID，只给出池编号、时间、物品与数量；
/// 一次十连可能同秒抽到两把同名三星武器，仅按前 7 项去重会少计。occurrence 是同一键在单次响应中的序号。
/// </para>
/// <para>
/// <c>Time</c> 保留接口原始字符串：它是本地时间文本而非 epoch，转成 DateTime 会引入时区歧义。
/// </para>
/// </summary>
internal readonly record struct GachaRecordKey(
    int CardPoolType,
    string Time,
    int ResourceId,
    int QualityLevel,
    string ResourceType,
    string Name,
    int Count,
    int Occurrence) : IComparable<GachaRecordKey>
{
    /// <summary>稳定排序：时间倒序（新在前），同时间按池、物品、序号，保证输出可复现。</summary>
    public int CompareTo(GachaRecordKey other)
    {
        var byTime = string.CompareOrdinal(other.Time, Time);
        if (byTime != 0) return byTime;
        var byPool = CardPoolType.CompareTo(other.CardPoolType);
        if (byPool != 0) return byPool;
        var byResource = ResourceId.CompareTo(other.ResourceId);
        if (byResource != 0) return byResource;
        var byQuality = QualityLevel.CompareTo(other.QualityLevel);
        if (byQuality != 0) return byQuality;
        var byName = string.CompareOrdinal(Name, other.Name);
        return byName != 0 ? byName : Occurrence.CompareTo(other.Occurrence);
    }
}

/// <summary>一条抽卡记录。不可变；全部字段原样来自接口，不做名称或星级补全。</summary>
internal sealed record GachaRecordState(
    GachaRecordKey Key,
    string PoolLabel)
{
    internal int CardPoolType => Key.CardPoolType;
    internal string Time => Key.Time;
    internal int ResourceId => Key.ResourceId;
    internal int QualityLevel => Key.QualityLevel;
    internal string ResourceType => Key.ResourceType;
    internal string Name => Key.Name;
    internal int Count => Key.Count;
    internal int Occurrence => Key.Occurrence;
}
