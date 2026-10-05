using FerretSharp.Core.ClrModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace FerretSharp.ModelHost;

/// <summary>
/// A declaration for a name a copied query uses but nobody declares (<c>customerId</c>, <c>request.From</c>), typed from
/// how it is used where possible: compared with <c>k.KundeId</c> → <c>int customerId = 0;</c>; its members compared or
/// passed on → <c>var request = new { From = DateTime.Today };</c>; <c>ids.Contains(k.KundeId)</c> →
/// <c>var ids = new List&lt;int&gt; { };</c>.
/// </summary>
internal static class TypeSuggestion
{
    public static LinqUnknownName For(LinqConsole.Analysis analysis, string name)
    {
        var model = analysis.Compilation.GetSemanticModel(analysis.Tree);
        var uses = analysis.Tree.GetRoot().DescendantNodes().OfType<IdentifierNameSyntax>().Where(n => n.Identifier.Text == name).ToList();
        var position = uses.Count > 0 ? uses[0].SpanStart : 0;
        ITypeSymbol? direct = null;
        var members = new Dictionary<string, ITypeSymbol?>(StringComparer.Ordinal);
        foreach (var use in uses)
        {
            if (use.Parent is MemberAccessExpressionSyntax access && access.Expression == use)
            {
                // ids.Contains(x.Id): a collection of what is looked for.
                if (access.Name.Identifier.Text == "Contains" && access.Parent is InvocationExpressionSyntax { ArgumentList.Arguments: [var argument] }
                    && Known(model.GetTypeInfo(argument.Expression).Type) is { } element)
                {
                    direct ??= model.Compilation.GetTypeByMetadataName("System.Collections.Generic.List`1")?.Construct(element);
                    continue;
                }

                var member = access.Name.Identifier.Text;
                var type = Infer(model, access);
                if (!members.TryGetValue(member, out var known) || known is null)
                {
                    members[member] = type;
                }
            }
            else
            {
                direct ??= Infer(model, use);
            }
        }

        if (direct is not null)
        {
            return new LinqUnknownName(name, $"{direct.ToMinimalDisplayString(model, position)} {name} = {Value(direct, model, position)};", true);
        }

        if (members.Count > 0)
        {
            var parts = members.Select(m => $"{m.Key} = {(m.Value is { } type ? Value(type, model, position) : "default(object)")}");
            return new LinqUnknownName(name, $"var {name} = new {{ {string.Join(", ", parts)} }};", members.Values.All(t => t is not null));
        }

        return new LinqUnknownName(name, $"object? {name} = null; // Typ und Wert eintragen", false);
    }

    /// <summary>The type an expression must have where it is used; null if that is not clear.</summary>
    private static ITypeSymbol? Infer(SemanticModel model, ExpressionSyntax expression)
    {
        var node = expression;
        while (node.Parent is ParenthesizedExpressionSyntax parenthesized)
        {
            node = parenthesized;
        }

        switch (node.Parent)
        {
            case BinaryExpressionSyntax binary:
                return Known(model.GetTypeInfo(binary.Left == node ? binary.Right : binary.Left).Type);
            case AssignmentExpressionSyntax assignment when assignment.Right == node:
                return Known(model.GetTypeInfo(assignment.Left).Type);
            case ArgumentSyntax { Parent: ArgumentListSyntax { Parent: InvocationExpressionSyntax invocation } list } argument:
                var index = list.Arguments.IndexOf(argument);
                var symbol = model.GetSymbolInfo(invocation);
                var method = symbol.Symbol as IMethodSymbol ?? symbol.CandidateSymbols.OfType<IMethodSymbol>().FirstOrDefault();
                return method is not null && index < method.Parameters.Length ? Known(method.Parameters[index].Type) : null;
            default:
                return null;
        }
    }

    private static ITypeSymbol? Known(ITypeSymbol? type) =>
        type is null or IErrorTypeSymbol || type.TypeKind == TypeKind.Error || type.TypeKind == TypeKind.TypeParameter ? null : type;

    /// <summary>A value of the type to start from; the user replaces it.</summary>
    private static string Value(ITypeSymbol type, SemanticModel model, int position)
    {
        if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable)
        {
            return Value(nullable.TypeArguments[0], model, position);
        }

        if (type.TypeKind == TypeKind.Enum)
        {
            var first = type.GetMembers().OfType<IFieldSymbol>().FirstOrDefault(f => f.IsConst);
            return first is null ? "default" : $"{type.ToMinimalDisplayString(model, position)}.{first.Name}";
        }

        return type.SpecialType switch
        {
            SpecialType.System_Int32 or SpecialType.System_Int16 or SpecialType.System_Byte or SpecialType.System_SByte or SpecialType.System_UInt16 => "0",
            SpecialType.System_Int64 => "0L",
            SpecialType.System_UInt32 => "0u",
            SpecialType.System_UInt64 => "0ul",
            SpecialType.System_Decimal => "0m",
            SpecialType.System_Double => "0d",
            SpecialType.System_Single => "0f",
            SpecialType.System_String => "\"\"",
            SpecialType.System_Boolean => "false",
            SpecialType.System_Char => "' '",
            SpecialType.System_DateTime => "DateTime.Today",
            _ => type.ToDisplayString() switch
            {
                "System.DateTimeOffset" => "DateTimeOffset.Now",
                "System.Guid" => "Guid.Empty",
                "System.TimeSpan" => "TimeSpan.Zero",
                "System.DateOnly" => "DateOnly.FromDateTime(DateTime.Today)",
                _ when type is IArrayTypeSymbol array => $"new {array.ElementType.ToMinimalDisplayString(model, position)}[] {{ }}",
                _ when type is INamedTypeSymbol { IsGenericType: true } && type.Name == "List" => $"new {type.ToMinimalDisplayString(model, position)} {{ }}",
                _ => "default!",
            },
        };
    }
}
