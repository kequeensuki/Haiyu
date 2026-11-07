using Haiyu.ABI.Services.GameDataExport.Aggregation.Collections;

namespace Haiyu.ABI.Services.GameDataExport.Domains.Inventory.NormalItems;

internal sealed class NormalItemCollection(IEnumerable<NormalItemState> items)
    : DataCollectionBase<int, NormalItemState, NormalItemChange>(items, item => item.Id, key => key > 0, Comparer<int>.Default)
{
    protected override NormalItemState Merge(NormalItemState current, NormalItemChange change) =>
        current with { Count = change.Count }; // 服务器发来的是绝对数量，获得/过期信息不随数量更新覆盖。
}
