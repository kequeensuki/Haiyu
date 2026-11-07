namespace Haiyu.ABI.Services.GameDataExport.Domains.Inventory.ValidTimeItems;

internal sealed record ValidTimeItemState(int Id, int IncrId, int Count, long ExpireTime);
internal sealed record ValidTimeItemChange(int Count, long ExpireTime);
