using System.Globalization;

namespace FerretSharp.Core.Schema;

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
