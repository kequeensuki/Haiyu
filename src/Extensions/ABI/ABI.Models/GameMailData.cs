using System.Text.Json.Serialization;
using MemoryPack;

namespace ABI.Models;

[MemoryPackable]
public sealed partial record GameMailData([property: JsonPropertyName("items")] IReadOnlyList<GameMailItem> Items);

[MemoryPackable]
public sealed partial record GameMailAttachment(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("count")] int Count);

/// <summary>服务器原始邮件字段及按客户端状态位规则派生的展示字段，不补造本地邮箱内容。</summary>
[MemoryPackable]
public sealed partial record GameMailItem(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("receivedTime")] long ReceivedTime,
    [property: JsonPropertyName("readTime")] long ReadTime,
    [property: JsonPropertyName("state")] int State,
    [property: JsonPropertyName("level")] int Level,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("content")] string Content,
    [property: JsonPropertyName("sender")] string Sender,
    [property: JsonPropertyName("validTime")] int ValidTime,
    [property: JsonPropertyName("readValidTime")] int ReadValidTime,
    [property: JsonPropertyName("attachments")] IReadOnlyList<GameMailAttachment> Attachments,
    [property: JsonPropertyName("configId")] int ConfigId,
    [property: JsonPropertyName("expiryTime")] long ExpiryTime,
    [property: JsonPropertyName("isIntervalMail")] bool IsIntervalMail,
    [property: JsonPropertyName("isRead")] bool IsRead,
    [property: JsonPropertyName("isFavorite")] bool IsFavorite,
    [property: JsonPropertyName("attachmentStatus")] string AttachmentStatus);
