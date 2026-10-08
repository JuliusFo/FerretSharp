using System.Text;
using FerretSharp.Core.Data;
using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Oracle;

/// <summary>Result of parsing an edited cell: the typed value (null = SQL NULL) or a message for the user.</summary>
public sealed record ParsedValue(object? Value, string? Error)
{
    public bool IsValid => Error is null;

    public static ParsedValue Ok(object? value) => new(value, null);

    public static ParsedValue Fail(string error) => new(null, error);
}

/// <summary>
/// Editing rules per column (v2, WP-09): which columns can be edited, text → typed value with a German message on
/// invalid input, value → edit text, and value equality for "changed back to the original". No driver types: the
/// session maps the values to Oracle parameters (<see cref="BindType"/>).
/// </summary>
public static class OracleTypeMapper
{
    /// <summary>Why a column cannot be edited; null if it can.</summary>
    /// <param name="newRow">Primary key columns can be filled in new rows only.</param>
    public static string? NotEditableReason(TableDetails table, ColumnInfo column, bool newRow)
    {
        if (table.Table.Kind != TableKind.Table)
        {
            return "Views und Materialized Views sind nur lesbar.";
        }

        if (QueryBuilder.RowKeyOf(table) == RowKeyKind.None)
        {
            return "Ohne Primärschlüssel und ROWID lassen sich Zeilen nicht eindeutig ändern.";
        }

        if (column.IsVirtual)
        {
            return "Virtuelle Spalte – Oracle berechnet den Wert.";
        }

        if (column.IsIdentity)
        {
            return "Identity-Spalte – Oracle vergibt den Wert.";
        }

        if (!newRow && table.PrimaryKey.Contains(column.Name))
        {
            return "Primärschlüssel sind nur bei neuen Zeilen änderbar.";
        }

        return IsEditableType(column) || IsLob(column) ? null : $"{column.DisplayType} lässt sich hier nicht bearbeiten.";
    }

    /// <summary>VARCHAR2/NVARCHAR2/CHAR/NCHAR, NUMBER/FLOAT/INTEGER, DATE, TIMESTAMP (also WITH [LOCAL] TIME ZONE), RAW.</summary>
    public static bool IsEditableType(ColumnInfo column) => ColumnCategories.Of(column) switch
    {
        ColumnCategory.Text => column.DataType is "VARCHAR2" or "NVARCHAR2" or "CHAR" or "NCHAR",
        ColumnCategory.Number => column.DataType is "NUMBER" or "FLOAT" or "INTEGER",
        ColumnCategory.Date or ColumnCategory.Timestamp or ColumnCategory.TimestampWithTimeZone or ColumnCategory.Raw => true,
        _ => false,
    };

    /// <summary>
    /// TIMESTAMP WITH LOCAL TIME ZONE: stored in the database's time zone, read and written in the session's – a time
    /// without offset (<see cref="DateTime"/>). TIMESTAMP WITH TIME ZONE keeps its offset (<see cref="DateTimeOffset"/>).
    /// </summary>
    public static bool IsLocalTimeZone(ColumnInfo column) => column.DataType.Contains("LOCAL TIME ZONE", StringComparison.Ordinal);

    /// <summary>CLOB, NCLOB, BLOB: edited as a whole in the LOB editor (WP-10), not in the cell.</summary>
    public static bool IsLob(ColumnInfo column) => ColumnCategories.Of(column) is ColumnCategory.Clob or ColumnCategory.Blob;

    /// <summary>
    /// Checks a whole LOB value from the LOB editor: text for CLOB/NCLOB, bytes for BLOB, null = SQL NULL. An empty
    /// text is NULL in Oracle, like for VARCHAR2.
    /// </summary>
    public static ParsedValue CheckContent(ColumnInfo column, object? value)
    {
        if (value is string { Length: 0 })
        {
            value = null;
        }

        if (value is null)
        {
            return column.Nullable ? ParsedValue.Ok(null) : ParsedValue.Fail($"{column.Name} darf nicht leer sein (NOT NULL).");
        }

        return (ColumnCategories.Of(column), value) switch
        {
            (ColumnCategory.Clob, string) or (ColumnCategory.Blob, byte[]) => ParsedValue.Ok(value),
            _ => ParsedValue.Fail($"{column.DisplayType} erwartet {(ColumnCategories.Of(column) == ColumnCategory.Blob ? "Bytes" : "Text")}."),
        };
    }

