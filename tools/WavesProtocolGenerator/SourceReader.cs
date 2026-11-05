using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace WavesProtocolGenerator;
internal sealed class SourceReader
{
    private readonly Dictionary<string, SourceType> _types = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CompilationUnitSyntax> _units = new();
    private static readonly Dictionary<string, string> Scalars = new()
    {
        ["int"] = "int32",
        ["uint"] = "uint32",
        ["long"] = "int64",
        ["ulong"] = "uint64",
        ["bool"] = "bool",
        ["string"] = "string",
        ["ByteString"] = "bytes",
        ["float"] = "float",
        ["double"] = "double"
    };
    private static readonly HashSet<string> WireScalars = ["Int32", "Int64", "UInt32", "UInt64", "SInt32", "SInt64", "Fixed32", "Fixed64", "SFixed32", "SFixed64", "Bool", "String", "Bytes", "Float", "Double"];
    internal ProtocolModel Read(string source, string commit)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var path in Directory.GetFiles(source, "*.cs", SearchOption.AllDirectories)
            .OrderBy(path => Path.GetRelativePath(source, path).Replace('\\', '/'), StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(source, path).Replace('\\', '/');
            var sourceText = File.ReadAllText(path).TrimStart('\uFEFF').Replace("\r\n", "\n");
            hash.AppendData(Encoding.UTF8.GetBytes(relative + "\0"));
            hash.AppendData(Encoding.UTF8.GetBytes(sourceText));
            var unit = CSharpSyntaxTree.ParseText(sourceText, path: relative).GetCompilationUnitRoot();
            var errors = unit.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
            if (errors.Length > 0)
                throw new InvalidDataException(relative + ": " + string.Join("; ", errors.Select(e => e.ToString())));
            _units[relative] = unit;
            foreach (var syntax in unit.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
            {
                if (syntax is ClassDeclarationSyntax cls && cls.BaseList?.Types.Any(b => b.Type.ToString().StartsWith("IMessage")) != true)
                    continue;
                if (syntax is not (ClassDeclarationSyntax or EnumDeclarationSyntax))
                    continue;
                if (syntax.Identifier.ValueText.EndsWith("OneofCase") || syntax.Identifier.ValueText == "EMessageId")
                    continue;
                var name = QualifiedName(syntax);
                var flat = name.Replace("Aki.Protocol.", "", StringComparison.Ordinal).Replace('.', '_');
                _types.Add(name, new(name, flat, relative, syntax));
            }
        }

        var enums = _types.Values.Where(t => t.Syntax is EnumDeclarationSyntax).Select(ReadEnum).OrderBy(t => t.Name, StringComparer.Ordinal).ToArray();
        var messages = _types.Values.Where(t => t.Syntax is ClassDeclarationSyntax).Select(ReadMessage).OrderBy(t => t.Name, StringComparer.Ordinal).ToArray();
        var collisions = _types.Values.GroupBy(t => t.FlatName).Where(g => g.Count() > 1).ToArray();
        if (collisions.Length > 0)
            throw new InvalidDataException("Flattened name collision: " + string.Join(",", collisions.Select(g => g.Key)));
        var idEnum = _units.Values.SelectMany(u => u.DescendantNodes().OfType<EnumDeclarationSyntax>()).Single(e => e.Identifier.ValueText == "EMessageId");
        var ids = EnumValues(idEnum).ToDictionary(e => e.Name, e => e.Number, StringComparer.Ordinal);
        var attributes = new SortedDictionary<int, string>();
        foreach (var type in _types.Values.Where(t => t.Syntax is ClassDeclarationSyntax))
        {
            var cls = (ClassDeclarationSyntax)type.Syntax;
            foreach (var attribute in cls.AttributeLists.SelectMany(a => a.Attributes).Where(a => a.Name.ToString() == "NetMessage"))
            {
                var name = attribute.ArgumentList!.Arguments.Single().Expression.ToString().Split('.').Last();
                attributes.Add(ids[name], type.Name);
            }
        }

        var factory = new SortedDictionary<int, string>();
        foreach (var unit in _units.Values)
            foreach (var method in unit.DescendantNodes().OfType<MethodDeclarationSyntax>().Where(m => m.Identifier.ValueText.StartsWith("CreateMessage_", StringComparison.Ordinal)))
            {
                var id = int.Parse(method.Identifier.ValueText[14..], CultureInfo.InvariantCulture);
                var obj = method.DescendantNodes().OfType<ObjectCreationExpressionSyntax>().Single();
                factory.Add(id, Resolve(obj.Type.ToString(), method));
            }

        var methods = _units.Values.SelectMany(u => u.DescendantNodes().OfType<MethodDeclarationSyntax>()).ToArray();
        var entry = methods.Single(m => m.Identifier.ValueText == "CreateMessageById");
        var ranges = new Dictionary<int, string>();
        foreach (var section in entry.DescendantNodes().OfType<SwitchSectionSyntax>())
        {
            var labels = section.Labels.OfType<CaseSwitchLabelSyntax>().ToArray();
            if (labels.Length == 0) continue;
            var call = section.DescendantNodes().OfType<InvocationExpressionSyntax>().SingleOrDefault();
            if (call is null) continue; // 显式 null 分支不注册；下方编号集合校验仍会拒绝任何遗漏的已知协议。
            var methodName = ((MemberAccessExpressionSyntax)call.Expression).Name.Identifier.ValueText;
            foreach (var label in labels) ranges.Add(Number(label.Value), methodName);
        }
        var routed = new SortedDictionary<int, string>();
        foreach (var range in methods.Where(m => m.Identifier.ValueText.StartsWith("CreateRange_", StringComparison.Ordinal)))
        foreach (var section in range.DescendantNodes().OfType<SwitchSectionSyntax>())
        {
            var labels = section.Labels.OfType<CaseSwitchLabelSyntax>().ToArray();
            if (labels.Length == 0) continue;
            var call = section.DescendantNodes().OfType<InvocationExpressionSyntax>().SingleOrDefault();
            if (call is null) continue; // 显式 null 分支不注册；下方编号集合校验仍会拒绝任何遗漏的已知协议。
            var name = call.Expression is MemberAccessExpressionSyntax member ? member.Name.Identifier.ValueText : call.Expression.ToString();
            var target = int.Parse(name[14..], CultureInfo.InvariantCulture);
            foreach (var label in labels)
            {
                var id = Number(label.Value);
                if (!ranges.TryGetValue(id >> 10, out var route) || route != range.Identifier.ValueText)
                    throw new InvalidDataException("Unreachable factory route for " + id);
                routed.Add(id, factory[target]);
            }
        }
        foreach (var range in methods.Where(m => m.Identifier.ValueText.StartsWith("CreateRange_", StringComparison.Ordinal)))
        foreach (var condition in range.DescendantNodes().OfType<IfStatementSyntax>())
        {
            if (condition.Condition is not BinaryExpressionSyntax comparison || comparison.Left.ToString() != "id") continue;
            var branch = comparison.IsKind(SyntaxKind.EqualsExpression) ? condition.Statement
                : comparison.IsKind(SyntaxKind.NotEqualsExpression) ? condition.Else?.Statement : null;
            if (branch is null) continue;
            var call = branch.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>().SingleOrDefault();
            if (call is null) continue;
            var id = Number(comparison.Right);
            var name = call.Expression is MemberAccessExpressionSyntax member ? member.Name.Identifier.ValueText : call.Expression.ToString();
            if (!ranges.TryGetValue(id >> 10, out var route) || route != range.Identifier.ValueText)
                throw new InvalidDataException("Unreachable conditional factory route for " + id);
            routed.Add(id, factory[int.Parse(name[14..], CultureInfo.InvariantCulture)]);
        }
        factory = routed;
        if (ids.Count != attributes.Count || !attributes.SequenceEqual(factory) || !ids.Values.Order().SequenceEqual(factory.Keys))
            throw new InvalidDataException("EMessageId / NetMessage / NetDefine disagree");
        return new(messages, enums, factory, commit, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(), new SortedDictionary<string, int>(ids, StringComparer.Ordinal));
    }

