using System.Globalization;

namespace FerretSharp.Core.Schema;

/// <summary>What changing a column's type with <c>ALTER TABLE … MODIFY</c> needs (<see cref="OracleTypes.ChangeOf"/>).</summary>
public enum ColumnTypeChange
{
    /// <summary>The same type in DDL: nothing to change.</summary>
    Same,

    /// <summary>Works with any data.</summary>
    Free,

    /// <summary>Works unless a value is too long for the new length (ORA-01441); Oracle checks the values.</summary>
    MayFailOnValues,

    /// <summary>Only while the column holds no value (ORA-01439, ORA-01440, ORA-30082).</summary>
    NeedsEmptyColumn,

    /// <summary>Never with MODIFY (to or from a LOB, ORA-22858): a new column, copy the values, drop the old one.</summary>
    NotPossible,
}

/// <summary>
/// What FerretSharp knows about Oracle column types, in one place (R3b): how a type is written for the user and in DDL,
/// which types have a length and whether it counts bytes, which can be changed into each other. Before, the same switches
/// over type names stood in the schema records, the DDL proposal, the model comparison and the value parser – and
/// drifted (<c>NUMBER(*,2)</c> was displayed as <c>NUMBER</c>, so the schema comparison missed it).
/// </summary>
public static class OracleTypes
{
    /// <summary>VARCHAR2, NVARCHAR2, CHAR, NCHAR: text with a length.</summary>
    public static bool IsCharacter(string dataType) => dataType is "VARCHAR2" or "NVARCHAR2" or "CHAR" or "NCHAR";

    /// <summary>The types whose <see cref="ColumnInfo.Length"/> is set: character types and RAW.</summary>
    public static bool HasLength(string dataType) => IsCharacter(dataType) || dataType == "RAW";

    /// <summary>
    /// The length counts bytes, not characters: VARCHAR2/CHAR with BYTE semantics and RAW. NVARCHAR2/NCHAR always count
    /// characters.
    /// </summary>
    public static bool LengthInBytes(ColumnInfo column) =>
        column.DataType is "VARCHAR2" or "CHAR" && !column.CharSemantics || column.DataType == "RAW";

    /// <summary>
    /// Large or long values, which MODIFY cannot convert and the grid only previews: CLOB, NCLOB, BLOB, BFILE, LONG, LONG RAW.
    /// </summary>
    public static bool IsLobOrLong(string dataType) => dataType is "CLOB" or "NCLOB" or "BLOB" or "BFILE" or "LONG" or "LONG RAW";

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
    /// (WP-23 experiment): text of the same kind, longer, or with CHAR instead of BYTE, DATE ↔ TIMESTAMP, more digits on both
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
                : ColumnTypeChange.Free,
            "number" => NumberChange(from, to),
            "datetime" => DateTimeChange(from.DataType, to.DataType),
            _ => ColumnTypeChange.NeedsEmptyColumn, // INTERVAL, object types …: not tried, so not promised
        };
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
            return Zone(from) == Zone(to) ? ColumnTypeChange.Free : ColumnTypeChange.NeedsEmptyColumn;
        }

        return Zone(from) != Zone(to) || Fraction(to) < Fraction(from) ? ColumnTypeChange.NeedsEmptyColumn : ColumnTypeChange.Free;

        static string Zone(string type) => type.EndsWith("WITH LOCAL TIME ZONE", StringComparison.Ordinal) ? "local"
            : type.EndsWith("WITH TIME ZONE", StringComparison.Ordinal) ? "zone" : "";

        // TIMESTAMP(6) …: the digits of the fractional seconds; 6 without a number.
        static int Fraction(string type) =>
            type.IndexOf('(', StringComparison.Ordinal) is var open and >= 0 && type.IndexOf(')', open) is var close and > 0
            && int.TryParse(type.AsSpan(open + 1, close - open - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var digits)
                ? digits
                : 6;
    }

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
