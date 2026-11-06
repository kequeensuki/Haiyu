using System.Text.Json.Serialization;
using MemoryPack;

namespace ABI.Models;

[JsonConverter(typeof(JsonStringEnumConverter<GameDataExportModuleState>))]
public enum GameDataExportModuleState
{
    Running,
    Waiting,
    Stopping,
    Completed,
    NoData,
    Incomplete,
    Failed,
}

[MemoryPackable]
public sealed partial record GameDataExportModuleDescriptor(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("nameKey")] string NameKey,
    [property: JsonPropertyName("displayName")] string DisplayName,
    [property: JsonPropertyName("defaultSelected")] bool DefaultSelected,
    [property: JsonPropertyName("requiresIdentity")] bool RequiresIdentity
);

[MemoryPackable]
public sealed partial record GameDataExportModuleCatalogRequest();

[MemoryPackable]
public sealed partial record GameDataExportModuleCatalogResult(
    [property: JsonPropertyName("modules")] GameDataExportModuleDescriptor[] Modules
);

[MemoryPackable]
public sealed partial record GameDataExportCounter(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("value")] long Value,
    [property: JsonPropertyName("playerId")] int? PlayerId = null
);

[MemoryPackable]
public sealed partial record GameDataExportSourceProgress(
    [property: JsonPropertyName("sourceId")] string SourceId,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("required")] bool Required,
    [property: JsonPropertyName("localVersion")] long LocalVersion,
    [property: JsonPropertyName("serverVersion")] long? ServerVersion,
    [property: JsonPropertyName("batchId")] string? BatchId,
    [property: JsonPropertyName("error")] string? Error,
    [property: JsonPropertyName("firstMessageIndex")] long? FirstMessageIndex = null,
    [property: JsonPropertyName("lastMessageIndex")] long? LastMessageIndex = null,
    [property: JsonPropertyName("receivedAt")] DateTimeOffset? ReceivedAt = null,
    [property: JsonPropertyName("connectionId")] uint? ConnectionId = null,
    [property: JsonPropertyName("sessionId")] string? SessionId = null,
    [property: JsonPropertyName("playerId")] int? PlayerId = null
);

[MemoryPackable]
public sealed partial record GameDataExportArtifact(
    [property: JsonPropertyName("moduleId")] string ModuleId,
    [property: JsonPropertyName("playerId")] int? PlayerId,
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("format")] string Format,
    [property: JsonPropertyName("revision")] long Revision,
    [property: JsonPropertyName("consistency")] string Consistency,
    [property: JsonPropertyName("sources")] GameDataExportSourceProgress[] Sources
);

[MemoryPackable]
public sealed partial record GameDataExportModuleResult(
    [property: JsonPropertyName("moduleId")] string ModuleId,
    [property: JsonPropertyName("displayName")] string DisplayName,
    [property: JsonPropertyName("state")] GameDataExportModuleState State,
    [property: JsonPropertyName("error")] string? Error,
    [property: JsonPropertyName("counters")] GameDataExportCounter[] Counters,
    [property: JsonPropertyName("sources")] GameDataExportSourceProgress[] Sources,
    [property: JsonPropertyName("artifacts")] GameDataExportArtifact[] Artifacts
);
