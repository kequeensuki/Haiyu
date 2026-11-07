namespace Haiyu.ABI.Services.GameDataExport.Gacha;

/// <summary>serverId 解析结果。</summary>
internal sealed record GachaServerResolution(
    string ServerId,
    GachaRegion Region,
    /// <summary>cache / probe / given。</summary>
    string ResolvedBy,
    /// <summary>探测命中时顺带拿到的首批记录，避免重复请求；其余情况为 null。</summary>
    IReadOnlyList<GachaWireRecord>? FirstRecords = null,
    int FirstPoolType = 0)
{
    internal string Area => GachaServerIds.AreaOf(ServerId);
    internal bool IsCn => Region == GachaRegion.Cn;
}

/// <summary>
/// 区服判定：以接口实测为准，而不是猜上下文。
/// <para>
/// <b>实测依据</b>（同一账号、同一 recordId）：
/// 正确 serverId → <c>code=0</c>（即使该池 0 条也是 0）；
/// 错误 serverId → <c>code=-4</c>（与"有没有记录"无关）；
/// recordId 失效 → <c>code=-1</c>（换 serverId 无效）。
/// </para>
/// </summary>
internal sealed class GachaServerResolver(IGachaRecordEndpoint endpoint, IGachaServerCache cache)
{
    /// <summary>探测固定用角色活动池；命中时的数据可直接作为该池的首批结果。</summary>
    internal const int ProbePoolType = 1;

    /// <summary>
    /// 解析顺序：已确认的给定值 → 缓存 → 按首选顺序探测两个常量。
    /// </summary>
    /// <param name="given">调用方显式指定（测试或高级用法）；非空时不探测。</param>
    /// <param name="preferred">上下文推断的偏好区服，只决定先试哪个，不作为真相来源。</param>
    /// <param name="refresh">true 表示缓存值已失效，需要跳过缓存重新探测。</param>
    internal async Task<GachaServerResolution> ResolveAsync(
        int playerId,
        string? given,
        GachaRegion preferred,
        string playerIdText,
        string cardPoolId,
        string languageCode,
        string recordId,
        bool refresh,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(given))
            return new(given, GachaServerIds.RegionOf(given), "given");

        if (!refresh && cache.TryGet(playerId) is { } cached && !string.IsNullOrWhiteSpace(cached.ServerId))
            return new(cached.ServerId, GachaServerIds.RegionOf(cached.ServerId), "cache");

        GachaQueryResult? lastFailure = null;
        foreach (var (region, serverId) in GachaServerIds.Candidates(preferred))
        {
            var result = await endpoint.QueryAsync(serverId, region == GachaRegion.Cn, ProbePoolType,
                playerIdText, cardPoolId, languageCode, recordId, cancellationToken).ConfigureAwait(false);

            if (result.IsSuccess)
            {
                cache.Set(playerId, new(serverId, languageCode, DateTimeOffset.UtcNow));
                return new(serverId, region, "probe", result.Records, ProbePoolType);
            }

            // -4 是唯一值得换下一个 serverId 的失败；-1 表示 recordId 失效，换也没用。
            if (result.Code == GachaQueryResult.ServerMismatch)
            {
                lastFailure = result;
                continue;
            }

            throw new InvalidDataException(DescribeFailure(result));
        }

        throw new InvalidDataException(DescribeFailure(lastFailure
            ?? new GachaQueryResult(GachaQueryResult.ServerMismatch, "两个 serverId 都被拒绝。", [])));
    }

    internal static string DescribeFailure(GachaQueryResult result) => result.Code switch
    {
        GachaQueryResult.ServerMismatch =>
            "区服判定失败：两个 serverId 都返回 -4（查询不到区服信息）。可能是常量过期或账号区服异常。",
        GachaQueryResult.InvalidRecord =>
            "抽卡记录凭证无效或已过期（code=-1）。等待新的 GachaInfoResponse 刷新 record_id 后重试。",
        int.MinValue => "抽卡记录接口不可用：" + result.Message,
        _ => $"抽卡记录接口返回 code={result.Code}：{result.Message}",
    };
}