    private static string QualifiedName(SyntaxNode node)
    {
        var names = node.AncestorsAndSelf().OfType<BaseTypeDeclarationSyntax>().Reverse().Select(t => t.Identifier.ValueText);
        var ns = node.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Reverse().Select(n => n.Name.ToString());
        return string.Join(".", ns.Concat(names));
    }

    private string Resolve(string name, SyntaxNode context)
    {
        name = name.Replace("global::", "", StringComparison.Ordinal);
        if (Scalars.TryGetValue(name, out var scalar))
            return scalar;
        if (_types.ContainsKey(name))
            return name;
        foreach (var parent in context.AncestorsAndSelf().OfType<BaseTypeDeclarationSyntax>())
        {
            var candidate = QualifiedName(parent) + "." + name;
            if (_types.ContainsKey(candidate))
                return candidate;
        }

        var ns = context.AncestorsAndSelf().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault()?.Name.ToString();
        while (!string.IsNullOrEmpty(ns))
        {
            if (_types.ContainsKey(ns + "." + name))
                return ns + "." + name;
            var dot = ns.LastIndexOf('.');
            ns = dot < 0 ? null : ns[..dot];
        }

        var unit = (CompilationUnitSyntax)context.SyntaxTree.GetRoot();
        var candidates = unit.Usings.Where(u => u.Alias == null).Select(u => u.Name + "." + name).Where(_types.ContainsKey).Distinct().ToArray();
        if (candidates.Length == 1)
            return candidates[0];
        throw new InvalidDataException($"Unresolved type {name} at {context.GetLocation().GetLineSpan()}");
    }

