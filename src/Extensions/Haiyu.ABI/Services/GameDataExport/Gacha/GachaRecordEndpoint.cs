using System.Text;
using System.Text.Json;

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

    private static readonly JsonSerializerOptions RequestOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
    };

    public async Task<GachaQueryResult> QueryAsync(string serverId, bool isCn, int cardPoolType,
        string playerId, string cardPoolId, string languageCode, string recordId,
        CancellationToken cancellationToken)
    {
        var baseUrl = isCn ? CnBaseUrl : GlobalBaseUrl;
        var payload = JsonSerializer.Serialize(new
        {
            playerId,
            cardPoolId,
            cardPoolType,
            serverId,
            languageCode,
            recordId,
        }, RequestOptions);
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
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var code = root.TryGetProperty("code", out var codeElement) && codeElement.TryGetInt32(out var parsed)
            ? parsed : int.MinValue;
        var message = root.TryGetProperty("message", out var messageElement)
            ? messageElement.GetString() ?? "" : "";
        var records = new List<GachaWireRecord>();
        if (root.TryGetProperty("data", out var dataElement) && dataElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in dataElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                records.Add(new GachaWireRecord(
                    ReadString(item, "cardPoolType"),
                    ReadInt32(item, "resourceId"),
                    ReadInt32(item, "qualityLevel"),
                    ReadString(item, "resourceType"),
                    ReadString(item, "name"),
                    ReadInt32(item, "count"),
                    ReadString(item, "time")));
            }
        }
        // 页面把 200 也视作成功；这里统一折叠成 0，避免上层分叉。
        var normalized = code == 200 || code == GachaQueryResult.Success
            || string.Equals(message, "成功", StringComparison.Ordinal)
            ? GachaQueryResult.Success : code;
        return new GachaQueryResult(normalized, message, records);
    }

    private static string ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) ? value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? "",
            JsonValueKind.Number => value.GetRawText(),
            _ => "",
        } : "";

    private static int ReadInt32(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return 0;
        return value.ValueKind switch
        {
            JsonValueKind.Number => value.TryGetInt32(out var number) ? number : 0,
            JsonValueKind.String => int.TryParse(value.GetString(), System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : 0,
            _ => 0,
        };
    }
}
