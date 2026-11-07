using Haiyu.ABI.Services.GameDataExport.Aggregation.Collections;
using Haiyu.ABI.Services.GameDataExport.Aggregation.Contracts;
using Haiyu.ABI.Services.GameDataExport.Protocol;

namespace Haiyu.ABI.Services.GameDataExport.Adapters;

internal sealed record CollectionMessageChange(string SourceId,
    Func<CollectionSnapshot>? Full = null, Func<CollectionSnapshot, CollectionSnapshot>? Apply = null, string? Error = null);

internal sealed record CollectionMessageHandler(IReadOnlyList<string> Sources, int ErrorField,
    Func<ProtocolMessage, Func<string, CollectionSnapshot?>, IReadOnlyList<CollectionMessageChange>> Read)
{
    internal IReadOnlyList<CollectionMessageChange> Decode(ProtocolMessage message, Func<string, CollectionSnapshot?> baseline) =>
        ErrorField != 0 && message.Int32(ErrorField) != 0 ? [] : Read(message, baseline);
}

internal interface ICollectionMessageAdapter
{
    IReadOnlyDictionary<ushort, CollectionMessageHandler> Handlers { get; }
}

/// <summary>协议注册和变更转换的公共管线；业务集合不认识协议字段编号。</summary>
internal abstract class CollectionMessageAdapter<TKey, TEntity, TChange>(string source,
    Func<IEnumerable<TEntity>, DataCollectionBase<TKey, TEntity, TChange>> create,
    Func<TEntity, long> retainedBytes) : ICollectionMessageAdapter where TKey : notnull where TEntity : class
{
    private readonly Dictionary<ushort, CollectionMessageHandler> _handlers = new();
    public IReadOnlyDictionary<ushort, CollectionMessageHandler> Handlers => _handlers;
    protected void Full(ushort id, int field, Func<ProtocolMessage, TEntity> read) =>
        _handlers.Add(id, new([source], 0, (message, _) =>
            [new(source, Full: () => Freeze(create(message.Messages(field).Select(read))))]));
    protected void Full(ushort id, Func<ProtocolMessage, IEnumerable<TEntity>> read) =>
        _handlers.Add(id, new([source], 0, (message, _) => [new(source, Full: () => Freeze(create(read(message))))]));
    protected void FullChecked(ushort id, int errorField, Func<ProtocolMessage, IEnumerable<TEntity>> read) =>
        _handlers.Add(id, new([source], errorField, (message, _) => [new(source, Full: () => Freeze(create(read(message))))]));
    protected void Ignore(ushort id, int errorField) => _handlers.Add(id, new([source], errorField, (_, _) => []));
    /// <summary>首次列表建立登录基线；后续列表按完整对象合并，不能凭缺席删除或解除失同步。</summary>
    protected void InitialFullThenMerge(ushort id, int field, Func<ProtocolMessage, TEntity> read,
        Func<TEntity, TKey> key, Func<TEntity, TChange> change) =>
        _handlers.Add(id, new([source], 0, (message, baseline) => baseline(source) is null
            ? [new(source, Full: () => Freeze(create(message.Messages(field).Select(read))))]
            : [Delta(message, (model, items) => Each(model.Messages(field), value =>
                { var entity = read(value); return items.Update(key(entity), change(entity)); }))]));
    protected void On(ushort id, int errorField, Func<ProtocolMessage, IDataCollection<TKey, TEntity, TChange>, ChangeResult> apply,
        Func<ProtocolMessage, bool>? when = null) =>
        _handlers.Add(id, new([source], errorField, (message, _) => when is not null && !when(message) ? [] : [Delta(message, apply)]));
    protected void Invalidate(ushort id, int errorField) =>
        _handlers.Add(id, new([source], errorField, (_, _) => [new(source, Error: "mutation_requires_request_context")]));
    protected CollectionMessageChange Delta(ProtocolMessage message,
        Func<ProtocolMessage, IDataCollection<TKey, TEntity, TChange>, ChangeResult> apply) => new(source, Apply: baseline =>
        {
            if (baseline is not CollectionSnapshot<TEntity> typed) throw new InvalidDataException("数据来源类型不匹配。");
            var collection = create(typed.Items);
            var result = collection.ApplyBatch(items => apply(message, items));
            if (!result.Succeeded) throw new InvalidDataException(result.Error ?? result.Status.ToString());
            return result.Status == ChangeStatus.Unchanged ? baseline : Freeze(collection);
        });
    private CollectionSnapshot<TEntity> Freeze(DataCollectionBase<TKey, TEntity, TChange> collection)
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
