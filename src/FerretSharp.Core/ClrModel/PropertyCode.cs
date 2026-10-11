using System.Text;
using System.Text.RegularExpressions;
using FerretSharp.Core.Resources;
using FerretSharp.Core.Schema;

namespace FerretSharp.Core.ClrModel;

/// <summary>C# for a column the entity has no property for yet (WP-23).</summary>
/// <param name="Declaration">The property, e.g. <c>public string? Fax { get; set; }</c>.</param>
/// <param name="Configuration">Its line for an <c>IEntityTypeConfiguration</c>, e.g. <c>builder.Property(x =&gt; x.Fax).HasMaxLength(30);</c>.</param>
/// <param name="Notes">Where name, type or converter came from, and what is left to do by hand.</param>
public sealed record PropertySuggestion(string Column, string Name, string Declaration, string Configuration, IReadOnlyList<string> Notes);

/// <summary>
/// A property for a column in the style of the linked project, from the model export only (decision of the user, WP-23):
/// name, type and converter of the same column elsewhere in the model; for a one-character text column the project's own
/// yes/no converter (<c>J</c>/<c>N</c>); otherwise the name in PascalCase and the type EF Core would read. The configuration
/// is written fluent; <c>HasColumnName</c> only if the project does not name its columns by a convention
/// (<c>KundenId</c> → <c>KUNDEN_ID</c>) or the column does not follow it.
/// </summary>
public static partial class PropertyCode
{
    /// <summary>Share of properties whose column follows <see cref="UpperSnake"/> above which the project counts as using it.</summary>
    private const double ConventionShare = 0.95;

    public static PropertySuggestion Suggest(ModelExport model, ColumnInfo column)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(column);
        var properties = model.Entities.SelectMany(e => e.Properties).Where(p => !p.IsShadow && p.Column is not null).ToList();
        var notes = new List<string>();

        string name;
        string type;
        string? converter;
        // The same column elsewhere counts only if its property can read this column: AKTIV as NUMBER(1) → bool says
        // nothing about a CHAR(1) AKTIV, which needs the project's yes/no converter (E2E of WP-23).
        var anyTable = new TableSummary("", "", TableKind.Table);
        var elsewhere = model.Entities
            .SelectMany(e => e.Properties.Where(p => !p.IsShadow && p.Column == column.Name)
                .Where(p => !ColumnTypeCheck.Check(e, p, anyTable, column, readOnly: false).Any(m => m.Kind == ColumnMismatchKind.Type)))
            .GroupBy(p => (p.Name, p.ClrType, p.Converter))
            .OrderByDescending(g => g.Count())
            .FirstOrDefault();
        if (elsewhere is not null)
        {
            (name, type, converter) = elsewhere.Key;
            notes.Add(TextFormat.Plural(elsewhere.Count(), ClrModelText.PropertyLikeElsewhereOne, ClrModelText.PropertyLikeElsewhereOther, column.Name));
        }
        else
        {
            name = PascalCase(column.Name);
            if (IsOneCharacterText(column) && YesNoConverter(properties) is { } yesNo)
            {
                (type, converter) = ("bool", yesNo.Converter);
                notes.Add(TextFormat.Plural(yesNo.Count, ClrModelText.PropertyYesNoOne, ClrModelText.PropertyYesNoOther, yesNo.Converter));
            }
            else
            {
                (type, converter) = (DefaultType(column), null);
            }
        }

        if (converter is not null && !IsTypeName(converter))
        {
            notes.Add(TextFormat.Format(ClrModelText.PropertyConverterByHand, converter));
            converter = null;
        }

        name = CSharpCode.Identifier(name);
        var configuration = new StringBuilder($"builder.Property(x => x.{name})");
        if (!UsesConvention(properties) || UpperSnake(name) != column.Name)
        {
            configuration.Append($".HasColumnName({CSharpCode.String(column.Name)})");
        }

        if (converter is not null)
        {
            configuration.Append($".HasConversion<{converter}>()");
        }
        else if (type == "string" && OracleTypes.HasLength(column.DataType) && column.Length is { } length)
        {
            configuration.Append($".HasMaxLength({length})");
        }
        else if (type == "decimal" && column.Precision is { } precision)
        {
            configuration.Append($".HasPrecision({precision}, {column.Scale ?? 0})");
        }

        // What the database fills in itself (R4a): EF must not write a virtual column (ORA-54013) or an identity column
        // GENERATED ALWAYS (ORA-32795), and leaves a column with a default to the database only if told so.
        if (column.IsVirtual && !string.IsNullOrWhiteSpace(column.Default))
        {
            configuration.Append($".HasComputedColumnSql({CSharpCode.String(column.Default.Trim())})");
        }
        else if (column.IsIdentity)
        {
            configuration.Append(".ValueGeneratedOnAdd()");
        }
        else if (!string.IsNullOrWhiteSpace(column.Default))
        {
            configuration.Append($".HasDefaultValueSql({CSharpCode.String(column.Default.Trim())})");
        }

