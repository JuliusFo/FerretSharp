using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;

namespace FerretSharp.Core.Schema;

/// <summary>What changing a column's type with <c>ALTER TABLE … MODIFY</c> needs (<see cref="OracleTypes.ChangeOf"/>).</summary>
public enum ColumnTypeChange
{
    /// <summary>The same type in DDL: nothing to change.</summary>
    Same,

    /// <summary>Works with any data.</summary>
    Free,

    /// <summary>
    /// Works with any data but changes values without a word (review 3.40.0, A9): TIMESTAMP → DATE cuts the fractional
    /// seconds, VARCHAR2 → CHAR pads with blanks. Committed at once like every DDL – the designer and the comparison warn.
    /// </summary>
    ChangesValues,

    /// <summary>Works unless a value is too long for the new length (ORA-01441); Oracle checks the values.</summary>
    MayFailOnValues,

    /// <summary>Only while the column holds no value (ORA-01439, ORA-01440, ORA-30082).</summary>
    NeedsEmptyColumn,

    /// <summary>Never with MODIFY (to or from a LOB, ORA-22858): a new column, copy the values, drop the old one.</summary>
    NotPossible,
}

/// <summary>What storing a value of one type in a column of another does (<see cref="OracleTypes.FitOf"/>).</summary>
public enum ValueFit
{
    /// <summary>Every value is kept as it is.</summary>
    Full,

    /// <summary>The value goes in, but loses something: fractional seconds, its time zone.</summary>
    Lossy,

    /// <summary>Not every value goes in (another family, too long, too many digits).</summary>
    None,
}

/// <summary>
/// What FerretSharp knows about Oracle column types, in one place (R3b): how a type is written for the user and in DDL,
/// which types have a length and whether it counts bytes, which can be changed into each other. Before, the same switches
/// over type names stood in the schema records, the DDL proposal, the model comparison and the value parser – and
/// drifted (<c>NUMBER(*,2)</c> was displayed as <c>NUMBER</c>, so the schema comparison missed it).
/// </summary>
public static partial class OracleTypes
{
    /// <summary>VARCHAR2, NVARCHAR2, CHAR, NCHAR: text with a length.</summary>
    public static bool IsCharacter(string dataType) => dataType is "VARCHAR2" or "NVARCHAR2" or "CHAR" or "NCHAR";

    /// <summary>The types whose <see cref="ColumnInfo.Length"/> is set: character types and RAW.</summary>
    public static bool HasLength(string dataType) => IsCharacter(dataType) || dataType == "RAW";

    /// <summary>VARCHAR2 and CHAR: the length counts BYTE or CHAR, as chosen. NVARCHAR2 and NCHAR always count characters.</summary>
    public static bool HasLengthSemantics(string dataType) => dataType is "VARCHAR2" or "CHAR";

    /// <summary>
    /// The length counts bytes, not characters: VARCHAR2/CHAR with BYTE semantics and RAW. NVARCHAR2/NCHAR always count
    /// characters.
    /// </summary>
    public static bool LengthInBytes(ColumnInfo column) =>
        HasLengthSemantics(column.DataType) && !column.CharSemantics || column.DataType == "RAW";

    /// <summary>
    /// CHAR and NCHAR: blank-padded to their length. Compared with padding (bound as CHAR), shown and looked up without it.
    /// </summary>
    public static bool IsFixedChar(string dataType) => dataType is "CHAR" or "NCHAR";

    /// <summary>
    /// NCHAR, NVARCHAR2 and NCLOB: in the national character set. Bound and written as such (<c>N'…'</c>) – in the database
    /// character set, characters it lacks become "?" (review 3.31.0, A18).
    /// </summary>
    public static bool IsNational(string dataType) => dataType is "NCHAR" or "NVARCHAR2" or "NCLOB";