    private static int Number(ExpressionSyntax expression)
    {
        if (expression is LiteralExpressionSyntax literal)
            return Convert.ToInt32(literal.Token.Value, CultureInfo.InvariantCulture);
        if (expression is PrefixUnaryExpressionSyntax unary && unary.IsKind(SyntaxKind.UnaryMinusExpression))
            return unary.Operand is LiteralExpressionSyntax operand
                ? checked((int)-Convert.ToInt64(operand.Token.Value, CultureInfo.InvariantCulture))
                : checked(-Number(unary.Operand));
        throw new InvalidDataException("Unsupported integer expression: " + expression);
    }

    private static EnumValue[] EnumValues(EnumDeclarationSyntax syntax)
    {
        var result = new List<EnumValue>();
        var current = -1;
        foreach (var member in syntax.Members)
        {
            current = member.EqualsValue is { } eq ? Number(eq.Value) : checked(current + 1);
            var original = member.AttributeLists.SelectMany(a => a.Attributes).FirstOrDefault(a => a.Name.ToString() == "OriginalName")?.ArgumentList?.Arguments.FirstOrDefault()?.Expression as LiteralExpressionSyntax;
            result.Add(new(member.Identifier.ValueText, current, original?.Token.ValueText));
        }

        return result.ToArray();
    }

    private Enumeration ReadEnum(SourceType type) => new(type.Name, type.FlatName, EnumValues((EnumDeclarationSyntax)type.Syntax));
    private Message ReadMessage(SourceType type)
    {
        var cls = (ClassDeclarationSyntax)type.Syntax;
        foreach (var variable in cls.Members.OfType<FieldDeclarationSyntax>().SelectMany(f => f.Declaration.Variables).Where(v => v.Identifier.ValueText.EndsWith("DefaultValue")))
        {
            var value = variable.Initializer?.Value ?? cls.Members.OfType<ConstructorDeclarationSyntax>().Where(c => c.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword))).SelectMany(c => c.DescendantNodes().OfType<AssignmentExpressionSyntax>()).FirstOrDefault(a => a.Left.ToString() == variable.Identifier.ValueText || a.Left.ToString() == cls.Identifier.ValueText + "." + variable.Identifier.ValueText)?.Right;
            var zero = value is null || value is LiteralExpressionSyntax literal && (literal.Token.Value is false || literal.Token.Value is "" || (literal.Token.Value is IConvertible scalar && scalar.ToString(CultureInfo.InvariantCulture) == "0"));
            if (!zero && value is MemberAccessExpressionSyntax member)
            {
                var enumType = Resolve(member.Expression.ToString(), cls);
                zero = _types.TryGetValue(enumType, out var e) && e.Syntax is EnumDeclarationSyntax es && EnumValues(es).Any(v => v.Name == member.Name.Identifier.ValueText && v.Number == 0);
            }

