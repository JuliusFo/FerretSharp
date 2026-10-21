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
        _ when OracleTypes.HasTimeZone(dataType) || OracleTypes.IsLocalTimeZone(dataType) =>
            ColumnCategory.TimestampWithTimeZone,
        _ when OracleTypes.IsTimestamp(dataType) => ColumnCategory.Timestamp,
        _ when OracleTypes.IsInterval(dataType) => ColumnCategory.Interval,
        _ => ColumnCategory.Unsupported,
    };

    /// <summary>Can appear in ORDER BY (LOBs, LONG and object types cannot).</summary>
    public static bool IsSortable(ColumnCategory category) =>
        category is not (ColumnCategory.Clob or ColumnCategory.Blob or ColumnCategory.Long or ColumnCategory.Unsupported);

    /// <summary>Fractional second digits of a TIMESTAMP(n) type (default 6).</summary>
    public static int FractionalDigits(ColumnInfo column) => column.Scale ?? 6;

    /// <summary>Fractional second digits a <see cref="DateTime"/> holds (100 ns): TIMESTAMP values are read as one.</summary>
    public const int ReadFractionalDigits = 7;

    /// <summary>A TIMESTAMP(8) or (9): its values are read without their finest fractions (review 3.44.0, A12).</summary>
    public static bool LosesFractionsWhenRead(ColumnInfo column) =>
        Of(column) is ColumnCategory.Timestamp or ColumnCategory.TimestampWithTimeZone && FractionalDigits(column) > ReadFractionalDigits;
}
