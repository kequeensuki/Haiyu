using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Haiyu.ABI.Services.GameDataExport.Gacha;

/// <summary>接口返回的单条原始记录；字段名与 <c>POST /gacha/record/query</c> 的 data 元素一致。</summary>
internal sealed record GachaWireRecord(
    string CardPoolType,
    int ResourceId,
    int QualityLevel,
    string ResourceType,
    string Name,
    int Count,
    string Time);

/// <summary>一次查询的结果。code 语义已实测：0=成功，-1=recordId/playerId 无效，-4=serverId 不符。</summary>
internal sealed record GachaQueryResult(int Code, string Message, IReadOnlyList<GachaWireRecord> Records)
{
    internal const int Success = 0;

    /// <summary>serverId 与账号区服不匹配；这是<b>唯一</b>可以换一个 serverId 重试的失败。</summary>
    internal const int ServerMismatch = -4;

    /// <summary>recordId 或 playerId 无效/过期；换 serverId 不会有帮助。</summary>
    internal const int InvalidRecord = -1;

    internal bool IsSuccess => Code == Success;

    /// <summary>成功但零条：某池确实没有记录，属于合法命中。</summary>
    internal bool IsEmptySuccess => Code == Success && Records.Count == 0;
}

/// <summary>抽卡记录查询端点，按区服选择。抽出来便于用本地 fixture 测试，不真的打生产接口。</summary>
internal interface IGachaRecordEndpoint
{
    /// <param name="serverId">本次尝试的区服 ID。</param>
    /// <param name="isCn">是否为国服；国服和国际服域名不同。</param>
    Task<GachaQueryResult> QueryAsync(
        string serverId,
        bool isCn,
        int cardPoolType,
        string playerId,
        string cardPoolId,
        string languageCode,
        string recordId,
        CancellationToken cancellationToken);
}

/// <summary>生产实现：<c>POST {cn|global}/gacha/record/query</c>。</summary>
internal sealed class HttpGachaRecordEndpoint(HttpClient client) : IGachaRecordEndpoint
{
    internal const string CnBaseUrl = "https://gmserver-api.aki-game2.com/gacha";
    internal const string GlobalBaseUrl = "https://gmserver-api.aki-game2.net/gacha";

    internal static HttpGachaRecordEndpoint Shared { get; } = new(new HttpClient { Timeout = TimeSpan.FromSeconds(15) });

    public async Task<GachaQueryResult> QueryAsync(string serverId, bool isCn, int cardPoolType,
        string playerId, string cardPoolId, string languageCode, string recordId,
        CancellationToken cancellationToken)
    {
        var baseUrl = isCn ? CnBaseUrl : GlobalBaseUrl;
        var payload = JsonSerializer.Serialize(new GachaHttpRequest(playerId, cardPoolId, cardPoolType,
            serverId, languageCode, recordId), GachaHttpJsonContext.Default.GachaHttpRequest);
        using var request = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/record/query")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
            .ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            return new GachaQueryResult(int.MinValue, string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"HTTP {(int)response.StatusCode}"), []);
        return Parse(body);
    }

    /// <summary>解析响应包络。成功判定与官方页面一致：code 为 0/200，或 message 为“成功”。</summary>
    internal static GachaQueryResult Parse(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("抽卡响应不是对象。");
            var hasCode = root.TryGetProperty("code", out _);
            var code = hasCode
                ? StrictJson.Int(root, "code", min: int.MinValue) : int.MinValue;
            var message = root.TryGetProperty("message", out _)
                ? StrictJson.String(root, "message", allowEmpty: true) : "";
            // 明确的失败码不能被 message 中的成功文字覆盖。
            var success = code is 0 or 200 || !hasCode && message == "成功";
            if (!success) return new GachaQueryResult(code, message, []);
            var records = new List<GachaWireRecord>();
            foreach (var item in StrictJson.Property(root, "data", JsonValueKind.Array).EnumerateArray())
                records.Add(new GachaWireRecord(
                    StrictJson.String(item, "cardPoolType", allowEmpty: true, allowNumber: true),
                    StrictJson.Int(item, "resourceId", min: 1, allowString: true),
                    StrictJson.Int(item, "qualityLevel", min: 1, allowString: true),
                    StrictJson.String(item, "resourceType"), StrictJson.String(item, "name"),
                    StrictJson.Int(item, "count", min: 1, allowString: true), StrictJson.String(item, "time")));
            return new GachaQueryResult(0, message, records);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("抽卡响应不是合法 JSON。", ex);
        }
    }
}

internal sealed record GachaHttpRequest(
    [property: JsonPropertyName("playerId")] string PlayerId,
    [property: JsonPropertyName("cardPoolId")] string CardPoolId,
    [property: JsonPropertyName("cardPoolType")] int CardPoolType,
    [property: JsonPropertyName("serverId")] string ServerId,
    [property: JsonPropertyName("languageCode")] string LanguageCode,
    [property: JsonPropertyName("recordId")] string RecordId);

[JsonSerializable(typeof(GachaHttpRequest))]
internal partial class GachaHttpJsonContext : JsonSerializerContext;