            if (!zero)
                throw new InvalidDataException(type.Name + " unsupported nonzero default: " + variable.Identifier);
        }

        var fields = new List<Field>();
        var properties = cls.Members.OfType<PropertyDeclarationSyntax>().Where(p => p.ExplicitInterfaceSpecifier == null).ToDictionary(p => p.Identifier.ValueText);
        var reads = cls.Members.OfType<MethodDeclarationSyntax>().Where(m => m.Identifier.ValueText == "InternalMergeFrom").ToArray();
        var oneofs = cls.Members.OfType<EnumDeclarationSyntax>().Where(e => e.Identifier.ValueText.EndsWith("OneofCase")).SelectMany(e => EnumValues(e).Where(v => v.Number != 0).Select(v => (v.Number, Name: e.Identifier.ValueText[..^9]))).ToDictionary(x => x.Number, x => x.Name);
        foreach (var declaration in cls.Members.OfType<FieldDeclarationSyntax>())
            foreach (var variable in declaration.Declaration.Variables.Where(v => v.Identifier.ValueText.EndsWith("FieldNumber")))
            {
                var name = variable.Identifier.ValueText[..^11];
                var number = Number(variable.Initializer!.Value);
                if (!properties.TryGetValue(name, out var property))
                    throw new InvalidDataException(type.Name + " missing property " + name);
                var ptype = property.Type;
                bool repeated = false;
                string? mapKey = null;
                if (ptype is GenericNameSyntax generic)
                {
                    if (generic.Identifier.ValueText == "RepeatedField")
                    {
                        repeated = true;
                        ptype = generic.TypeArgumentList.Arguments[0];
                    }
                    else if (generic.Identifier.ValueText == "MapField")
                    {
                        mapKey = Resolve(generic.TypeArgumentList.Arguments[0].ToString(), property);
                        ptype = generic.TypeArgumentList.Arguments[1];
                    }
                    else
                        throw new InvalidDataException("Unsupported generic: " + ptype);
                }

                var resolved = Resolve(ptype.ToString(), property);
                var backing = property.DescendantNodes().OfType<MemberAccessExpressionSyntax>().Where(m => m.Expression is ThisExpressionSyntax && m.Name.Identifier.ValueText.EndsWith('_')).Select(m => m.Name.Identifier.ValueText).FirstOrDefault();
                var codecs = cls.Members.OfType<FieldDeclarationSyntax>().SelectMany(f => f.Declaration.Variables).Where(v => v.Identifier.ValueText.StartsWith(repeated ? "_repeated_" : "_map_", StringComparison.Ordinal) && v.Identifier.ValueText == (repeated ? "_repeated_" : "_map_") + (backing?.TrimEnd('_') ?? char.ToLowerInvariant(name[0]) + name[1..]) + "_codec").ToArray();
                InvocationExpressionSyntax[] calls = [];
                if (repeated || mapKey != null)
                {
                    if (codecs.Length != 1)
                        throw new InvalidDataException(type.Name + " codec ambiguous: " + name);
                    calls = codecs[0].DescendantNodes().OfType<InvocationExpressionSyntax>().Where(c => c.Expression is MemberAccessExpressionSyntax a && a.Expression.ToString() == "FieldCodec").ToArray();
                    if (mapKey != null && calls.Length != 2)
                        throw new InvalidDataException(type.Name + " map codecs: " + name);
                    for (var codecIndex = 0; codecIndex < calls.Length; codecIndex++)
                {
                    var codecType = CodecType(calls[codecIndex], mapKey != null && codecIndex == 0 ? mapKey : resolved);
                    var codecTag = Convert.ToUInt32(((LiteralExpressionSyntax)calls[codecIndex].ArgumentList.Arguments[0].Expression).Token.Value, CultureInfo.InvariantCulture);
                    var wire = (int)(codecTag & 7);
                    if (mapKey != null && (codecTag >> 3) != codecIndex + 1)
                        throw new InvalidDataException(type.Name + " map entry field mismatch: " + name);
                    if (wire != WireType(codecType) && !(repeated && wire == 2 && WireType(codecType) != 2))
                        throw new InvalidDataException(type.Name + " codec wire mismatch: " + name);
                }
                var call = calls[mapKey == null ? 0 : 1];
                    resolved = CodecType(call, resolved);
                    if (mapKey != null)
                        mapKey = CodecType(calls[0], mapKey);
                    if (repeated)
                    {
                        var tag = Convert.ToUInt32(((LiteralExpressionSyntax)calls[0].ArgumentList.Arguments[0].Expression).Token.Value, CultureInfo.InvariantCulture);
                        if (tag >> 3 != number)
                            throw new InvalidDataException(type.Name + " codec tag mismatch " + name);
                    }
                }
                else
                {
                    var scalarReads = reads.SelectMany(m => m.DescendantNodes().OfType<AssignmentExpressionSyntax>()).Where(a => a.Left.ToString() == "this." + name || (backing != null && a.Left.ToString() == "this." + backing)).SelectMany(a => a.Right.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>()).Select(c => (c.Expression as MemberAccessExpressionSyntax)?.Name.Identifier.ValueText).Where(n => n != null && n.StartsWith("Read") && WireScalars.Contains(n[4..])).Distinct().ToArray();
                    if (scalarReads.Length > 1)
                        throw new InvalidDataException(type.Name + " conflicting reads: " + name);
                    if (scalarReads.Length == 1)
                        resolved = scalarReads[0]![4..].ToLowerInvariant();
                    else if (Scalars.Values.Contains(resolved))
                        throw new InvalidDataException(type.Name + " missing encoding evidence: " + name);
                }

                if (mapKey != null)
                {
                    var constructor = codecs[0].DescendantNodes().OfType<ObjectCreationExpressionSyntax>().Single();
                    var tag = Convert.ToUInt32(((LiteralExpressionSyntax)constructor.ArgumentList!.Arguments.Last().Expression).Token.Value, CultureInfo.InvariantCulture);
                    if (tag >> 3 != number || (tag & 7) != 2)
                        throw new InvalidDataException(type.Name + " map tag mismatch: " + name);
                }

                if (!repeated && mapKey == null)
                    ValidateWrite(cls, name, backing, number, resolved, type.Name);
                var oneof = oneofs.GetValueOrDefault(number);
                var optional = oneof == null && properties.ContainsKey("Has" + name);
                bool packed = repeated && calls.Length > 0 && ((Convert.ToUInt32(((LiteralExpressionSyntax)calls[0].ArgumentList.Arguments[0].Expression).Token.Value, CultureInfo.InvariantCulture) & 7) == 2);
                fields.Add(new(number, name, resolved, repeated, mapKey, oneof, optional, packed));
            }

        if (fields.GroupBy(f => f.Number).Any(g => g.Count() > 1))
            throw new InvalidDataException(type.Name + " duplicate field number");
        return new(type.Name, type.FlatName, type.Path, fields.OrderBy(f => f.Number).ToArray());
    }

    private int WireType(string type) => type switch
    {
        "double" or "fixed64" or "sfixed64" => 1,
        "float" or "fixed32" or "sfixed32" => 5,
        "string" or "bytes" => 2,
        _ => _types.TryGetValue(type, out var t) && t.Syntax is ClassDeclarationSyntax ? 2 : 0
    };

    private void ValidateWrite(ClassDeclarationSyntax cls, string name, string? backing, int number, string resolved, string owner)
    {
        var calls = cls.Members.OfType<MethodDeclarationSyntax>().Where(m => m.Identifier.ValueText == "InternalWriteTo").SelectMany(m => m.DescendantNodes().OfType<InvocationExpressionSyntax>()).Where(c => c.Expression is MemberAccessExpressionSyntax access && access.Expression.ToString() == "output" && c.ArgumentList.DescendantNodes().OfType<MemberAccessExpressionSyntax>().Any(a => a.ToString() == "this." + name || (backing != null && a.ToString() == "this." + backing))).ToArray();
        if (calls.Length != 1)
            throw new InvalidDataException(owner + " missing/ambiguous write evidence: " + name);
        var call = calls[0];
        var statement = call.Ancestors().OfType<ExpressionStatementSyntax>().First();
        if (statement.Parent is not BlockSyntax block)
            throw new InvalidDataException(owner + " unsupported writer structure: " + name);
        var index = block.Statements.IndexOf(statement);
        var tagCall = index > 0 ? block.Statements[index - 1].DescendantNodes().OfType<InvocationExpressionSyntax>().SingleOrDefault(c => c.Expression.ToString() == "output.WriteRawTag") : null;
        if (tagCall == null)
            throw new InvalidDataException(owner + " missing tag write: " + name);
        ulong tag = 0;
        var shift = 0;
        foreach (var arg in tagCall.ArgumentList.Arguments)
        {
            tag |= (ulong)(Number(arg.Expression) & 127) << shift;
            shift += 7;
        }

        if ((tag >> 3) != (ulong)number || (int)(tag & 7) != WireType(resolved))
            throw new InvalidDataException(owner + " field/tag disagreement: " + name);
        var method = ((MemberAccessExpressionSyntax)call.Expression).Name.Identifier.ValueText;
        if (WireScalars.Contains(method[5..]) && resolved != method[5..].ToLowerInvariant())
            throw new InvalidDataException(owner + " read/write type disagreement: " + name);
    }

    private static string CodecType(InvocationExpressionSyntax call, string resolved)
    {
        var name = ((MemberAccessExpressionSyntax)call.Expression).Name.Identifier.ValueText;
        return WireScalars.Contains(name[3..]) ? name[3..].ToLowerInvariant() : resolved;
    }
}