    /// <summary>
    /// NUMBER, FLOAT and INTEGER: Oracle's decimal NUMBER (FLOAT is one with a binary precision, INTEGER is how a query
    /// can report one) – not BINARY_FLOAT or BINARY_DOUBLE.
    /// </summary>
    public static bool IsDecimalNumber(string dataType) => dataType is "NUMBER" or "FLOAT" or "INTEGER";

    /// <summary>NUMBER itself (with precision and scale), not INTEGER or FLOAT.</summary>
    public static bool IsNumber(string dataType) => dataType == "NUMBER";

    /// <summary>Types a number may lose digits in: FLOAT (binary precision) and the IEEE types.</summary>
    public static bool IsFloatingPoint(string dataType) => dataType is "FLOAT" or "BINARY_FLOAT" or "BINARY_DOUBLE";

    /// <summary>RAW(n); LONG RAW is a LONG (<see cref="IsLong"/>).</summary>
    public static bool IsRaw(string dataType) => dataType == "RAW";

    /// <summary>TIMESTAMP in all its forms, with or without (local) time zone.</summary>
    public static bool IsTimestamp(string dataType) => dataType.StartsWith("TIMESTAMP", StringComparison.Ordinal);

    /// <summary>TIMESTAMP … WITH TIME ZONE: keeps its offset (<see cref="DateTimeOffset"/>).</summary>
    public static bool HasTimeZone(string dataType) => IsTimestamp(dataType) && dataType.EndsWith(" WITH TIME ZONE", StringComparison.Ordinal);

    /// <summary>
    /// TIMESTAMP … WITH LOCAL TIME ZONE: stored in the database's time zone, read and written in the session's – a time
    /// without offset.
    /// </summary>
    public static bool IsLocalTimeZone(string dataType) => IsTimestamp(dataType) && dataType.EndsWith(" WITH LOCAL TIME ZONE", StringComparison.Ordinal);

    /// <summary>INTERVAL YEAR TO MONTH and INTERVAL DAY TO SECOND.</summary>
    public static bool IsInterval(string dataType) => dataType.StartsWith("INTERVAL", StringComparison.Ordinal);

    /// <summary>INTERVAL YEAR TO MONTH (<c>TO_YMINTERVAL</c>).</summary>
    public static bool IsYearToMonth(string dataType) => dataType.StartsWith("INTERVAL YEAR", StringComparison.Ordinal);

    /// <summary>INTERVAL DAY TO SECOND (<c>TO_DSINTERVAL</c>, <see cref="TimeSpan"/>).</summary>
    public static bool IsDayToSecond(string dataType) => dataType.StartsWith("INTERVAL DAY", StringComparison.Ordinal);

    /// <summary>
    /// Large or long values, which MODIFY cannot convert and the grid only previews: CLOB, NCLOB, BLOB, BFILE, LONG, LONG RAW.
    /// </summary>
    public static bool IsLobOrLong(string dataType) => dataType is "CLOB" or "NCLOB" or "BLOB" or "BFILE" or "LONG" or "LONG RAW";

    /// <summary>
    /// LONG and LONG RAW: a trigger has no <c>:new</c>/<c>:old</c> for them (ORA-04093), and a table holds at most one.
    /// </summary>
    public static bool IsLong(string dataType) => dataType is "LONG" or "LONG RAW";

    /// <summary>
    /// Values of the type can be compared with <c>=</c> and <c>&lt;&gt;</c> in SQL and PL/SQL: character, number, date,
    /// timestamp and interval types, RAW, ROWID and BOOLEAN. Not: LOBs and BFILE (PLS-00306), LONG, XMLTYPE, JSON,
    /// VECTOR and object types (any other type name in <c>DATA_TYPE</c>).
    /// </summary>
    public static bool IsComparable(string dataType) =>
        IsCharacter(dataType)
        || dataType is "NUMBER" or "FLOAT" or "BINARY_FLOAT" or "BINARY_DOUBLE" or "DATE" or "RAW" or "ROWID" or "UROWID" or "BOOLEAN"
        || IsTimestamp(dataType)
        || IsInterval(dataType);

