using System.Text.RegularExpressions;
using FerretSharp.Core.Resources;
using FerretSharp.Core.Schema;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace FerretSharp.Core.ClrModel;

/// <summary>
/// The C# changes for a designer change of a mapped table (WP-41, first stage, decisions of the user): a new column gets a
/// property behind the entity's last one and – if it says more than the name – a configuration line; a changed length,
/// precision, scale or NULL adjusts what the project configures (<c>HasMaxLength</c>, <c>HasPrecision</c>,
/// <c>HasColumnType</c>, <c>IsRequired</c>, the attributes, <c>?</c> at the type) and adds nothing that is not there; a
/// dropped column takes its property and its configuration lines with it. Renames and type changes are only named.
/// </summary>
public static partial class EntityCodeChanges
{
    /// <summary>The identifiers a scan must find files by: the entity's name and the names its configuration uses.</summary>
    public static IReadOnlyList<string> Words(EntityExport entity) => [ClrModelMapping.ShortName(entity.ClrType), "IEntityTypeConfiguration", "Entity"];

    public static CodeChangePlan Plan(ClrModelMapping mapping, TableRef table, IReadOnlyList<ColumnChange> changes, CodeScan scan)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentNullException.ThrowIfNull(changes);
        ArgumentNullException.ThrowIfNull(scan);
        var notes = new List<string>();
        var problems = new List<string>();
        if (mapping.EntityOf(table) is not { } entityMapping)
        {
            return new CodeChangePlan(scan.Root, [], [], [CodeEditText.Problem_NoEntity]);
        }

        var entity = entityMapping.Entity;
        var name = ClrModelMapping.ShortName(entity.ClrType);
        var types = scan.TypesNamed(entity.ClrType);
        if (types.Count == 0)
        {
            return new CodeChangePlan(scan.Root, [], [], [TextFormat.Format(CodeEditText.Problem_ClassNotFound, entity.ClrType)]);
        }

        foreach (var unreadable in scan.Unreadable)
        {
            notes.Add(TextFormat.Format(CodeEditText.Note_Unreadable, unreadable));
        }

        var sites = scan.ConfigurationsOf(entity.ClrType)
            .OrderBy(s => s.Kind) // a configuration class before a lambda in OnModelCreating before chains there
            .ToList();
        var context = new Context(scan, entityMapping, types, sites, new FileEdits(scan.Root), notes, NullableReferences(scan));
        var added = new List<string>();
        foreach (var change in changes)
        {
            switch (change.Kind)
            {
                case ColumnChangeKind.Added:
                    Add(context, mapping, change.After!, added);
                    break;
                case ColumnChangeKind.Facets:
                    Adjust(context, mapping.PropertyOf(table, change.Column), change.Before!, change.After!);
                    break;
                case ColumnChangeKind.Dropped:
                    Drop(context, mapping.PropertyOf(table, change.Column), change.Column);
                    break;
                case ColumnChangeKind.Renamed when mapping.PropertyOf(table, change.Column) is { } renamed:
                    notes.Add(TextFormat.Format(CodeEditText.Note_Renamed, change.Column, change.After!.Name, renamed.Name));
                    break;
                case ColumnChangeKind.TypeChanged when mapping.PropertyOf(table, change.Column) is { } retyped:
                    notes.Add(TextFormat.Format(CodeEditText.Note_TypeChanged, change.Column, change.After!.DisplayType, retyped.Name, retyped.ClrType));
                    break;
            }
        }

        var others = mapping.Entities.Where(e => e.Table.Ref == table && e.Entity != entity).Select(e => ClrModelMapping.ShortName(e.Entity.ClrType)).Distinct().ToList();
        if (others.Count > 0)
        {
            notes.Add(TextFormat.Format(CodeEditText.Note_OtherEntities, string.Join(", ", others), name));
        }

