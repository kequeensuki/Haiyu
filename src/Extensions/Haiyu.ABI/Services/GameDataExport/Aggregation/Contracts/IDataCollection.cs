namespace Haiyu.ABI.Services.GameDataExport.Aggregation.Contracts;

/// <summary>管理一类不可变业务对象；不解释协议，也不管理来源基线或落盘版本。</summary>
internal interface IDataCollection<TKey, TEntity, in TChange> where TKey : notnull
{
    ChangeResult Add(TEntity entity);
    ChangeResult Update(TKey key, TChange change);
    ChangeResult Remove(TKey key);
    IReadOnlyList<TEntity> Snapshot();
}
