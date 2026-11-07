namespace Haiyu.ABI.Services.GameDataExport.Domains.Roles;

internal sealed record RoleValue(int Id, int Value);
internal sealed record RoleSkillNode(int Id, bool Active, int SkillId);
internal sealed record RoleState(int Id, string Name, int Level, int Exp, int Breakthrough,
    IReadOnlyList<RoleValue> Skills, IReadOnlyList<RoleValue> BaseProps, IReadOnlyList<RoleValue> AddProps,
    IReadOnlyList<RoleSkillNode> SkillNodes, bool IsInitialized = true);
internal abstract record RoleChange;
internal sealed record RoleReplaced(RoleState Value) : RoleChange;
internal sealed record RoleSkillChanged(RoleValue Value) : RoleChange;
internal sealed record RoleAttributesChanged(IReadOnlyList<RoleValue> BaseProps, IReadOnlyList<RoleValue> AddProps,
    bool MergeKeys = false, bool IgnoreMissing = false) : RoleChange;
internal sealed record RoleLevelChanged(int Level, int Exp) : RoleChange;
internal sealed record RoleBreakthroughChanged(int Breakthrough) : RoleChange;
internal sealed record RoleNameChanged(string Name) : RoleChange;
internal sealed record RoleNodesChanged(IReadOnlyList<RoleSkillNode> Nodes) : RoleChange;
internal sealed record RoleEquipmentState(int Id, IReadOnlyList<int>? Slots,
    IReadOnlyList<RoleValue>? BaseProps, IReadOnlyList<RoleValue>? AddProps);
internal sealed record RoleEquipmentChange(RoleEquipmentState Value);
