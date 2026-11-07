namespace Haiyu.ABI.Services.GameDataExport.Adapters.Inventory;

/// <summary>组合根只注册适配器；消息编号、字段及业务分支由各自的适配器/集合维护。</summary>
internal sealed class InventoryMessageRegistry
{
    internal static InventoryMessageRegistry Default { get; } = CreateDefault();
    internal IReadOnlyDictionary<ushort, InventoryMessageHandler> Handlers { get; }
    internal IReadOnlyList<string> Sources { get; }
    internal InventoryMessageRegistry(IEnumerable<IInventoryMessageAdapter> adapters)
    {
        Handlers = adapters.SelectMany(adapter => adapter.Handlers).ToDictionary(pair => pair.Key, pair => pair.Value);
        Sources = Array.AsReadOnly(Handlers.Values.SelectMany(handler => handler.Sources).Distinct(StringComparer.Ordinal).ToArray());
    }
    private static InventoryMessageRegistry CreateDefault()
    {
        var weapons = new WeaponMessageAdapter();
        var phantoms = new PhantomMessageAdapter();
        return new([new NormalItemMessageAdapter(), weapons, phantoms, new ValidTimeItemMessageAdapter(),
            new ItemFunctionMessageAdapter(weapons, phantoms)]);
    }
}
