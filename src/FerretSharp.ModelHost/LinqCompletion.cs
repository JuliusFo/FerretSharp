using FerretSharp.Core.ClrModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace FerretSharp.ModelHost;

/// <summary>
/// Completion for the LINQ console (WP-19, ADR 0015) from Roslyn's semantic model of the script – without the IDE's
/// completion service (Microsoft.CodeAnalysis.Features would bring more assemblies into the project's process, where
/// Roslyn is held at 4.11 already). After <c>x.</c> the members of x's type with extension methods (LINQ, EF); after
/// <c>Type.</c> its static members or enum members; in <c>new Kunde { … }</c> the properties not set yet; elsewhere the
/// variables, the project's types, common .NET types and C# keywords. Nothing in strings and comments, nothing while
/// naming a new variable.
/// </summary>
internal static class LinqCompletion
{
    private const int MaxItems = 3000;

    /// <summary>.NET types worth offering without a dot (the project's own come from its namespaces).</summary>
    private static readonly HashSet<string> CommonTypes = new(StringComparer.Ordinal)
    {
        "System.DateTime", "System.DateTimeOffset", "System.DateOnly", "System.TimeOnly", "System.TimeSpan", "System.Guid",
        "System.Math", "System.Convert", "System.StringComparison", "System.StringComparer", "System.Array",
        "System.Collections.Generic.List`1", "System.Collections.Generic.Dictionary`2", "System.Collections.Generic.HashSet`1",
        "Microsoft.EntityFrameworkCore.EF",
    };

    private static readonly string[] Keywords =
    [
        "var", "new", "return", "await", "true", "false", "null", "default", "typeof", "nameof", "is", "not", "and", "or", "as",
        "from", "where", "select", "orderby", "ascending", "descending", "group", "by", "into", "join", "on", "equals", "let", "in",
        "if", "else", "foreach", "int", "long", "decimal", "double", "bool", "string", "object", "byte", "char",
    ];

