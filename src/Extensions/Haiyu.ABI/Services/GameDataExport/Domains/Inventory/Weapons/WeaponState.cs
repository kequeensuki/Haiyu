namespace Haiyu.ABI.Services.GameDataExport.Domains.Inventory.Weapons;

internal sealed record WeaponState(int Id, int IncrId, int Level, int Exp, int Breach, int Resonance, int RoleId, int FuncValue);

internal abstract record WeaponChange;
internal sealed record WeaponLevelChanged(int Level, int Exp) : WeaponChange;
internal sealed record WeaponBreachChanged(int Breach) : WeaponChange;
internal sealed record WeaponResonanceChanged(int Resonance) : WeaponChange;
internal sealed record WeaponEquipmentChanged(int RoleId) : WeaponChange;
internal sealed record WeaponFunctionChanged(int FuncValue) : WeaponChange;