        if (type == "string" && !column.Nullable)
        {
            configuration.Append(".IsRequired()");
        }

        configuration.Append(';');
        var declaration = (type, column.Nullable) switch
        {
            (_, true) => $"public {type}? {name} {{ get; set; }}",
            ("string", false) => $"public string {name} {{ get; set; }} = \"\";", // nullable reference types: never null
            ("byte[]", false) => $"public byte[] {name} {{ get; set; }} = [];",
            _ => $"public {type} {name} {{ get; set; }}",
        };
        return new PropertySuggestion(column.Name, name, declaration, configuration.ToString(), notes);
    }

    /// <summary><c>KundenId</c> → <c>KUNDEN_ID</c>, <c>PLZ</c> → <c>PLZ</c>, <c>HttpUrl2</c> → <c>HTTP_URL2</c>.</summary>
    public static string UpperSnake(string name)
    {
        var result = new StringBuilder();
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            var boundary = i > 0 && char.IsUpper(c)
                           && (char.IsLower(name[i - 1]) || char.IsDigit(name[i - 1]) || i + 1 < name.Length && char.IsUpper(name[i - 1]) && char.IsLower(name[i + 1]));
            if (boundary)
            {
                result.Append('_');
            }

            result.Append(char.ToUpperInvariant(c));
        }

        return result.ToString();
    }

    /// <summary>
    /// <c>KUNDEN_ID</c> → <c>KundenId</c>; a name with lower case letters (<c>"Nummer"</c>) keeps them, upper-cased at the
    /// start. Always a C# identifier (R4a): other characters part words like <c>_</c> (<c>NR#</c> → <c>Nr</c>,
    /// <c>"Größe (cm)"</c> → <c>GrößeCm</c>), a leading digit gets a <c>_</c>.
    /// </summary>
    public static string PascalCase(string column)
    {
        var keepCase = column.Any(char.IsLower);
        var name = string.Concat(NonWordCharacters().Split(column).Where(p => p.Length > 0)
            .Select(p => char.ToUpperInvariant(p[0]) + (keepCase ? p[1..] : p[1..].ToLowerInvariant())));
        return name.Length == 0 || char.IsDigit(name[0]) ? "_" + name : name;
    }

    [GeneratedRegex(@"[^\p{L}\p{Nd}]+")]
    private static partial Regex NonWordCharacters();

    /// <summary>Most properties of the project have the column <see cref="UpperSnake"/> makes of their name: a naming convention.</summary>
    private static bool UsesConvention(IReadOnlyList<PropertyExport> properties) =>
        properties.Count > 0 && properties.Count(p => p.Column == UpperSnake(p.Name)) >= properties.Count * ConventionShare;

    private static bool IsOneCharacterText(ColumnInfo column) => column.DataType is "CHAR" or "VARCHAR2" && column.Length == 1;

    /// <summary>The converter the project uses most for bools stored as text (<c>J</c>/<c>N</c>), and how often.</summary>
    private static (string Converter, int Count)? YesNoConverter(IEnumerable<PropertyExport> properties) =>
        properties.Where(p => p is { ClrType: "bool", Converter: not null, ProviderClrType: "string" })
            .GroupBy(p => p.Converter!)
            .OrderByDescending(g => g.Count())
            .Select(g => (g.Key, g.Count()))
            .FirstOrDefault() is { Key: not null } found ? found : null;

    /// <summary>A converter usable as <c>HasConversion&lt;T&gt;()</c>: a plain type name, not one built inline (<c>ValueConverter&lt;bool, string&gt;</c>).</summary>
    private static bool IsTypeName(string converter) => !converter.Contains('<', StringComparison.Ordinal) && !converter.StartsWith("ValueConverter", StringComparison.Ordinal);

    /// <summary>What EF Core with the Oracle provider reads from the column (as FerretSharp's editor heuristics, CLAUDE.md Core).</summary>
    private static string DefaultType(ColumnInfo column) => column.DataType switch
    {
        "NUMBER" when column.Scale is 0 && column.Precision is { } p => p <= 9 ? "int" : p <= 18 ? "long" : "decimal",
        "NUMBER" => "decimal",
        "FLOAT" or "BINARY_DOUBLE" => "double",
        "BINARY_FLOAT" => "float",
        "DATE" => "DateTime",
        "RAW" when column.Length == 16 => "Guid",
        "RAW" or "BLOB" or "LONG RAW" => "byte[]",
        _ when column.DataType.StartsWith("TIMESTAMP", StringComparison.Ordinal) =>
            column.DataType.EndsWith("WITH TIME ZONE", StringComparison.Ordinal) ? "DateTimeOffset" : "DateTime",
        _ when column.DataType.StartsWith("INTERVAL DAY", StringComparison.Ordinal) => "TimeSpan",
        _ => "string",
    };
}