    /// <summary>A value that cannot be edited even though its column can (NUMBER with more than 28 digits).</summary>
    public static bool IsEditableValue(object? value) => value is not BigNumber;

    /// <summary>The full value as text for the editor (no shortening, no thousands separators); empty for NULL.</summary>
    public static string EditText(ColumnInfo column, object? value) => DelimitedExport.CellText(column, value).Text;

    /// <summary>Text as typed → value of the column's type. Empty text is NULL (in Oracle '' is NULL anyway).</summary>
    public static ParsedValue Parse(ColumnInfo column, string text)
    {
        if (string.IsNullOrEmpty(text) || (ColumnCategories.Of(column) != ColumnCategory.Text && string.IsNullOrWhiteSpace(text)))
        {
            return column.Nullable ? ParsedValue.Ok(null) : ParsedValue.Fail($"{column.Name} darf nicht leer sein (NOT NULL).");
        }

        return ColumnCategories.Of(column) switch
        {
            ColumnCategory.Text => ParseText(column, text),
            ColumnCategory.Number => ParseNumber(column, text),
            ColumnCategory.Date => FilterRules.TryParseDate(text, out var date, out _)
                ? ParsedValue.Ok(date)
                : ParsedValue.Fail($"„{text}“ ist kein Datum (TT.MM.JJJJ [hh:mm[:ss]])."),
            ColumnCategory.Timestamp => FilterRules.TryParseDate(text, out var timestamp, out _)
                ? ParsedValue.Ok(timestamp)
                : ParsedValue.Fail($"„{text}“ ist kein Zeitstempel (TT.MM.JJJJ hh:mm:ss[,ffffff])."),
            ColumnCategory.TimestampWithTimeZone when IsLocalTimeZone(column) => FilterRules.TryParseDate(text, out var local, out _)
                ? ParsedValue.Ok(local)
                : ParsedValue.Fail($"„{text}“ ist kein Zeitstempel (TT.MM.JJJJ hh:mm:ss[,ffffff])."),
            ColumnCategory.TimestampWithTimeZone => ParseWithOffset(text),
            ColumnCategory.Raw => ParseRaw(column, text),
            _ => ParsedValue.Fail($"{column.DisplayType} lässt sich hier nicht bearbeiten."),
        };
    }

    /// <summary>Equality as Oracle would see it: 1.50 = 1.5, byte arrays by content, texts exactly.</summary>
    public static bool ValuesEqual(object? a, object? b) => (a, b) switch
    {
        (null, null) => true,
        (null, _) or (_, null) => false,
        (decimal x, decimal y) => x == y,
        (byte[] x, byte[] y) => x.AsSpan().SequenceEqual(y),
        // A LOB known only as preview and length (as loaded in the grid) against a whole value.
        (LobValue lob, string text) => lob.Length == text.Length && lob.Preview is { } preview && text.StartsWith(preview, StringComparison.Ordinal),
        (string text, LobValue lob) => lob.Length == text.Length && lob.Preview is { } preview && text.StartsWith(preview, StringComparison.Ordinal),
        (LobValue { Preview: null } lob, byte[] bytes) => lob.Length == bytes.Length,
        (byte[] bytes, LobValue { Preview: null } lob) => lob.Length == bytes.Length,
        (DateTime x, DateTime y) => x == y,
        // The same instant with another offset is another value for TIMESTAMP WITH TIME ZONE (Equals compares instants).
        (DateTimeOffset x, DateTimeOffset y) => x.EqualsExact(y),
        _ => Equals(a, b),
    };

    /// <summary>How the value is bound (CHAR blank-padding, national character set, RAW, DATE vs. TIMESTAMP).</summary>
    public static OracleTypeHint BindType(ColumnInfo column) => column.DataType switch
    {
        "CHAR" => OracleTypeHint.Char,
        "NCHAR" or "NVARCHAR2" => OracleTypeHint.NVarchar2,
        "VARCHAR2" => OracleTypeHint.Varchar2,
        "NUMBER" or "FLOAT" or "INTEGER" => OracleTypeHint.Number,
        "DATE" => OracleTypeHint.Date,
        "RAW" => OracleTypeHint.Raw,
        "CLOB" => OracleTypeHint.Clob,
        "NCLOB" => OracleTypeHint.NClob,
        "BLOB" => OracleTypeHint.Blob,
        _ when ColumnCategories.Of(column) == ColumnCategory.Timestamp => OracleTypeHint.TimeStamp,
        _ when ColumnCategories.Of(column) == ColumnCategory.TimestampWithTimeZone =>
            IsLocalTimeZone(column) ? OracleTypeHint.TimeStampLTZ : OracleTypeHint.TimeStampTZ,
        _ => OracleTypeHint.Auto,
    };

