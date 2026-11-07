using Haiyu.ABI.Services.GameDataExport.Aggregation.Collections;

namespace Haiyu.ABI.Services.GameDataExport.Domains.Inventory.ValidTimeItems;

internal sealed class ValidTimeItemCollection(IEnumerable<ValidTimeItemState> items)
    : DataCollectionBase<(int Id, int IncrId), ValidTimeItemState, ValidTimeItemChange>(
        items, item => (item.Id, item.IncrId), key => key.Id > 0, Comparer<(int Id, int IncrId)>.Default)
{
    protected override ValidTimeItemState Merge(ValidTimeItemState current, ValidTimeItemChange change) =>
        current with { Count = change.Count, ExpireTime = change.ExpireTime };
}
