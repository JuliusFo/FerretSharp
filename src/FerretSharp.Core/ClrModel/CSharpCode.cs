using System.Globalization;
using System.Text;
using FerretSharp.Core.Data;
using FerretSharp.Core.Query;
using FerretSharp.Core.Resources;
using FerretSharp.Core.Schema;

namespace FerretSharp.Core.ClrModel;

/// <summary>A C# expression for a value, or why there is none.</summary>
/// <param name="Code">The expression (<c>Kundenart.Gewerbe</c>, <c>1234.5m</c>); null if the value has none.</param>
/// <param name="Problem">Why there is no expression, for a comment in the generated code.</param>
public sealed record CSharpValue(string? Code, string? Problem = null)
{
    public static CSharpValue Fails(string problem) => new(null, problem);
}

/// <summary>
/// C# spelling of database values for generated code (WP-15): a value read from a column becomes a literal of the
/// property's type – enum members and converted bools through the <see cref="ValueTable"/> (the project's own converters),
/// everything else as EF Core maps it by default. Values that have no C# form come back with a reason instead.
/// </summary>
public static class CSharpCode
{
    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");

    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked", "class", "const", "continue",
        "decimal", "default", "delegate", "do", "double", "else", "enum", "event", "explicit", "extern", "false", "finally",
        "fixed", "float", "for", "foreach", "goto", "if", "implicit", "in", "int", "interface", "internal", "is", "lock",
        "long", "namespace", "new", "null", "object", "operator", "out", "override", "params", "private", "protected",
        "public", "readonly", "ref", "return", "sbyte", "sealed", "short", "sizeof", "stackalloc", "static", "string",
        "struct", "switch", "this", "throw", "true", "try", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort",
        "using", "virtual", "void", "volatile", "while",
    };

    /// <summary>A name as identifier: keywords get an <c>@</c>.</summary>
    public static string Identifier(string name) => Keywords.Contains(name) ? "@" + name : name;

    /// <summary><c>Kunde</c> → <c>kunde</c>, <c>Kunden</c> → <c>kunden</c>; keywords get an <c>@</c>.</summary>
    public static string LocalName(string name) =>
        Identifier(name.Length == 0 ? name : char.ToLowerInvariant(name[0]) + name[1..]);

    /// <summary>A regular string literal; control characters and line breaks as escapes.</summary>
    public static string String(string value)
    {
        var text = new StringBuilder(value.Length + 2).Append('"');
        foreach (var c in value)
        {
            text.Append(c switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                '\0' => "\\0",
                '\t' => "\\t",
                '\r' => "\\r",
                '\n' => "\\n",
                _ when char.IsControl(c) || c is (char)0x2028 or (char)0x2029 => $"\\u{(int)c:X4}",
                _ => c.ToString(),
            });
        }

        return text.Append('"').ToString();
    }

    /// <summary>
    /// The C# expression for a database value of a property: <c>null</c> for NULL, otherwise a literal of the property's
    /// type. <paramref name="values"/> is the property's value table (enum or converted bool), if it has one.
    /// </summary>
    public static CSharpValue Value(PropertyExport property, ValueTable? values, ColumnInfo column, object? raw)
    {
        if (raw is null or DBNull)
        {
            return new CSharpValue("null");
        }

        if (values is not null)
        {
            return Member(property, values, column, raw);
        }

        if (property.Converter is { } converter)
        {
            return CSharpValue.Fails(TextFormat.Format(ClrModelText.ValueCustomConverter, converter, Shown(column, raw)));
        }

        if (raw is LobValue lob)
        {
            if (lob.Preview is { } preview && preview.Length == lob.Length && property.ClrType == "string")
            {
                return new CSharpValue(String(preview));
            }

            return CSharpValue.Fails(lob.Preview is null
                ? TextFormat.Format(German, ClrModelText.LobNotLoaded, column.DataType, lob.Length)
                : TextFormat.Format(German, ClrModelText.LobPreviewOnly, column.DataType, lob.Length));
        }

        var code = property.ClrType switch
        {
            "string" => raw is string s ? String(s) : null,
            "char" => raw is string { Length: 1 } c ? Char(c[0]) : null,
            "bool" => raw is bool b ? Bool(b) : Number(raw) switch { 0m => "false", 1m => "true", _ => null },
            "int" => Integral(raw, int.MinValue, int.MaxValue),
            "long" => Integral(raw, long.MinValue, long.MaxValue),
            "short" => Integral(raw, short.MinValue, short.MaxValue),
            "byte" => Integral(raw, byte.MinValue, byte.MaxValue),
            "sbyte" => Integral(raw, sbyte.MinValue, sbyte.MaxValue),
            "ushort" => Integral(raw, ushort.MinValue, ushort.MaxValue),
            "uint" => Integral(raw, uint.MinValue, uint.MaxValue),
            "ulong" => Integral(raw, ulong.MinValue, ulong.MaxValue),
            "decimal" => Number(raw) is { } d ? d.ToString(CultureInfo.InvariantCulture) + "m" : null,
            "double" => Floating(raw, "double", ""),
            "float" => Floating(raw, "float", "f"),
            "DateTime" => raw is DateTime dt ? DateTimeLiteral(dt) : null,
            "DateOnly" => raw is DateTime date ? $"new DateOnly({date.Year}, {date.Month}, {date.Day})" : null,
            "TimeOnly" => raw is DateTime time ? TimeOnlyLiteral(time.TimeOfDay) : raw is TimeSpan ts ? TimeOnlyLiteral(ts) : null,
            "DateTimeOffset" => raw switch
            {
                DateTimeOffset dto => DateTimeOffsetLiteral(dto),
                // A filter value without zone: Oracle reads it in the session's time zone, which is the client's.
                DateTime local => DateTimeOffsetLiteral(new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local))),
                _ => null,
            },
            "TimeSpan" => raw is string interval && ParseDayToSecond(interval) is { } span ? TimeSpanLiteral(span) : null,
            "Guid" => raw switch
            {
                // Oracle's EF provider stores a Guid in RAW(16) as Guid.ToByteArray() (.NET byte order).
                byte[] { Length: 16 } bytes => GuidLiteral(new Guid(bytes)),
                string text when Guid.TryParse(text, out var guid) => GuidLiteral(guid),
                _ => null,
            },
            "byte[]" => raw is byte[] data ? $"Convert.FromHexString(\"{Convert.ToHexString(data)}\")" : null,
            _ => null,
        };

        return code is not null
            ? new CSharpValue(code)
            : CSharpValue.Fails(TextFormat.Format(ClrModelText.ValueNotWritableAs, Shown(column, raw), TablePresentation.ClrTypeText(property)));
    }

    /// <summary>An enum member (<c>Kundenart.Gewerbe</c>), a flags combination, a cast for a number without member, true/false.</summary>
    private static CSharpValue Member(PropertyExport property, ValueTable values, ColumnInfo column, object raw)
    {
        var type = property.ClrType;
        if (values.Find(raw) is { } mapping)
        {
            return new CSharpValue(values.IsBool ? mapping.Name : $"{type}.{Identifier(mapping.Name)}");
        }

        if (values.FlagMembers(raw) is { } members)
        {
            return new CSharpValue(string.Join(" | ", members.Select(m => $"{type}.{Identifier(m)}")));
        }

        if (values.StoredAsNumber && Number(raw) is { } number && number == decimal.Truncate(number))
        {
            var text = number.ToString(CultureInfo.InvariantCulture);
            return new CSharpValue(number < 0 ? $"({type})({text})" : $"({type}){text}");
        }

        return CSharpValue.Fails(values.IsBool
            ? property.Converter is { } converter
                ? TextFormat.Format(ClrModelText.ValueNotTrueOrFalse, Shown(column, raw), converter)
                : TextFormat.Format(ClrModelText.ValueNotTrueOrFalseConverter, Shown(column, raw))
            : TextFormat.Format(ClrModelText.ValueNotMember, Shown(column, raw), type));
    }

    /// <summary>The value as an Oracle literal, for comments (<c>'J'</c>, <c>12</c>).</summary>
    internal static string Shown(ColumnInfo column, object? raw) => InsertExport.Literal(column, raw, new ExportWarnings());

    private static decimal? Number(object raw) => raw switch
    {
        decimal d => d,
        int or long or short or byte => Convert.ToDecimal(raw, CultureInfo.InvariantCulture),
        BigNumber big => decimal.TryParse(big.Invariant, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null,
        double d when double.IsFinite(d) && Math.Abs(d) < 7.9e28 => (decimal)d,
        float f when float.IsFinite(f) && Math.Abs(f) < 7.9e28f => (decimal)f,
        _ => null,
    };

    private static string? Integral(object raw, decimal min, decimal max) =>
        Number(raw) is { } d && d == decimal.Truncate(d) && d >= min && d <= max
            ? decimal.Truncate(d).ToString(CultureInfo.InvariantCulture)
            : null;

    private static string? Floating(object raw, string type, string suffix) => raw switch
    {
        double d when double.IsNaN(d) => $"{type}.NaN",
        double d when double.IsInfinity(d) => d > 0 ? $"{type}.PositiveInfinity" : $"{type}.NegativeInfinity",
        float f when float.IsNaN(f) => $"{type}.NaN",
        float f when float.IsInfinity(f) => f > 0 ? $"{type}.PositiveInfinity" : $"{type}.NegativeInfinity",
        double d => d.ToString("R", CultureInfo.InvariantCulture) + suffix,
        float f => f.ToString("R", CultureInfo.InvariantCulture) + suffix,
        _ => Number(raw) is { } n ? n.ToString(CultureInfo.InvariantCulture) + suffix : null,
    };

    private static string Bool(bool value) => value ? "true" : "false";

    private static string Char(char c) => c switch
    {
        '\'' => "'\\''",
        '\\' => "'\\\\'",
        _ when char.IsControl(c) => $"'\\u{(int)c:X4}'",
        _ => $"'{c}'",
    };

    /// <summary><c>new DateTime(2026, 10, 5)</c>, with time, milli- and microseconds only as far as needed.</summary>
    public static string DateTimeLiteral(DateTime value) => $"new DateTime({DateParts(value)})" + ExtraTicks(value.Ticks);

    private static string DateTimeOffsetLiteral(DateTimeOffset value)
    {
        var offset = value.Offset == TimeSpan.Zero ? "TimeSpan.Zero"
            : value.Offset.Minutes == 0 ? $"TimeSpan.FromHours({value.Offset.Hours})"
            : $"new TimeSpan({value.Offset.Hours}, {value.Offset.Minutes}, 0)";
        var parts = DateParts(value.DateTime, alwaysTime: true);
        return $"new DateTimeOffset({parts}, {offset})" + ExtraTicks(value.Ticks);
    }

    private static string DateParts(DateTime value, bool alwaysTime = false)
    {
        var parts = new List<int> { value.Year, value.Month, value.Day };
        if (alwaysTime || value.TimeOfDay != TimeSpan.Zero)
        {
            parts.AddRange([value.Hour, value.Minute, value.Second]);
            if (value.Millisecond != 0 || value.Microsecond != 0)
            {
                parts.Add(value.Millisecond);
            }

            if (value.Microsecond != 0)
            {
                parts.Add(value.Microsecond);
            }
        }

        return string.Join(", ", parts);
    }

    /// <summary>Ticks below a microsecond (TIMESTAMP(7)): no constructor takes them.</summary>
    private static string ExtraTicks(long ticks) => ticks % 10 is var rest and not 0 ? $".AddTicks({rest})" : "";

    private static string TimeOnlyLiteral(TimeSpan time) =>
        time.Ticks % TimeSpan.TicksPerSecond == 0
            ? $"new TimeOnly({time.Hours}, {time.Minutes}, {time.Seconds})"
            : $"new TimeOnly({time.Ticks})";

    private static string TimeSpanLiteral(TimeSpan span) =>
        span.Ticks % TimeSpan.TicksPerMillisecond != 0 ? $"TimeSpan.FromTicks({span.Ticks})"
        : span.Milliseconds != 0 ? $"new TimeSpan({span.Days}, {span.Hours}, {span.Minutes}, {span.Seconds}, {span.Milliseconds})"
        : span.Days != 0 ? $"new TimeSpan({span.Days}, {span.Hours}, {span.Minutes}, {span.Seconds})"
        : $"new TimeSpan({span.Hours}, {span.Minutes}, {span.Seconds})";

    private static string GuidLiteral(Guid value) => $"new Guid(\"{value:D}\")";

    /// <summary>
    /// An INTERVAL DAY TO SECOND as the grid reads it (<c>+000000001 02:03:04.500000000</c>, sign optional); fractions
    /// beyond 100 ns are cut. Null for other forms.
    /// </summary>
    internal static TimeSpan? ParseDayToSecond(string text)
    {
        var trimmed = text.Trim();
        var negative = trimmed.StartsWith('-');
        var body = trimmed.TrimStart('+', '-');
        var space = body.IndexOf(' ', StringComparison.Ordinal);
        if (space < 0 || !int.TryParse(body[..space], NumberStyles.None, CultureInfo.InvariantCulture, out var days))
        {
            return null;
        }

        var time = body[(space + 1)..];
        var dot = time.IndexOf('.', StringComparison.Ordinal);
        if (dot >= 0 && time.Length - dot - 1 > 7)
        {
            time = time[..(dot + 8)];
        }

        if (!TimeSpan.TryParseExact(time, [@"hh\:mm\:ss\.FFFFFFF", @"hh\:mm\:ss"], CultureInfo.InvariantCulture, out var span))
        {
            return null;
        }

        span += TimeSpan.FromDays(days);
        return negative ? -span : span;
    }
}