    private static ParsedValue ParseText(ColumnInfo column, string text)
    {
        if (column.Length is not { } max)
        {
            return ParsedValue.Ok(text);
        }

        // BYTE semantics count bytes of the database character set; AL32UTF8 is assumed (Oracle reports ORA-12899 otherwise).
        var byteSemantics = column.DataType is "VARCHAR2" or "CHAR" && !column.CharSemantics;
        var length = byteSemantics ? Encoding.UTF8.GetByteCount(text) : text.Length;
        return length <= max
            ? ParsedValue.Ok(text)
            : ParsedValue.Fail(byteSemantics
                ? $"Zu lang: {length} Bytes, erlaubt sind {max} ({column.DisplayType})."
                : $"Zu lang: {length} Zeichen, erlaubt sind {max} ({column.DisplayType}).");
    }

    private static ParsedValue ParseNumber(ColumnInfo column, string text)
    {
        if (!FilterRules.TryParseNumber(text, out var value))
        {
            return ParsedValue.Fail($"„{text}“ ist keine Zahl (z. B. 4711 oder 12,5).");
        }

        var scale = column.DataType == "INTEGER" ? 0 : column.Scale;
        if (scale is { } s and >= 0 && value.Scale > s && decimal.Round(value, s) != value)
        {
            return ParsedValue.Fail(s == 0 ? $"{column.Name} nimmt nur ganze Zahlen ({column.DisplayType})." : $"Höchstens {s} Nachkommastellen ({column.DisplayType}).");
        }

        if (column.DataType == "NUMBER" && column.Precision is { } p && scale is { } sc)
        {
            var integerDigits = decimal.Truncate(decimal.Abs(value)).ToString(System.Globalization.CultureInfo.InvariantCulture).TrimStart('0').Length;
            if (integerDigits > p - sc)
            {
                return ParsedValue.Fail($"Zu groß: höchstens {p - sc} Stellen vor dem Komma ({column.DisplayType}).");
            }
        }

        return ParsedValue.Ok(value);
    }

    /// <summary>
    /// A time with an optional offset at the end – <c>+02:00</c>, <c>-0530</c>, <c>+2</c> or <c>Z</c>, with or without a
    /// blank before it (ISO <c>2026-10-08T12:00:00+02:00</c>). Without one the time zone of this computer applies for that
    /// date, as Oracle takes the session's time zone for a time without one. Region names (<c>Europe/Berlin</c>) are not
    /// taken: the value keeps an offset.
    /// </summary>
    private static ParsedValue ParseWithOffset(string text)
    {
        switch (FilterRules.TryParseDateWithOffset(text, out var value, out var offset))
        {
            case true:
                return ParsedValue.Ok(value);
            case false:
                return ParsedValue.Fail($"„{offset}“ ist kein Zeitzonen-Offset (zwischen -12:00 und +14:00, z. B. +02:00).");
        }

        // No offset (or a date like 2026-10-08, whose "-08" only looks like one).
        return FilterRules.TryParseDate(text.Trim(), out var local, out _)
            ? ParsedValue.Ok(new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local)))
            : ParsedValue.Fail($"„{text}“ ist kein Zeitstempel mit Zeitzone (TT.MM.JJJJ hh:mm:ss[,ffffff] [+hh:mm]).");
    }

    private static ParsedValue ParseRaw(ColumnInfo column, string text)
    {
        if (!FilterRules.TryParseHex(text, out var bytes))
        {
            return ParsedValue.Fail($"„{text}“ ist kein Hex-Wert (z. B. CAFE01).");
        }

        return column.Length is { } max && bytes.Length > max
            ? ParsedValue.Fail($"Zu lang: {bytes.Length} Bytes, erlaubt sind {max} ({column.DisplayType}).")
            : ParsedValue.Ok(bytes);
    }
}
