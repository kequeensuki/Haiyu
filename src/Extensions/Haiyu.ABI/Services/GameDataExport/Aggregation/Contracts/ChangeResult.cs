namespace Haiyu.ABI.Services.GameDataExport.Aggregation.Contracts;

internal enum ChangeStatus { Changed, Unchanged, MissingTarget, Invalid, RequiresBaseline }

internal readonly record struct ChangeResult(ChangeStatus Status, string? Error = null)
{
    internal bool Succeeded => Status is ChangeStatus.Changed or ChangeStatus.Unchanged;
    internal static ChangeResult Changed => new(ChangeStatus.Changed);
    internal static ChangeResult Unchanged => new(ChangeStatus.Unchanged);
    internal static ChangeResult Missing => new(ChangeStatus.MissingTarget, "mutation_target_missing");
    internal static ChangeResult Invalid(string error) => new(ChangeStatus.Invalid, error);
}