        var (files, built) = context.Edits.Build();
        problems.AddRange(built);
        return new CodeChangePlan(scan.Root, files, notes, problems);
    }

    /// <summary>What the steps of a plan share.</summary>
    private sealed record Context(
        CodeScan Scan, EntityMapping Entity, IReadOnlyList<TypeSite> Types, IReadOnlyList<ConfigSite> Sites, FileEdits Edits, List<string> Notes,
        bool NullableReferences)
    {
        public string Name => ClrModelMapping.ShortName(Entity.Entity.ClrType);

        /// <summary>The property's declarations in the entity's parts (none if a base class declares it).</summary>
        public IEnumerable<(TypeSite Type, PropertyDeclarationSyntax Declaration)> Declarations(string property) =>
            Types.SelectMany(t => t.Declaration.Members.OfType<PropertyDeclarationSyntax>().Where(p => p.Identifier.ValueText == property).Select(p => (t, p)));

        /// <summary>The configuration statements that start with <c>Property(x =&gt; x.Name)</c> of the property.</summary>
        public IEnumerable<(ConfigSite Site, ExpressionStatementSyntax Statement, IReadOnlyList<InvocationExpressionSyntax> Chain)> Configured(string property) =>
            Sites.SelectMany(site => site.Statements.Select(s => (site, s, Chain(s.Expression, site.Prefix)))
                .Where(x => x.Item3 is [var first, ..] && PropertyOf(first, site.Prefix) == property));
    }

    private static void Add(Context context, ClrModelMapping mapping, ColumnInfo column, List<string> added)
    {
        if (mapping.PropertyOf(context.Entity.Table.Ref, column.Name) is not null)
        {
            return; // mapped already: the model was reloaded after the change was written
        }

        var suggestion = EntityCode.PropertyFor(mapping, context.Entity, column, added);
        if (context.Declarations(PropertyCode.Suggest(mapping.Model, column).Name).Any())
        {
            // in the class, not yet in the model: written before the build (a second "Write" must not add it again)
            context.Notes.Add(TextFormat.Format(CodeEditText.Note_AlreadyDeclared, PropertyCode.Suggest(mapping.Model, column).Name, context.Name));
            return;
        }

        added.Add(suggestion.Name);
        foreach (var note in suggestion.Notes)
        {
            context.Notes.Add($"{suggestion.Name}: {note}");
        }

        // The part with most properties – the class itself, not a partial part with methods.
        var target = context.Types.OrderByDescending(t => t.Declaration.Members.OfType<PropertyDeclarationSyntax>().Count()).First();
        var file = target.File;
        var scalar = context.Entity.Entity.Properties.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        var properties = target.Declaration.Members.OfType<PropertyDeclarationSyntax>().ToList();
        var after = properties.LastOrDefault(p => scalar.Contains(p.Identifier.ValueText)) ?? properties.LastOrDefault();
        var declaration = context.NullableReferences ? suggestion.Declaration : ReferenceNullable().Replace(suggestion.Declaration, "$1 ");
        var format = file.File.Format;
        if (after is not null)
        {
            var indent = FileEdits.IndentAt(file.File.Text, after.SpanStart);
            context.Edits.Insert(file, FileEdits.LineEnd(file.File.Text, after.Span.End), indent + declaration + format.NewLine);
        }
        else
        {
            var open = target.Declaration.OpenBraceToken;
            var indent = FileEdits.IndentAt(file.File.Text, target.Declaration.SpanStart) + format.Indent;
            context.Edits.Insert(file, FileEdits.LineEnd(file.File.Text, open.Span.End), indent + declaration + format.NewLine);
        }

        if (EntityCode.IsBare(suggestion.Configuration))
        {
            return;
        }

        if (context.Sites.FirstOrDefault() is not { } site)
        {
            context.Notes.Add(TextFormat.Format(CodeEditText.Note_NoConfiguration, context.Name, suggestion.Configuration));
            return;
        }

        var parameter = site.Statements.Select(s => Chain(s.Expression, site.Prefix)).Where(c => c is [_, ..]).Select(c => LambdaParameter(c[0], site.Prefix)).FirstOrDefault(p => p is not null) ?? "x";
        var line = suggestion.Configuration.Replace($"builder.Property(x => x.{suggestion.Name})", $"{site.Prefix}.Property({parameter} => {parameter}.{suggestion.Name})", StringComparison.Ordinal);
        var anchor = site.Statements.LastOrDefault(s => Chain(s.Expression, site.Prefix) is [var first, ..] && IsProperty(first, site.Prefix)) ?? site.Statements.LastOrDefault();
        var siteText = site.File.File.Text;
        if (anchor is not null)
        {
            context.Edits.Insert(site.File, FileEdits.LineEnd(siteText, anchor.Span.End), FileEdits.IndentAt(siteText, anchor.SpanStart) + line + site.File.File.Format.NewLine);
        }
        else
        {
            var indent = FileEdits.IndentAt(siteText, site.Block.SpanStart) + site.File.File.Format.Indent;
            context.Edits.Insert(site.File, FileEdits.LineEnd(siteText, site.Block.OpenBraceToken.Span.End), indent + line + site.File.File.Format.NewLine);
        }
    }

    private static void Adjust(Context context, PropertyExport? property, ColumnInfo before, ColumnInfo after)
    {
        if (property is null)
        {
            return; // no property maps the column: nothing in the code describes it
        }

        var lengthTouched = false;
        var precisionTouched = false;
        var reference = property.ClrType == "string" || property.ClrType.EndsWith("[]", StringComparison.Ordinal);
        foreach (var (type, declaration) in context.Declarations(property.Name))
        {
            var file = type.File;
            if (before.Nullable != after.Nullable)
            {
                Nullability(context, file, type, declaration, after.Nullable, reference);
            }

            foreach (var attribute in declaration.AttributeLists.SelectMany(l => l.Attributes))
            {
                switch (AttributeName(attribute))
                {
                    case "MaxLength" or "StringLength" when after.Length is { } length && attribute.ArgumentList?.Arguments is { Count: > 0 } lengthArgs && lengthArgs[0].NameEquals is null:
                        lengthTouched |= Number(context, file, lengthArgs[0].Expression, length, before.Length != after.Length);
                        break;
                    case "Precision" when after.Precision is { } precision && attribute.ArgumentList?.Arguments is { Count: > 0 } precisionArgs:
                        precisionTouched |= Number(context, file, precisionArgs[0].Expression, precision, before.Precision != after.Precision);
                        if (precisionArgs.Count > 1)
                        {
                            Number(context, file, precisionArgs[1].Expression, after.Scale ?? 0, before.Scale != after.Scale);
                        }

                        break;
                    case "Column" when attribute.ArgumentList?.Arguments.FirstOrDefault(a => a.NameEquals?.Name.Identifier.ValueText == "TypeName") is { } typeName:
                        var touched = ColumnType(context, file, typeName.Expression, before, after);
                        (lengthTouched, precisionTouched) = (lengthTouched || touched, precisionTouched || touched);
                        break;
                    case "Required" when after.Nullable:
                        RemoveAttribute(context, file, attribute);
                        break;
                }
            }
        }

        foreach (var (site, _, chain) in context.Configured(property.Name))
        {
            foreach (var call in chain.Skip(1))
            {
                var args = call.ArgumentList.Arguments;
                switch (MethodName(call))
                {
                    case "HasMaxLength" when after.Length is { } length && args is [var first]:
                        lengthTouched |= Number(context, site.File, first.Expression, length, before.Length != after.Length);
                        break;
                    case "HasPrecision" when after.Precision is { } precision && args.Count > 0:
                        precisionTouched |= Number(context, site.File, args[0].Expression, precision, before.Precision != after.Precision);
                        if (args.Count > 1)
                        {
                            Number(context, site.File, args[1].Expression, after.Scale ?? 0, before.Scale != after.Scale);
                        }

                        break;
                    case "HasColumnType" when args is [var type]:
                        var touched = ColumnType(context, site.File, type.Expression, before, after);
                        (lengthTouched, precisionTouched) = (lengthTouched || touched, precisionTouched || touched);
                        break;
                    case "IsRequired" when before.Nullable != after.Nullable && Requires(args) is { } required && required == after.Nullable:
                        RemoveCall(context, site.File, call); // IsRequired() on a nullable column, IsRequired(false) on a NOT NULL one
                        break;
                }
            }
        }

        if (before.Length != after.Length && OracleTypes.HasLength(after.DataType) && !lengthTouched)
        {
            context.Notes.Add(TextFormat.Format(CodeEditText.Note_LengthNotConfigured, property.Name, after.DisplayType));
        }

        if ((before.Precision != after.Precision || before.Scale != after.Scale) && !precisionTouched && property.ClrType == "decimal")
        {
            context.Notes.Add(TextFormat.Format(CodeEditText.Note_PrecisionNotConfigured, property.Name, after.DisplayType));
        }

        // NUMBER(9) → NUMBER(12): an int no longer holds every value (the type follows how EF reads the column).
        if (PropertyCode.DefaultType(before) is var was && PropertyCode.DefaultType(after) is var now && was != now && property.ClrType == was)
        {
            context.Notes.Add(TextFormat.Format(CodeEditText.Note_ClrTypeTooSmall, property.Name, property.ClrType, after.DisplayType, now));
        }
    }

    private static void Drop(Context context, PropertyExport? property, string column)
    {
        if (property is null)
        {
            return;
        }

        var declarations = context.Declarations(property.Name).ToList();
        foreach (var (type, declaration) in declarations)
        {
            context.Edits.RemoveLines(type.File, declaration);
        }

        if (declarations.Count == 0 && !property.IsShadow)
        {
            context.Notes.Add(TextFormat.Format(CodeEditText.Note_DeclaredElsewhere, property.Name, context.Name));
        }

        var removed = new HashSet<ExpressionStatementSyntax>();
        foreach (var (site, statement, _) in context.Configured(property.Name))
        {
            context.Edits.RemoveLines(site.File, statement);
            removed.Add(statement);
        }

        // Keys, indexes and relationships naming the property stay: they need a decision (the property's lines are gone).
        foreach (var site in context.Sites)
        {
            foreach (var statement in site.Statements.Where(s => !removed.Contains(s) && Mentions(s, property.Name)))
            {
                context.Notes.Add(TextFormat.Format(CodeEditText.Note_StillNamed, property.Name, site.File.Relative, site.File.LineOf(statement.SpanStart), statement.ToString().ReplaceLineEndings(" ")));
            }
        }

        if (context.Entity.Entity.PrimaryKey.Contains(property.Name, StringComparer.Ordinal))
        {
            context.Notes.Add(TextFormat.Format(CodeEditText.Note_KeyDropped, property.Name, column));
        }

        foreach (var key in context.Entity.Entity.ForeignKeys.Where(k => k.Properties.Contains(property.Name, StringComparer.Ordinal) && k.Navigation is not null))
        {
            context.Notes.Add(TextFormat.Format(CodeEditText.Note_NavigationLeft, key.Navigation!, property.Name));
        }
    }

    /// <summary>Adds or removes the <c>?</c> of the property's type; a reference type only where the project annotates them.</summary>
    private static void Nullability(Context context, ScannedFile file, TypeSite type, PropertyDeclarationSyntax declaration, bool nullable, bool reference)
    {
        if (nullable && declaration.Type is not NullableTypeSyntax && (!reference || context.NullableReferences))
        {
            context.Edits.Insert(file, declaration.Type.Span.End, "?");
        }
        else if (!nullable && declaration.Type is NullableTypeSyntax nullableType)
        {
            context.Edits.Remove(file, nullableType.QuestionToken.Span);
            if (reference && context.NullableReferences && declaration.Initializer is null && declaration.AccessorList is { } accessors)
            {
                // never null now: initialized as the class does it with its other properties of the type
                var element = nullableType.ElementType.ToString();
                if ((Initializer(type, element) ?? (element == "string" ? "\"\"" : null)) is { } value)
                {
                    context.Edits.Insert(file, accessors.Span.End, $" = {value};");
                }
            }
        }
    }

    /// <summary>The initializer most properties of the type <paramref name="type"/> in the class have (<c>""</c>, <c>string.Empty</c>, <c>null!</c>).</summary>
    private static string? Initializer(TypeSite site, string type) =>
        site.Declaration.Members.OfType<PropertyDeclarationSyntax>()
            .Where(p => p.Type.ToString() == type && p.Initializer is not null)
            .GroupBy(p => p.Initializer!.Value.ToString())
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .FirstOrDefault();

    /// <summary>Replaces a number literal; anything else (a constant) is named for the user.</summary>
    /// <returns>Whether the project configures this facet here.</returns>
    private static bool Number(Context context, ScannedFile file, ExpressionSyntax expression, int value, bool changed)
    {
        if (!changed)
        {
            return true;
        }

        if (expression is LiteralExpressionSyntax { RawKind: (int)SyntaxKind.NumericLiteralExpression } or PrefixUnaryExpressionSyntax { Operand: LiteralExpressionSyntax })
        {
            context.Edits.Replace(file, expression.Span, value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        else
        {
            context.Notes.Add(TextFormat.Format(CodeEditText.Note_NotALiteral, expression.ToString(), file.Relative, file.LineOf(expression.SpanStart), value));
        }

        return true;
    }

    /// <summary>
    /// The numbers in a store type like <c>"VARCHAR2(50 CHAR)"</c> or <c>"NUMBER(12,2)"</c>: replaced in place, the rest of the
    /// text as the project wrote it.
    /// </summary>
    /// <returns>Whether the text names a size.</returns>
    private static bool ColumnType(Context context, ScannedFile file, ExpressionSyntax expression, ColumnInfo before, ColumnInfo after)
    {
        if (expression is not LiteralExpressionSyntax { RawKind: (int)SyntaxKind.StringLiteralExpression } literal || literal.Token.Text.StartsWith('@'))
        {
            return false;
        }

        var text = literal.Token.ValueText;
        if (StoreType().Match(text) is not { Success: true } match)
        {
            return false;
        }

        var sized = OracleTypes.HasLength(after.DataType);
        var first = sized ? after.Length : after.Precision;
        if (first is null)
        {
            context.Notes.Add(TextFormat.Format(CodeEditText.Note_NotALiteral, text, file.Relative, file.LineOf(literal.SpanStart), after.DisplayType));
            return true;
        }

        var result = text[..match.Groups["a"].Index] + first.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var at = match.Groups["a"].Index + match.Groups["a"].Length;
        if (!sized && match.Groups["b"].Success)
        {
            result += text[at..match.Groups["b"].Index] + (after.Scale ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture);
            at = match.Groups["b"].Index + match.Groups["b"].Length;
        }
        else if (!sized && after.Scale is > 0)
        {
            result += $",{after.Scale.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
        }

        if (sized && match.Groups["sem"].Success && OracleTypes.HasLengthSemantics(after.DataType))
        {
            var word = after.CharSemantics ? "CHAR" : "BYTE";
            var original = match.Groups["sem"].Value;
            result += text[at..match.Groups["sem"].Index] + (char.IsLower(original[0]) ? word.ToLowerInvariant() : word);
            at = match.Groups["sem"].Index + match.Groups["sem"].Length;
        }

        result += text[at..];
        if (result != text && (ColumnChanges.FacetsDiffer(before, after) || before.CharSemantics != after.CharSemantics))
        {
            context.Edits.Replace(file, literal.Span, CSharpCode.String(result));
        }

        return true;
    }

    /// <summary><c>IsRequired()</c>/<c>IsRequired(true)</c> → true, <c>IsRequired(false)</c> → false; null for anything else.</summary>
    private static bool? Requires(SeparatedSyntaxList<ArgumentSyntax> args) => args switch
    {
        [] => true,
        [{ Expression: var e }] when e.IsKind(SyntaxKind.TrueLiteralExpression) => true,
        [{ Expression: var e }] when e.IsKind(SyntaxKind.FalseLiteralExpression) => false,
        _ => null,
    };

    private static void RemoveCall(Context context, ScannedFile file, InvocationExpressionSyntax call)
    {
        if (call.Expression is MemberAccessExpressionSyntax access)
        {
            context.Edits.Remove(file, TextSpan.FromBounds(access.Expression.Span.End, call.Span.End));
        }
    }

    private static void RemoveAttribute(Context context, ScannedFile file, AttributeSyntax attribute)
    {
        if (attribute.Parent is not AttributeListSyntax list)
        {
            return;
        }

        if (list.Attributes.Count == 1)
        {
            var text = file.File.Text;
            var lineStart = FileEdits.LineStart(text, list.SpanStart);
            var alone = text[lineStart..list.SpanStart].Trim().Length == 0 && text[list.Span.End..FileEdits.LineEnd(text, list.Span.End)].Trim().Length == 0;
            if (alone)
            {
                context.Edits.Remove(file, TextSpan.FromBounds(lineStart, FileEdits.LineEnd(text, list.Span.End)));
            }
            else
            {
                var end = list.Span.End;
                while (end < text.Length && text[end] == ' ')
                {
                    end++;
                }

                context.Edits.Remove(file, TextSpan.FromBounds(list.SpanStart, end));
            }

            return;
        }

        var index = list.Attributes.IndexOf(attribute);
        var separators = list.Attributes.GetSeparators().ToList();
        var span = index < separators.Count
            ? TextSpan.FromBounds(attribute.SpanStart, list.Attributes[index + 1].SpanStart) // "Required, " before the next
            : TextSpan.FromBounds(separators[index - 1].SpanStart, attribute.Span.End); // ", Required" at the end
        context.Edits.Remove(file, span);
    }

    /// <summary>
    /// The calls of a chain from the first one on <paramref name="prefix"/>: <c>builder.Property(…)</c>,
    /// <c>.HasMaxLength(…)</c> … – for <c>modelBuilder.Entity&lt;T&gt;().Property(…)</c> from <c>Property</c> on.
    /// </summary>
    internal static IReadOnlyList<InvocationExpressionSyntax> Chain(ExpressionSyntax expression, string prefix)
    {
        var calls = new List<InvocationExpressionSyntax>();
        var current = expression;
        while (current is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax access } invocation)
        {
            calls.Insert(0, invocation);
            if (access.Expression.ToString() == prefix)
            {
                return calls;
            }

            current = access.Expression;
        }

        return [];
    }

    private static string? MethodName(InvocationExpressionSyntax call) =>
        call.Expression is MemberAccessExpressionSyntax { Name: var name } ? name.Identifier.ValueText : null;

    /// <summary><c>{prefix}.Property(…)</c> (also <c>Property&lt;T&gt;("Name")</c>).</summary>
    private static bool IsProperty(InvocationExpressionSyntax call, string prefix) =>
        call.Expression is MemberAccessExpressionSyntax access && access.Name.Identifier.ValueText == "Property" && access.Expression.ToString() == prefix;

    /// <summary>The property a <c>Property(x =&gt; x.Name)</c>, <c>Property("Name")</c> or <c>Property(nameof(…Name))</c> names.</summary>
    private static string? PropertyOf(InvocationExpressionSyntax call, string prefix)
    {
        if (!IsProperty(call, prefix) || call.ArgumentList.Arguments is not [var argument])
        {
            return null;
        }

        return argument.Expression switch
        {
            SimpleLambdaExpressionSyntax { Body: MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax target, Name: var member } } lambda
                when target.Identifier.ValueText == lambda.Parameter.Identifier.ValueText => member.Identifier.ValueText,
            LiteralExpressionSyntax { RawKind: (int)SyntaxKind.StringLiteralExpression } literal => literal.Token.ValueText,
            InvocationExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.ValueText: "nameof" }, ArgumentList.Arguments: [var named] } =>
                named.Expression switch
                {
                    MemberAccessExpressionSyntax m => m.Name.Identifier.ValueText,
                    IdentifierNameSyntax i => i.Identifier.ValueText,
                    _ => null,
                },
            _ => null,
        };
    }

    /// <summary>The lambda parameter of <c>Property(a =&gt; a.Name)</c>: new lines are written the same way.</summary>
    private static string? LambdaParameter(InvocationExpressionSyntax call, string prefix) =>
        IsProperty(call, prefix) && call.ArgumentList.Arguments is [{ Expression: SimpleLambdaExpressionSyntax lambda }] ? lambda.Parameter.Identifier.ValueText : null;

    /// <summary>The statement names the property as a member (<c>x.Name</c>) or in <c>nameof</c>/a string.</summary>
    private static bool Mentions(ExpressionStatementSyntax statement, string property) =>
        statement.DescendantNodes().Any(n => n switch
        {
            MemberAccessExpressionSyntax { Name.Identifier.ValueText: var member } => member == property,
            AnonymousObjectMemberDeclaratorSyntax { NameEquals: null, Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: var member } } => member == property,
            LiteralExpressionSyntax { RawKind: (int)SyntaxKind.StringLiteralExpression } literal => literal.Token.ValueText == property,
            _ => false,
        });

    private static string AttributeName(AttributeSyntax attribute)
    {
        var name = attribute.Name switch
        {
            QualifiedNameSyntax q => q.Right.Identifier.ValueText,
            SimpleNameSyntax s => s.Identifier.ValueText,
            _ => attribute.Name.ToString(),
        };
        return name.EndsWith("Attribute", StringComparison.Ordinal) ? name[..^"Attribute".Length] : name;
    }

    /// <summary>
    /// The project annotates reference types (<c>string?</c>): a property declared so in one of the scanned files. Without, a
    /// <c>?</c> on a reference type would be a warning (CS8632) in a project without nullable reference types.
    /// </summary>
    internal static bool NullableReferences(CodeScan scan) =>
        scan.Files.Any(f => f.Root.DescendantNodes().OfType<PropertyDeclarationSyntax>()
            .Any(p => p.Type is NullableTypeSyntax { ElementType: PredefinedTypeSyntax { Keyword.RawKind: (int)SyntaxKind.StringKeyword } or ArrayTypeSyntax }));

    /// <summary><c>string?</c> or <c>byte[]?</c> before the name of a declaration.</summary>
    [GeneratedRegex(@"\b(string|byte\[\])\? ")]
    internal static partial Regex ReferenceNullable();

    /// <summary><c>VARCHAR2(50 CHAR)</c>, <c>NUMBER(12, 2)</c>, <c>nvarchar2(20)</c>: the type, its numbers and the semantics.</summary>
    [GeneratedRegex(@"^\s*[A-Za-z_][A-Za-z0-9_ ]*?\s*\(\s*(?<a>\d+)\s*(?:,\s*(?<b>-?\d+)\s*)?(?:\s+(?<sem>CHAR|BYTE))?\s*\)", RegexOptions.IgnoreCase)]
    private static partial Regex StoreType();
}