    /// <summary>Signatures as the editor shows them: <c>IQueryable&lt;Kunde&gt; Where(Expression&lt;Func&lt;Kunde, bool&gt;&gt; predicate)</c>.</summary>
    private static readonly SymbolDisplayFormat Signature = new(
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameOnly,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        memberOptions: SymbolDisplayMemberOptions.IncludeParameters | SymbolDisplayMemberOptions.IncludeType,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType | SymbolDisplayParameterOptions.IncludeName
                          | SymbolDisplayParameterOptions.IncludeParamsRefOut | SymbolDisplayParameterOptions.IncludeDefaultValue,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    private static readonly SymbolDisplayFormat TypeName = new(
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameOnly,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    /// <param name="position">The cursor in the script text.</param>
    /// <param name="projectNamespaces">Namespaces of the context, the entities and their property types.</param>
    public static IReadOnlyList<LinqCompletionItem> Items(SemanticModel model, int position, IReadOnlySet<string> projectNamespaces)
    {
        var root = model.SyntaxTree.GetRoot();
        position = Math.Clamp(position, 0, root.FullSpan.End);
        if (InCommentOrString(root, position))
        {
            return [];
        }

        var token = root.FindToken(position);
        if (position <= token.SpanStart)
        {
            token = token.GetPreviousToken();
        }

        // The word being typed (Monaco filters by it) and the token before it, which decides what fits.
        SyntaxToken? word = token.Span.End == position && token.Text.Length > 0 && SyntaxFacts.IsIdentifierStartCharacter(token.Text[0]) ? token : null;
        var previous = word is { } w ? w.GetPreviousToken() : token;
        if (word is { } typed && IsDeclaredName(typed))
        {
            return [];
        }

        if (previous.IsKind(SyntaxKind.DotToken))
        {
            return Limit(MembersAfterDot(model, previous));
        }

        if (ObjectInitializer(model, previous) is { } initializer)
        {
            return Limit(initializer);
        }

        // At the start of the word: its end may lie behind an unfinished lambda (end of text), where its parameters are gone.
        return Limit(Everything(model, word?.SpanStart ?? position, previous, projectNamespaces));
    }

    private static IReadOnlyList<LinqCompletionItem> Limit(IEnumerable<LinqCompletionItem> items) => items.Take(MaxItems).ToList();

    private static bool InCommentOrString(SyntaxNode root, int position)
    {
        if (position > 0)
        {
            var trivia = root.FindTrivia(position - 1, findInsideTrivia: true);
            if (trivia.IsKind(SyntaxKind.SingleLineCommentTrivia) && position <= trivia.Span.End
                || trivia.IsKind(SyntaxKind.MultiLineCommentTrivia) && (position < trivia.Span.End || !trivia.ToString().EndsWith("*/", StringComparison.Ordinal))
                || trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia))
            {
                return true;
            }
        }

        var token = root.FindToken(position);
        if (position <= token.SpanStart)
        {
            token = token.GetPreviousToken();
        }

        // Inside a literal, or right behind an unterminated one ("Newline in constant").
        return token.Kind() is SyntaxKind.StringLiteralToken or SyntaxKind.CharacterLiteralToken or SyntaxKind.InterpolatedStringTextToken
                   or SyntaxKind.SingleLineRawStringLiteralToken or SyntaxKind.MultiLineRawStringLiteralToken or SyntaxKind.Utf8StringLiteralToken
               && position > token.SpanStart && (position < token.Span.End || token.ContainsDiagnostics)
               || token.IsKind(SyntaxKind.InterpolatedStringStartToken) && position >= token.Span.End;
    }

    /// <summary>The name of something being declared: a new name, nothing to complete.</summary>
    private static bool IsDeclaredName(SyntaxToken word) => word.Parent switch
    {
        VariableDeclaratorSyntax d => d.Identifier == word,
        ParameterSyntax p => p.Identifier == word,
        ForEachStatementSyntax f => f.Identifier == word,
        FromClauseSyntax f => f.Identifier == word,
        JoinClauseSyntax j => j.Identifier == word,
        LetClauseSyntax l => l.Identifier == word,
        QueryContinuationSyntax q => q.Identifier == word,
        SingleVariableDesignationSyntax => true,
        CatchDeclarationSyntax c => c.Identifier == word,
        LocalFunctionStatementSyntax f => f.Identifier == word,
        TypeParameterSyntax => true,
        _ => false,
    };

    private static IEnumerable<LinqCompletionItem> MembersAfterDot(SemanticModel model, SyntaxToken dot)
    {
        var expression = dot.Parent switch
        {
            MemberAccessExpressionSyntax access => access.Expression,
            QualifiedNameSyntax qualified => qualified.Left,
            MemberBindingExpressionSyntax binding => binding.FirstAncestorOrSelf<ConditionalAccessExpressionSyntax>()?.Expression,
            _ => null,
        };
        if (expression is null)
        {
            return [];
        }

        var position = dot.Span.End;
        var info = model.GetSymbolInfo(expression);
        var symbol = info.Symbol ?? info.CandidateSymbols.FirstOrDefault();
        if (symbol is IAliasSymbol alias)
        {
            symbol = alias.Target;
        }

        switch (symbol)
        {
            case INamespaceSymbol ns:
                return Describe(model.LookupNamespacesAndTypes(position, ns), null);
            case ITypeSymbol type:
                // Kundenart. – its members only (like Visual Studio; Enum.Parse is not meant this way); DateTime. – static members, nested types.
                return Describe(model.LookupStaticMembers(position, type)
                    .Where(s => type.TypeKind == TypeKind.Enum
                        ? s is IFieldSymbol && SymbolEqualityComparer.Default.Equals(s.ContainingType, type)
                        : s.IsStatic || s is INamedTypeSymbol), type);
        }

        var receiver = model.GetTypeInfo(expression).Type;
        if (receiver is null or { TypeKind: TypeKind.Error })
        {
            // Unfinished code (a lambda in a call that does not bind yet): the symbol still knows its type.
            receiver = symbol switch
            {
                ILocalSymbol local => local.Type,
                IParameterSymbol parameter => parameter.Type,
                IPropertySymbol property => property.Type,
                IFieldSymbol field => field.Type,
                IRangeVariableSymbol => null,
                _ => receiver,
            };
        }

        if (receiver is null or { TypeKind: TypeKind.Error } || receiver.SpecialType == SpecialType.System_Void)
        {
            return [];
        }

        return Describe(model.LookupSymbols(position, receiver, includeReducedExtensionMethods: true)
            .Where(s => !s.IsStatic || s is IMethodSymbol { MethodKind: MethodKind.ReducedExtension }), receiver);
    }

    /// <summary>
    /// <c>new Kunde { |</c> or after a comma in it: the settable properties and fields not assigned yet.
    /// </summary>
    private static IEnumerable<LinqCompletionItem>? ObjectInitializer(SemanticModel model, SyntaxToken previous)
    {
        if (!previous.IsKind(SyntaxKind.OpenBraceToken) && !previous.IsKind(SyntaxKind.CommaToken)
            || previous.Parent is not InitializerExpressionSyntax initializer
            || initializer.Parent is not BaseObjectCreationExpressionSyntax creation
            || model.GetTypeInfo(creation).Type is not INamedTypeSymbol type || type.TypeKind == TypeKind.Error
            || type.AllInterfaces.Any(i => i.SpecialType == SpecialType.System_Collections_IEnumerable) && type.SpecialType != SpecialType.System_String)
        {
            return null;
        }

        var assigned = initializer.Expressions.OfType<AssignmentExpressionSyntax>()
            .Select(a => a.Left).OfType<IdentifierNameSyntax>().Select(n => n.Identifier.Text).ToHashSet(StringComparer.Ordinal);
        var position = previous.Span.End;
        var members = model.LookupSymbols(position, type)
            .Where(s => !s.IsStatic && !assigned.Contains(s.Name) && s switch
            {
                IPropertySymbol p => !p.IsIndexer && p.SetMethod is { } setter && model.IsAccessible(position, setter),
                IFieldSymbol f => !f.IsReadOnly && !f.IsConst,
                _ => false,
            });
        return Describe(members, type);
    }

    /// <summary>No dot: variables, the project's types and common .NET types, then keywords (after <c>new</c> only types).</summary>
    private static IEnumerable<LinqCompletionItem> Everything(SemanticModel model, int position, SyntaxToken previous, IReadOnlySet<string> projectNamespaces)
    {
        var afterNew = previous.IsKind(SyntaxKind.NewKeyword);
        var symbols = model.LookupSymbols(position).Where(s => s switch
        {
            ILocalSymbol or IParameterSymbol or IRangeVariableSymbol => !afterNew,
            // Top-level declarations of the script are members of its class.
            IFieldSymbol or IPropertySymbol or IMethodSymbol => !afterNew && s.ContainingType is { IsScriptClass: true },
            INamedTypeSymbol type => type.ContainingNamespace is { } ns && projectNamespaces.Contains(ns.ToDisplayString())
                                     || CommonTypes.Contains(MetadataName(type)),
            _ => false,
        });
        var items = Describe(symbols, null);
        return afterNew ? items : items.Concat(Keywords.Select(k => new LinqCompletionItem(k, k, LinqProtocol.Keyword, null, 4)));
    }

    private static string MetadataName(INamedTypeSymbol type) =>
        type.ContainingNamespace is { IsGlobalNamespace: false } ns ? $"{ns.ToDisplayString()}.{type.MetadataName}" : type.MetadataName;

    /// <summary>Items for symbols: hidden and generated ones left out, overloads as one item with their number.</summary>
    /// <param name="receiver">The type before the dot (ranks its own members first); null without one.</param>
    private static IEnumerable<LinqCompletionItem> Describe(IEnumerable<ISymbol> symbols, ITypeSymbol? receiver)
    {
        var shown = symbols.Where(s => s.CanBeReferencedByName && !s.IsImplicitlyDeclared && !s.Name.StartsWith("__", StringComparison.Ordinal)
                                       && !IsHidden(s) && IsListed(s));
        foreach (var group in shown.GroupBy(s => s.Name, StringComparer.Ordinal))
        {
            // The overload C# would pick for a query: Queryable.Where (Expression<…>) rather than Enumerable.Where (Func<…>).
            var first = group.OrderBy(s => s is IMethodSymbol { ReducedFrom.ContainingType: { Name: "Enumerable", ContainingNamespace.Name: "Linq" } } ? 1 : 0)
                .ThenBy(s => s is IMethodSymbol m ? m.Parameters.Length : 0)
                .First();
            var overloads = group.Count() - 1;
            var detail = Detail(first) + (overloads > 0 ? $" (+{overloads})" : "");
            yield return new LinqCompletionItem(Label(first), Insert(first.Name), Kind(first), detail, Rank(first, receiver));
        }
    }

    private static bool IsListed(ISymbol symbol) => symbol switch
    {
        IMethodSymbol m => m.MethodKind is MethodKind.Ordinary or MethodKind.ReducedExtension or MethodKind.LocalFunction,
        IPropertySymbol p => !p.IsIndexer,
        IFieldSymbol or IEventSymbol or INamedTypeSymbol or INamespaceSymbol or ILocalSymbol or IParameterSymbol or IRangeVariableSymbol => true,
        _ => false,
    };

    /// <summary>[EditorBrowsable(Never)] members – hidden in Visual Studio too.</summary>
    private static bool IsHidden(ISymbol symbol)
    {
        var target = symbol is IMethodSymbol { ReducedFrom: { } reduced } ? reduced : symbol;
        return target.GetAttributes().Any(a => a.AttributeClass?.Name == "EditorBrowsableAttribute"
                                               && a.ConstructorArguments is [{ Value: 1 }]);
    }

    private static string Label(ISymbol symbol) =>
        symbol is INamedTypeSymbol { IsGenericType: true } type ? type.ToDisplayString(TypeName) : symbol.Name;

    private static string Insert(string name) => SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None ? "@" + name : name;

    private static string Kind(ISymbol symbol) => symbol switch
    {
        IPropertySymbol => LinqProtocol.Property,
        IFieldSymbol { ContainingType.TypeKind: TypeKind.Enum } => LinqProtocol.EnumMember,
        IFieldSymbol f => f.ContainingType is { IsScriptClass: true } ? LinqProtocol.Variable : LinqProtocol.Field,
        IMethodSymbol { MethodKind: MethodKind.ReducedExtension } => LinqProtocol.ExtensionMethod,
        IMethodSymbol => LinqProtocol.Method,
        IEventSymbol => LinqProtocol.Event,
        INamespaceSymbol => LinqProtocol.Namespace,
        INamedTypeSymbol { TypeKind: TypeKind.Enum } => LinqProtocol.Enum,
        INamedTypeSymbol { TypeKind: TypeKind.Interface } => LinqProtocol.Interface,
        INamedTypeSymbol { TypeKind: TypeKind.Struct } => LinqProtocol.Struct,
        INamedTypeSymbol => LinqProtocol.Class,
        _ => LinqProtocol.Variable,
    };

    private static string? Detail(ISymbol symbol)
    {
        // Names only, also of types the script does not import (Expression<…>): a hint, not code to paste.
        string Type(ITypeSymbol type) => type.ToDisplayString(TypeName);

        return symbol switch
        {
            IFieldSymbol { ContainingType.TypeKind: TypeKind.Enum, HasConstantValue: true } member => $"{member.ContainingType.Name} = {member.ConstantValue}",
            IFieldSymbol field => Type(field.Type),
            IPropertySymbol property => Type(property.Type),
            ILocalSymbol local => Type(local.Type),
            IParameterSymbol parameter => Type(parameter.Type),
            IEventSymbol e => Type(e.Type),
            IMethodSymbol method => method.ToDisplayString(Signature),
            INamedTypeSymbol type => type.ContainingNamespace is { IsGlobalNamespace: false } ns ? ns.ToDisplayString() : null,
            _ => null,
        };
    }

    /// <summary>
    /// The type's own members first (a DbSet's or entity's properties), then inherited ones, extension methods, and
    /// last what every object has (<c>ToString</c>, <c>Equals</c> …). Without a receiver: variables before types.
    /// </summary>
    private static int Rank(ISymbol symbol, ITypeSymbol? receiver)
    {
        if (receiver is null)
        {
            return symbol is INamedTypeSymbol ? 2 : 0;
        }

        if (symbol.ContainingType?.SpecialType == SpecialType.System_Object)
        {
            return 3;
        }

        if (symbol is IMethodSymbol { MethodKind: MethodKind.ReducedExtension })
        {
            return 2;
        }

        var own = SymbolEqualityComparer.Default.Equals(symbol.ContainingType?.OriginalDefinition, receiver.OriginalDefinition);
        return own && symbol is not IMethodSymbol ? 0 : 1;
    }
}
