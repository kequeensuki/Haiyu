using Haiyu.ABI.Services.GameDataExport.Aggregation.Collections;
using Haiyu.ABI.Services.GameDataExport.Aggregation.Contracts;
using Haiyu.ABI.Services.GameDataExport.Domains.Inventory;
using Haiyu.ABI.Services.GameDataExport.Protocol;

namespace Haiyu.ABI.Services.GameDataExport.Adapters.Inventory;

internal sealed record InventoryMessageChange(string SourceId,
    Func<InventorySnapshot>? Full = null, Func<InventorySnapshot, InventorySnapshot>? Apply = null, string? Error = null);

internal sealed record InventoryMessageHandler(IReadOnlyList<string> Sources, int ErrorField,
    Func<ProtocolMessage, Func<string, InventorySnapshot?>, IReadOnlyList<InventoryMessageChange>> Read)
{
    internal IReadOnlyList<InventoryMessageChange> Decode(ProtocolMessage message, Func<string, InventorySnapshot?> baseline) =>
        ErrorField != 0 && message.Int32(ErrorField) != 0 ? [] : Read(message, baseline);
}

internal interface IInventoryMessageAdapter
{
    IReadOnlyDictionary<ushort, InventoryMessageHandler> Handlers { get; }
}

/// <summary>协议注册和变更转换的公共管线；业务集合不认识协议字段编号。</summary>
internal abstract class InventoryMessageAdapter<TKey, TEntity, TChange>(string source,
    Func<IEnumerable<TEntity>, DataCollectionBase<TKey, TEntity, TChange>> create,
    Func<TEntity, long> retainedBytes) : IInventoryMessageAdapter where TKey : notnull where TEntity : class
{
    private readonly Dictionary<ushort, InventoryMessageHandler> _handlers = new();
    public IReadOnlyDictionary<ushort, InventoryMessageHandler> Handlers => _handlers;
    protected void Full(ushort id, int field, Func<ProtocolMessage, TEntity> read) =>
        _handlers.Add(id, new([source], 0, (message, _) =>
            [new(source, Full: () => Freeze(create(message.Messages(field).Select(read))))]));
    protected void On(ushort id, int errorField, Func<ProtocolMessage, IDataCollection<TKey, TEntity, TChange>, ChangeResult> apply) =>
        _handlers.Add(id, new([source], errorField, (message, _) => [Delta(message, apply)]));
    protected void Invalidate(ushort id, int errorField) =>
        _handlers.Add(id, new([source], errorField, (_, _) => [new(source, Error: "mutation_requires_request_context")]));
    protected InventoryMessageChange Delta(ProtocolMessage message,
        Func<ProtocolMessage, IDataCollection<TKey, TEntity, TChange>, ChangeResult> apply) => new(source, Apply: baseline =>
        {
            if (baseline is not InventorySnapshot<TEntity> typed) throw new InvalidDataException("背包来源类型不匹配。");
            var collection = create(typed.Items);
            var result = collection.ApplyBatch(items => apply(message, items));
            if (!result.Succeeded) throw new InvalidDataException(result.Error ?? result.Status.ToString());
            return result.Status == ChangeStatus.Unchanged ? baseline : Freeze(collection);
        });
    private InventorySnapshot<TEntity> Freeze(DataCollectionBase<TKey, TEntity, TChange> collection)
    {
        var items = collection.Snapshot();
        return new(items, 64 + items.Sum(item => 48 + retainedBytes(item)));
    }
    protected static ChangeResult Each<T>(IEnumerable<T> values, Func<T, ChangeResult> apply)
    {
        foreach (var value in values)
        {
            var result = apply(value);
            if (!result.Succeeded) return result;
        }
        return ChangeResult.Changed; // 集合事务会统一检查最终值是否实际改变。
    }
}
