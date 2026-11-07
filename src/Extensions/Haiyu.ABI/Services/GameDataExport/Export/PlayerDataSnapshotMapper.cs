using ABI.Models;
using Haiyu.ABI.Services.GameDataExport.Domains.Roles;
using Haiyu.ABI.Services.GameDataExport.Domains.Achievements;
using Haiyu.ABI.Services.GameDataExport.Domains.Mail;
using Haiyu.ABI.Services.GameDataExport.Aggregation.Collections;
using Haiyu.ABI.Services.GameDataExport.Domains.Inventory.NormalItems;
using Haiyu.ABI.Services.GameDataExport.Domains.Inventory.ValidTimeItems;
using Haiyu.ABI.Services.GameDataExport.Domains.Inventory.Weapons;
using Haiyu.ABI.Services.GameDataExport.Domains.Inventory.Phantoms;

namespace Haiyu.ABI.Services.GameDataExport.Export;

/// <summary>业务快照到稳定 ABI/JSON 格式的唯一映射，不参与协议解析或业务合并。</summary>
internal static class PlayerDataSnapshotMapper
{
    internal static GameInventoryData Map(IReadOnlyDictionary<string, CollectionSnapshot> values) => new(
        Array.AsReadOnly(((CollectionSnapshot<NormalItemState>)values["normalItems"]).Items.Select(item =>
            new GameNormalItem(item.Id, item.Count, item.ExpireTime)).ToArray()),
        Array.AsReadOnly(((CollectionSnapshot<WeaponState>)values["weapons"]).Items.Select(item =>
            new GameWeaponItem(item.Id, item.IncrId, item.Level, item.Exp, item.Breach, item.Resonance, item.RoleId)).ToArray()),
        Array.AsReadOnly(((CollectionSnapshot<PhantomState>)values["phantoms"]).Items.Select(item =>
            new GamePhantomItem(item.Id, item.IncrId, item.Level, item.Exp, Props(item.MainProps), Props(item.SubProps),
                item.FetterGroupId, item.SkinId, item.Count, Props(item.UnacknowledgedProps),
                item.LockedProps, item.AddTime, item.FuncValue)).ToArray()),
        Array.AsReadOnly(((CollectionSnapshot<ValidTimeItemState>)values["validTimeItems"]).Items.Select(item =>
            new GameValidTimeItem(item.Id, item.Count, item.IncrId, item.ExpireTime)).ToArray()));

    internal static GameMailData? MapMails(IReadOnlyDictionary<string, CollectionSnapshot> values) =>
        values.TryGetValue("mails", out var snapshot) && snapshot is CollectionSnapshot<MailState> mails
            ? new(Array.AsReadOnly(mails.Items.Select(mail => new GameMailItem(mail.Id, mail.ReceivedTime,
                mail.ReadTime, mail.State, mail.Level, mail.Title, mail.Content, mail.Sender, mail.ValidTime,
                mail.ReadValidTime, Array.AsReadOnly(mail.Attachments.Select(item => new GameMailAttachment(item.Id, item.Count)).ToArray()),
                mail.ConfigId, mail.ExpiryTime, mail.IsIntervalMail, (mail.State & 1) != 0, (mail.State & 4) != 0,
                mail.Attachments.Count == 0 ? "none" : (mail.State & 2) != 0 ? "claimed" : "unclaimed")).ToArray()))
            : null;

    internal static GameAchievementData? MapAchievements(IReadOnlyDictionary<string, CollectionSnapshot> values)
    {
        if (!values.TryGetValue("achievements", out var snapshot)) return null;
        var states = ((CollectionSnapshot<AchievementState>)snapshot).Items;
        var summary = states.Single(item => item.Kind == AchievementKind.Summary);
        return new(summary.FinishCount, summary.StarCount,
            Array.AsReadOnly(states.Where(item => item.Kind == AchievementKind.Entry).Select(item =>
                new GameAchievementItem(item.Id, item.GroupId, item.FinishTime, item.IsReceive, item.CurrentProgress,
                    item.TotalProgress, item.IsCompleted, item.IsReceive, item.RewardStatus)).ToArray()),
            Array.AsReadOnly(states.Where(item => item.Kind == AchievementKind.Group).Select(item =>
                new GameAchievementGroup(item.Id, item.FinishTime, item.IsReceive, item.IsCompleted,
                    item.IsReceive, item.RewardStatus)).ToArray()));
    }

    internal static GameRoleData? MapRoles(IReadOnlyDictionary<string, CollectionSnapshot> values)
    {
        if (!values.TryGetValue("roles", out var roster)) return null;
        var equipment = values.TryGetValue("roleEquipment", out var gear)
            ? ((CollectionSnapshot<RoleEquipmentState>)gear).Items.ToDictionary(r => r.Id) : new Dictionary<int, RoleEquipmentState>();
        var phantoms = ((CollectionSnapshot<PhantomState>)values["phantoms"]).Items.ToDictionary(p => p.IncrId);
        return new(Array.AsReadOnly(((CollectionSnapshot<RoleState>)roster).Items.Select(role =>
        {
            equipment.TryGetValue(role.Id, out var equipped);
            var slots = equipped?.Slots ?? (gear is not null ? Array.Empty<int>() : null);
            var echoes = slots is null ? Array.Empty<GameRoleEcho>() : Enumerable.Range(0, 5).Select(index =>
            {
                var id = index < slots.Count ? slots[index] : 0;
                phantoms.TryGetValue(id, out var item);
                var status = id == 0 ? "empty" : item is null ? "unresolved" : item.Level is < 0 or > 25 ? "invalid_level" : "ready";
                return new GameRoleEcho(index + 1, index == 0, id, status, item is null ? null : MapPhantom(item));
            }).ToArray();
            var status = slots is null ? "missing" : echoes.Any(e => e.Status is "unresolved" or "invalid_level") ? "incomplete" : "ready";
            // 客户端 RoleAttributeData.GetAttrValueById：同ID base+add。保持协议单位，不重复加声骸贡献。
            var attributes = role.BaseProps.Concat(role.AddProps).GroupBy(p => p.Id).OrderBy(g => g.Key)
                .Select(g => new GameRoleValue(g.Key, g.Sum(p => (long)p.Value))).ToArray();
            return new GameRoleItem(role.Id, role.Name, role.Level, role.Exp, role.Breakthrough,
                RoleValues(role.Skills), Array.AsReadOnly(role.SkillNodes.Select(n => new GameRoleSkillNode(n.Id, n.Active, n.SkillId)).ToArray()),
                RoleValues(role.BaseProps), RoleValues(role.AddProps), Array.AsReadOnly(attributes), status,
                Array.AsReadOnly(echoes), equipped?.BaseProps is { } bp ? RoleValues(bp) : null,
                equipped?.AddProps is { } ap ? RoleValues(ap) : null);
        }).ToArray()));
    }
    private static IReadOnlyList<GameRoleValue> RoleValues(IReadOnlyList<RoleValue> values) =>
        Array.AsReadOnly(values.Select(v => new GameRoleValue(v.Id, v.Value)).ToArray());
    private static GamePhantomItem MapPhantom(PhantomState item) => new(item.Id, item.IncrId, item.Level, item.Exp,
        Props(item.MainProps), Props(item.SubProps), item.FetterGroupId, item.SkinId, item.Count,
        Props(item.UnacknowledgedProps), item.LockedProps, item.AddTime, item.FuncValue);

    private static IReadOnlyList<GamePhantomProp> Props(IReadOnlyList<PhantomProperty> props) =>
        Array.AsReadOnly(props.Select(prop => new GamePhantomProp(prop.Id, prop.Value)).ToArray());
}
