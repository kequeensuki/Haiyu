using System.Text.Json.Serialization;
using MemoryPack;

namespace ABI.Models;

/*
 抽卡记录（Gacha）ABI 模型。
 数据来自 HTTP 抽卡记录接口，不来自 TCP 下行；
 产物独立落盘到 <playerId>/gacha/record.json，不并入 game-data.json。
 */

/// <summary>
/// 抽卡记录拉取参数。由主程序在启动导出时填入（recordId 来自抓包得到的 GachaInfoResponse）。
/// 全部字段为空时模块报告 NoData，不发起任何网络请求。
/// </summary>
[MemoryPackable]
public sealed partial record GameGachaRequest(
    /// <summary>玩家 UID。0 表示未知，模块不会拉取。</summary>
    [property: JsonPropertyName("playerId")] int PlayerId = 0,
    /// <summary>历史记录查询凭证，来自 GachaInfoResponse.record_id。</summary>
    [property: JsonPropertyName("recordId")] string? RecordId = null,
    /// <summary>卡池资源 ID，来自 GachaInfo.resources_id。接口不校验，可为空。</summary>
    [property: JsonPropertyName("cardPoolId")] string? CardPoolId = null,
    /// <summary>语言代码，影响接口返回；错值会静默返回空列表。</summary>
    [property: JsonPropertyName("languageCode")] string? LanguageCode = null,
    /// <summary>候选 serverId，按优先顺序尝试（国服 / 国际服）。</summary>
    [property: JsonPropertyName("serverIds")] string[]? ServerIds = null,
    /// <summary>要拉取的池类型编号；为空时使用官方页面常量。</summary>
    [property: JsonPropertyName("poolTypes")] int[]? PoolTypes = null,
    /// <summary>请求发起时间，用于文件元数据；为空时取模块时钟。</summary>
    [property: JsonPropertyName("requestedAt")] DateTimeOffset? RequestedAt = null
);

/// <summary>一次抽卡记录导出的结果。文件是权威产物，这里携带同样的内容供 UI 与测试使用。</summary>
[MemoryPackable]
public sealed partial record GameGachaData(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("playerId")] int PlayerId,
    [property: JsonPropertyName("updatedAt")] string UpdatedAt,
    [property: JsonPropertyName("query")] GameGachaQuery Query,
    [property: JsonPropertyName("totals")] GameGachaTotals Totals,
    [property: JsonPropertyName("pools")] IReadOnlyList<GameGachaPool> Pools
);

/// <summary>本次查询实际使用的身份参数与结果，便于复核"为什么是这个区服"。</summary>
[MemoryPackable]
public sealed partial record GameGachaQuery(
    [property: JsonPropertyName("serverId")] string ServerId,
    [property: JsonPropertyName("serverArea")] string ServerArea,
    [property: JsonPropertyName("recordId")] string RecordId,
    [property: JsonPropertyName("cardPoolId")] string CardPoolId,
    [property: JsonPropertyName("languageCode")] string LanguageCode,
    /// <summary>serverId 的判定方式：cache / probe / given。</summary>
    [property: JsonPropertyName("serverResolvedBy")] string ServerResolvedBy,
    [property: JsonPropertyName("fetchedPoolTypes")] IReadOnlyList<int> FetchedPoolTypes,
    [property: JsonPropertyName("failedPoolTypes")] IReadOnlyList<int> FailedPoolTypes
);

[MemoryPackable]
public sealed partial record GameGachaTotals(
    [property: JsonPropertyName("count")] int Count,
    [property: JsonPropertyName("poolCount")] int PoolCount,
    [property: JsonPropertyName("firstTime")] string? FirstTime,
    [property: JsonPropertyName("lastTime")] string? LastTime
);

[MemoryPackable]
public sealed partial record GameGachaPool(
    [property: JsonPropertyName("cardPoolType")] int CardPoolType,
    [property: JsonPropertyName("count")] int Count,
    [property: JsonPropertyName("items")] IReadOnlyList<GameGachaRecordItem> Items
);

/// <summary>
/// 单条抽卡记录。字段原样来自接口，不做名称/星级补全。
/// <c>cardPoolType</c> 使用**请求时**的池编号：接口返回的同名字段是本地化显示文本，
/// 且可能只是裸数字，不可用于分类。
/// </summary>
[MemoryPackable]
public sealed partial record GameGachaRecordItem(
    [property: JsonPropertyName("cardPoolType")] int CardPoolType,
    /// <summary>接口返回的显示文本（可能为空或未本地化），仅作展示，不参与判定。</summary>
    [property: JsonPropertyName("poolLabel")] string PoolLabel,
    [property: JsonPropertyName("resourceId")] int ResourceId,
    [property: JsonPropertyName("qualityLevel")] int QualityLevel,
    [property: JsonPropertyName("resourceType")] string ResourceType,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("count")] int Count,
    [property: JsonPropertyName("time")] string Time,
    /// <summary>同键在本次拉取中的出现序号；用于区分同秒同名的重复抽取。</summary>
    [property: JsonPropertyName("occurrence")] int Occurrence
);

/// <summary>导出结果里的抽卡摘要，只用于进度/提示，不重复携带全部条目。</summary>
[MemoryPackable]
public sealed partial record GameGachaSummary(
    [property: JsonPropertyName("playerId")] int PlayerId,
    [property: JsonPropertyName("recordCount")] int RecordCount,
    [property: JsonPropertyName("poolCount")] int PoolCount,
    [property: JsonPropertyName("serverId")] string ServerId,
    [property: JsonPropertyName("serverArea")] string ServerArea,
    [property: JsonPropertyName("outputPath")] string? OutputPath,
    [property: JsonPropertyName("error")] string? Error = null
);
