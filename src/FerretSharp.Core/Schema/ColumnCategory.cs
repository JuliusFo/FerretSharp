namespace FerretSharp.Core.Schema;

/// <summary>How a column is selected, filtered, sorted and displayed.</summary>
public enum ColumnCategory
{
    Text,
    Number,
    Date,
    Timestamp,
    TimestampWithTimeZone,
    Boolean,
    Interval,
    Raw,
    Clob,
    Blob,
    /// <summary>LONG / LONG RAW: legacy, cannot be used in expressions; shown truncated, never filtered or sorted.</summary>
    Long,
    /// <summary>XMLTYPE, object types, BFILE, VECTOR, … – only "NULL or not" is shown.</summary>
    Unsupported,
}

public static class ColumnCategories
{
    public static ColumnCategory Of(ColumnInfo column) => Of(column.DataType);

    public static ColumnCategory Of(string dataType) => dataType switch
    {
        "VARCHAR2" or "NVARCHAR2" or "CHAR" or "NCHAR" or "ROWID" or "UROWID" => ColumnCategory.Text,
        "NUMBER" or "FLOAT" or "BINARY_FLOAT" or "BINARY_DOUBLE" or "INTEGER" => ColumnCategory.Number,
        "DATE" => ColumnCategory.Date,
        "BOOLEAN" => ColumnCategory.Boolean,
        "RAW" => ColumnCategory.Raw,
        "CLOB" or "NCLOB" => ColumnCategory.Clob,
        "BLOB" => ColumnCategory.Blob,
        "LONG" or "LONG RAW" => ColumnCategory.Long,
        _ when dataType.StartsWith("TIMESTAMP", StringComparison.Ordinal) && dataType.Contains("TIME ZONE", StringComparison.Ordinal) =>
            ColumnCategory.TimestampWithTimeZone,
        _ when dataType.StartsWith("TIMESTAMP", StringComparison.Ordinal) => ColumnCategory.Timestamp,
        _ when dataType.StartsWith("INTERVAL", StringComparison.Ordinal) => ColumnCategory.Interval,
        _ => ColumnCategory.Unsupported,
    };

    /// <summary>Can appear in ORDER BY (LOBs, LONG and object types cannot).</summary>
    public static bool IsSortable(ColumnCategory category) =>
        category is not (ColumnCategory.Clob or ColumnCategory.Blob or ColumnCategory.Long or ColumnCategory.Unsupported);

    /// <summary>Fractional second digits of a TIMESTAMP(n) type (default 6).</summary>
    public static int FractionalDigits(ColumnInfo column) => column.Scale ?? 6;
}