    /// <summary>
    /// Reads a type as written in DDL (<c>NUMBER(12,2)</c>, <c>VARCHAR2(30 CHAR)</c>, <c>TIMESTAMP</c>, <c>INTEGER</c> …) into the
    /// facets of a <see cref="ColumnInfo"/> as the dictionary would show them after a CREATE TABLE: <c>TIMESTAMP</c> is
    /// <c>TIMESTAMP(6)</c>, <c>NUMBER(10)</c> has scale 0, <c>INTEGER</c> is a NUMBER with scale 0 and no precision,
    /// <c>FLOAT</c> is <c>FLOAT(126)</c>, VARCHAR2 and CHAR without CHAR count bytes (<see cref="DdlType"/> writes BYTE).
    /// The column has no name, is nullable and at position 0. Keywords in any case; false for anything else, including
    /// object types and out-of-range facets.
    /// </summary>
    public static bool TryParse(string text, [NotNullWhen(true)] out ColumnInfo? type)
    {
        type = null;
        var t = TypeSpaces().Replace(text.Trim().ToUpperInvariant(), " ");
        t = SpaceBeforeClose().Replace(SpaceAroundOpen().Replace(t, "$1"), ")");
        ColumnInfo Of(string dataType, int? length = null, bool charSemantics = false, int? precision = null, int? scale = null) =>
            new("", dataType, length, charSemantics, precision, scale, Nullable: true, IsIdentity: false, Default: null, Position: 0);

        if (TextType().Match(t) is { Success: true } chars)
        {
            var name = chars.Groups["type"].Value is "VARCHAR" ? "VARCHAR2" : chars.Groups["type"].Value;
            var unit = chars.Groups["unit"].Value;
            int? length = chars.Groups["length"].Success ? Number(chars.Groups["length"].Value) : name is "CHAR" or "NCHAR" ? 1 : null;
            var max = name is "CHAR" or "NCHAR" ? 2000 : 32767;
            if (length is not { } l || l < 1 || l > max || unit.Length > 0 && name is not ("VARCHAR2" or "CHAR"))
            {
                return false;
            }

            type = Of(name, l, charSemantics: unit == "CHAR" || name is "NVARCHAR2" or "NCHAR");
            return true;
        }

        if (NumberType().Match(t) is { Success: true } number)
        {
            var p = number.Groups["p"];
            var s = number.Groups["s"];
            int? precision = p.Success && p.Value != "*" ? Number(p.Value) : null;
            int? scale = s.Success ? Number(s.Value) : precision is null ? null : 0;
            if (precision is < 1 or > 38 || scale is < -84 or > 127)
            {
                return false;
            }

            type = Of("NUMBER", precision: precision, scale: scale);
            return true;
        }

        if (FloatType().Match(t) is { Success: true } @float)
        {
            var precision = @float.Groups["p"].Success ? Number(@float.Groups["p"].Value) : MaxFloatPrecision;
            if (precision is < 1 or > MaxFloatPrecision)
            {
                return false;
            }

            type = Of("FLOAT", precision: precision);
            return true;
        }

        if (TimestampType().Match(t) is { Success: true } timestamp)
        {
            var fraction = timestamp.Groups["f"].Success ? Number(timestamp.Groups["f"].Value) : 6;
            if (fraction > 9)
            {
                return false;
            }

            type = Of($"TIMESTAMP({N(fraction)}){timestamp.Groups["zone"].Value}", scale: fraction);
            return true;
        }

        if (IntervalType().Match(t) is { Success: true } interval)
        {
            var leading = interval.Groups["p"].Success ? Number(interval.Groups["p"].Value) : 2;
            if (interval.Groups["unit"].Value == "YEAR")
            {
                type = Of($"INTERVAL YEAR({N(leading)}) TO MONTH", precision: leading, scale: 0);
                return true;
            }

            var fraction = interval.Groups["f"].Success ? Number(interval.Groups["f"].Value) : 6;
            type = fraction > 9 ? null : Of($"INTERVAL DAY({N(leading)}) TO SECOND({N(fraction)})", precision: leading, scale: fraction);
            return type is not null;
        }

        type = t switch
        {
            "INTEGER" or "INT" or "SMALLINT" => Of("NUMBER", scale: 0),
            "DATE" or "BINARY_FLOAT" or "BINARY_DOUBLE" or "CLOB" or "NCLOB" or "BLOB" or "BFILE" or "LONG" or "LONG RAW" or "ROWID"
                or "XMLTYPE" or "JSON" or "BOOLEAN" => Of(t),
            "SYS.XMLTYPE" => Of("XMLTYPE"),
            _ => null,
        };
        return type is not null;

        static int Number(string digits) =>
            int.TryParse(digits, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n) ? n : int.MaxValue;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex TypeSpaces();

    [GeneratedRegex(@" ?([(,]) ?")]
    private static partial Regex SpaceAroundOpen();

    [GeneratedRegex(@" \)")]
    private static partial Regex SpaceBeforeClose();

    [GeneratedRegex(@"^(?<type>VARCHAR2|VARCHAR|NVARCHAR2|CHAR|NCHAR|RAW)(\((?<length>\d+)( (?<unit>BYTE|CHAR))?\))?$")]
    private static partial Regex TextType();

    [GeneratedRegex(@"^NUMBER(\((?<p>\*|\d+)(,(?<s>-?\d+))?\))?$")]
    private static partial Regex NumberType();

    [GeneratedRegex(@"^FLOAT(\((?<p>\d+)\))?$")]
    private static partial Regex FloatType();

    [GeneratedRegex(@"^TIMESTAMP(\((?<f>\d)\))?(?<zone> WITH TIME ZONE| WITH LOCAL TIME ZONE)?$")]
    private static partial Regex TimestampType();

    [GeneratedRegex(@"^INTERVAL ((?<unit>YEAR)(\((?<p>\d)\))? TO MONTH|(?<unit>DAY)(\((?<p>\d)\))? TO SECOND(\((?<f>\d)\))?)$")]
    private static partial Regex IntervalType();

    /// <summary>Type families between which <c>MODIFY</c> can change a type; anything else needs a new column.</summary>
    public static string Family(string dataType) => dataType switch
    {
        "VARCHAR2" or "NVARCHAR2" or "CHAR" or "NCHAR" => "text",
        "NUMBER" or "FLOAT" => "number",
        "DATE" => "datetime",
        _ when dataType.StartsWith("TIMESTAMP", StringComparison.Ordinal) => "datetime",
        "RAW" => "raw",
        _ => dataType, // LOBs, LONG, INTERVAL, object types …: only to the very same type
    };

    /// <summary>
    /// What <c>ALTER TABLE … MODIFY</c> from one type to another needs, as Oracle 23 answered with data in the column
    /// (WP-23 experiment): text of the same kind, longer, or with CHAR instead of BYTE, DATE ↔ TIMESTAMP (to DATE without the
    /// fractional seconds, and to CHAR padded: <see cref="ColumnTypeChange.ChangesValues"/>), more digits on both
    /// sides of the decimal point and an unconstrained NUMBER or FLOAT go through. Shorter text or RAW goes through unless a
    /// value is too long (ORA-01441). Fewer integer or decimal digits (ORA-01440), fewer fractional seconds (ORA-30082),
    /// another family, NVARCHAR2 → VARCHAR2 or a time zone added or taken away (ORA-01439) need an empty column. To or from a
    /// LOB never works (ORA-22858, also when empty).
    /// </summary>
    public static ColumnTypeChange ChangeOf(ColumnInfo from, ColumnInfo to)
    {
        if (DdlType(from) == DdlType(to))
        {
            return ColumnTypeChange.Same;
        }

        if (IsLobOrLong(from.DataType) || IsLobOrLong(to.DataType))
        {
            return ColumnTypeChange.NotPossible;
        }

        if (Family(from.DataType) != Family(to.DataType))
        {
            return ColumnTypeChange.NeedsEmptyColumn;
        }

        return Family(from.DataType) switch
        {
            "text" when from.DataType is "NVARCHAR2" or "NCHAR" && to.DataType is "VARCHAR2" or "CHAR" => ColumnTypeChange.NeedsEmptyColumn,
            "text" or "raw" => (to.Length ?? 0) < (from.Length ?? 0) || (from.CharSemantics && !to.CharSemantics)
                ? ColumnTypeChange.MayFailOnValues
                : IsFixedChar(to.DataType) && !IsFixedChar(from.DataType) ? ColumnTypeChange.ChangesValues // padded with blanks
                : ColumnTypeChange.Free,
            "number" => NumberChange(from, to),
            "datetime" => DateTimeChange(from.DataType, to.DataType),
            _ => ColumnTypeChange.NeedsEmptyColumn, // INTERVAL, object types …: not tried, so not promised
        };
    }

    /// <summary>
    /// What an INSERT of a value of type <paramref name="value"/> into a column of type <paramref name="column"/> does (WP-51:
    /// a trigger writes <c>SYSDATE</c> into an audit column of another type). Date and time types take each other's values
    /// always – Oracle converts – and keep them whole if the column has at least the fractional digits and as much time
    /// zone (none &lt; local &lt; with time zone; DATE has neither); otherwise the value goes in with a loss. Any other type
    /// keeps every value only if <c>MODIFY</c> from <paramref name="value"/> to <paramref name="column"/> works with any data
    /// (<see cref="ChangeOf"/>) – longer, more digits –, else not every value goes in.
    /// </summary>
    public static ValueFit FitOf(ColumnInfo column, ColumnInfo value)
    {
        ArgumentNullException.ThrowIfNull(column);
        ArgumentNullException.ThrowIfNull(value);
        if (Family(column.DataType) == "datetime" && Family(value.DataType) == "datetime")
        {
            return ZoneLevel(column.DataType) >= ZoneLevel(value.DataType) && FractionOf(column.DataType) >= FractionOf(value.DataType)
                ? ValueFit.Full
                : ValueFit.Lossy;
        }

        // Text padded with blanks (VARCHAR2 → CHAR) goes in; date and time types are decided above.
        return ChangeOf(value, column) is ColumnTypeChange.Same or ColumnTypeChange.Free or ColumnTypeChange.ChangesValues ? ValueFit.Full : ValueFit.None;

        static int ZoneLevel(string type) => HasTimeZone(type) ? 2 : IsLocalTimeZone(type) ? 1 : 0;

        static int FractionOf(string type) => IsTimestamp(type) ? TimestampFraction(type) : 0;
    }

    /// <summary>Binary precision of a plain <c>FLOAT</c>.</summary>
    private const int MaxFloatPrecision = 126;

    private static ColumnTypeChange NumberChange(ColumnInfo from, ColumnInfo to)
    {
        if (to.DataType == "NUMBER" && to.Precision is null && to.Scale is null || to.DataType == "FLOAT" && to.Precision is null or MaxFloatPrecision)
        {
            return ColumnTypeChange.Free; // takes any number (FLOAT is FLOAT(126) in the dictionary)
        }

        if (from.DataType == "FLOAT" || to.DataType == "FLOAT" || from.Precision is null && from.Scale is null)
        {
            return ColumnTypeChange.NeedsEmptyColumn; // binary precision or no limit before: Oracle refuses to narrow
        }

        int Integer(ColumnInfo c) => (c.Precision ?? 38) - (c.Scale ?? 0);
        return Integer(to) >= Integer(from) && (to.Scale ?? 0) >= (from.Scale ?? 0) ? ColumnTypeChange.Free : ColumnTypeChange.NeedsEmptyColumn;
    }

    private static ColumnTypeChange DateTimeChange(string from, string to)
    {
        if (from == "DATE" || to == "DATE")
        {
            // TIMESTAMP → DATE goes through with data, cutting the fractional seconds (review 3.40.0, A9).
            return Zone(from) != Zone(to) ? ColumnTypeChange.NeedsEmptyColumn
                : to == "DATE" && TimestampFraction(from) > 0 ? ColumnTypeChange.ChangesValues
                : ColumnTypeChange.Free;
        }

        return Zone(from) != Zone(to) || TimestampFraction(to) < TimestampFraction(from) ? ColumnTypeChange.NeedsEmptyColumn : ColumnTypeChange.Free;

        static string Zone(string type) => type.EndsWith("WITH LOCAL TIME ZONE", StringComparison.Ordinal) ? "local"
            : type.EndsWith("WITH TIME ZONE", StringComparison.Ordinal) ? "zone" : "";
    }

    /// <summary>TIMESTAMP(6) …: the digits of the fractional seconds; 6 without a number.</summary>
    private static int TimestampFraction(string type) =>
        type.IndexOf('(', StringComparison.Ordinal) is var open and >= 0 && type.IndexOf(')', open) is var close and > 0
        && int.TryParse(type.AsSpan(open + 1, close - open - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var digits)
            ? digits
            : 6;

    /// <summary>
    /// The type as shown to the user, e.g. <c>VARCHAR2(50 CHAR)</c>, <c>NUMBER(12,2)</c>, <c>NUMBER(*,2)</c>, <c>INTEGER</c>.
    /// BYTE is the usual semantics and left out; see <see cref="DdlType"/> for DDL.
    /// </summary>
    public static string DisplayType(ColumnInfo column) => column.DataType switch
    {
        "VARCHAR2" or "CHAR" when column.Length is { } length =>
            column.CharSemantics ? $"{column.DataType}({N(length)} CHAR)" : $"{column.DataType}({N(length)})",
        "NVARCHAR2" or "NCHAR" or "RAW" when column.Length is { } length => $"{column.DataType}({N(length)})",
        _ => NumericType(column) ?? column.DataType,
    };

    /// <summary>
    /// The type as written in DDL. Unlike <see cref="DisplayType"/> it always states BYTE or CHAR: the session's
    /// <c>NLS_LENGTH_SEMANTICS</c> must not decide.
    /// </summary>
    public static string DdlType(ColumnInfo column) => column.DataType switch
    {
        "VARCHAR2" or "CHAR" when column.Length is { } length => $"{column.DataType}({N(length)} {(column.CharSemantics ? "CHAR" : "BYTE")})",
        "NVARCHAR2" or "NCHAR" or "RAW" when column.Length is { } length => $"{column.DataType}({N(length)})",
        _ => NumericType(column) ?? column.DataType,
    };

    /// <summary>NUMBER and FLOAT with their facets; null for other types.</summary>
    private static string? NumericType(ColumnInfo column) => column.DataType switch
    {
        "NUMBER" when column.Precision is { } p && column.Scale is { } s and not 0 => $"NUMBER({N(p)},{N(s)})",
        "NUMBER" when column.Precision is { } p => $"NUMBER({N(p)})",
        "NUMBER" when column.Scale is 0 => "INTEGER",
        "NUMBER" when column.Scale is { } s => $"NUMBER(*,{N(s)})", // a scale without precision is a type of its own
        "FLOAT" when column.Precision is { } p => $"FLOAT({N(p)})",
        _ => null,
    };

    private static string N(int value) => value.ToString(CultureInfo.InvariantCulture);
}
