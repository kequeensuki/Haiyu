using Haiyu.ABI.Protocol;
using Haiyu.ABI.Services.GameDataExport.Aggregation.Collections;
using Haiyu.ABI.Services.GameDataExport.Domains.Inventory.Phantoms;
using Haiyu.ABI.Services.GameDataExport.Domains.Inventory.Weapons;

namespace Haiyu.ABI.Services.GameDataExport.Adapters.Inventory;

/// <summary>跨来源通知的路由留在协议边界，模块无需认识特殊消息编号。</summary>
internal sealed class ItemFunctionMessageAdapter : ICollectionMessageAdapter
{
    public IReadOnlyDictionary<ushort, CollectionMessageHandler> Handlers { get; }
    internal ItemFunctionMessageAdapter(WeaponMessageAdapter weapons, PhantomMessageAdapter phantoms)
    {
        Handlers = new Dictionary<ushort, CollectionMessageHandler>
        {
            [WavesMessageIds.ItemFuncValueUpdateNotify] = new(["weapons", "phantoms"], 0, (message, baseline) =>
            {
                var id = message.Int32(1);
                var weapon = baseline("weapons") is CollectionSnapshot<WeaponState> w && w.Items.Any(item => item.IncrId == id);
                var phantom = baseline("phantoms") is CollectionSnapshot<PhantomState> p && p.Items.Any(item => item.IncrId == id);
                if (weapon == phantom) return [new("weapons", Error: "item_identity_unresolved"), new("phantoms", Error: "item_identity_unresolved")];
                return [weapon ? weapons.FunctionChange(message) : phantoms.FunctionChange(message)];
            })
        };
    }
}
