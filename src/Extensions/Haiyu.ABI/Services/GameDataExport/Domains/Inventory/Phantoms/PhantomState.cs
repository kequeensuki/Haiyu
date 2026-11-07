namespace Haiyu.ABI.Services.GameDataExport.Domains.Inventory.Phantoms;

internal sealed record PhantomProperty(int Id, int Value);
internal sealed record PhantomState(int Id, int IncrId, int Level, int Exp,
    IReadOnlyList<PhantomProperty> MainProps, IReadOnlyList<PhantomProperty> SubProps,
    int FetterGroupId, int SkinId, IReadOnlyList<PhantomProperty> UnacknowledgedProps,
    IReadOnlyList<int> LockedProps, int Count, long AddTime, int FuncValue);

internal abstract record PhantomChange;
internal sealed record PhantomInventoryChanged(PhantomState Value) : PhantomChange;
internal sealed record PhantomCultivationChanged(PhantomState Value) : PhantomChange;
internal sealed record PhantomRemainderAdded(PhantomState Value) : PhantomChange;
internal sealed record PhantomFunctionChanged(int FuncValue) : PhantomChange;

internal sealed record PhantomStackSplit(PhantomState? Operate, PhantomState? Remainder);
internal sealed record PhantomStackMerge(int MergedIncrId, PhantomState? Representative);
internal sealed record PhantomUpgradeChanged(PhantomState Value, PhantomStackSplit? Split) : PhantomChange;
internal sealed record PhantomStacksChanged(IReadOnlyList<PhantomStackSplit> Splits,
    IReadOnlyList<PhantomStackMerge> Merges, IReadOnlyList<PhantomState> Updates) : PhantomChange;
