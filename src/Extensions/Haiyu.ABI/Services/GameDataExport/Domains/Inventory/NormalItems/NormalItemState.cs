namespace Haiyu.ABI.Services.GameDataExport.Domains.Inventory.NormalItems;

internal sealed record NormalItemState(int Id, int Count, long ExpireTime);
internal sealed record NormalItemChange(int Count);
