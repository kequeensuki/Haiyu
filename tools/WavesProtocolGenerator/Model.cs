namespace WavesProtocolGenerator;
internal sealed record SourceType(string Name, string FlatName, string Path, Microsoft.CodeAnalysis.CSharp.Syntax.BaseTypeDeclarationSyntax Syntax);
internal sealed record Field(int Number, string Name, string Type, bool Repeated, string? MapKey, string? Oneof, bool Optional, bool Packed);
internal sealed record Message(string Name, string FlatName, string Source, Field[] Fields);
internal sealed record EnumValue(string Name, int Number, string? OriginalName = null);
internal sealed record Enumeration(string Name, string FlatName, EnumValue[] Values);
internal sealed record ProtocolModel(Message[] Messages, Enumeration[] Enums, SortedDictionary<int, string> Ids, string SourceCommit, string SourceHash,
    [property: System.Text.Json.Serialization.JsonIgnore] SortedDictionary<string, int> IdNames);
