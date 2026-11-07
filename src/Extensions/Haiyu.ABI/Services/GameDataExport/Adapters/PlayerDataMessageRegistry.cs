using Haiyu.ABI.Services.GameDataExport.Adapters.Inventory;
using Haiyu.ABI.Services.GameDataExport.Adapters.Roles;
using Haiyu.ABI.Services.GameDataExport.Adapters.Achievements;
using Haiyu.ABI.Services.GameDataExport.Adapters.Mail;

namespace Haiyu.ABI.Services.GameDataExport.Adapters;

/// <summary>组合根只注册适配器；消息编号、字段及业务分支由各自的适配器/集合维护。</summary>
internal sealed class PlayerDataMessageRegistry
{
    internal static PlayerDataMessageRegistry Default { get; } = CreateDefault();
    internal IReadOnlyDictionary<ushort, CollectionMessageHandler> Handlers { get; }
    internal IReadOnlyList<string> Sources { get; }
    internal IReadOnlySet<string> OptionalSources { get; }
    internal PlayerDataMessageRegistry(IEnumerable<ICollectionMessageAdapter> adapters, IReadOnlySet<string>? optionalSources = null)
    {
        OptionalSources = optionalSources ?? new HashSet<string>();
        var handlers = new Dictionary<ushort, CollectionMessageHandler>();
        foreach (var group in adapters.SelectMany(adapter => adapter.Handlers).GroupBy(pair => pair.Key))
        {
            var consumers = group.Select(pair => pair.Value).ToArray();
            var sources = consumers.SelectMany(h => h.Sources).ToArray();
            // 同一消息允许投递给不同业务集合；同一来源重复注册仍是配置错误。
            if (sources.Distinct(StringComparer.Ordinal).Count() != sources.Length)
                throw new ArgumentException("同一消息的数据来源重复注册。");
            handlers.Add(group.Key, consumers.Length == 1 ? consumers[0] : new(sources, 0,
                (message, baseline) => consumers.SelectMany(h => h.Decode(message, baseline)).ToArray()));
        }
        Handlers = handlers;
        Sources = Array.AsReadOnly(Handlers.Values.SelectMany(handler => handler.Sources).Distinct(StringComparer.Ordinal).ToArray());
    }
    private static PlayerDataMessageRegistry CreateDefault()
    {
        var weapons = new WeaponMessageAdapter();
        var phantoms = new PhantomMessageAdapter();
        return new([new NormalItemMessageAdapter(), weapons, phantoms, new ValidTimeItemMessageAdapter(),
            new ItemFunctionMessageAdapter(weapons, phantoms), new MailMessageAdapter(), new AchievementMessageAdapter(), new RoleMessageAdapter(), new RoleEquipmentMessageAdapter()], new HashSet<string> { "mails", "achievements", "roles", "roleEquipment" });
    }
}
