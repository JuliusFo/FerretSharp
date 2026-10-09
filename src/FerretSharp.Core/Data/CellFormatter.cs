using System.Globalization;
using System.Text;
using FerretSharp.Core.Resources;
using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Data;

/// <summary>
/// Display text for grid cells (German notation). Returns null for SQL NULL, which the UI renders explicitly,
/// so an empty string and NULL never look the same.
/// </summary>
public static class CellFormatter
{
    public const int MaxTextLength = 1000;
    public const int MaxRawBytes = 32;

    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");

    public static string? Format(ColumnInfo column, object? value) => FormatValue(column,
        ColumnCategories.Of(column) is ColumnCategory.Clob or ColumnCategory.Blob ? LobValue.FromContent(value) : value);

    private static string? FormatValue(ColumnInfo column, object? value) => value switch
    {
        null or DBNull => null,
        decimal d => FormatNumber(d.ToString(CultureInfo.InvariantCulture), column.Scale),
        BigNumber big => FormatNumber(big.Invariant, column.Scale),
        int or long or short => FormatNumber(Convert.ToString(value, CultureInfo.InvariantCulture)!, 0),
        double d => d.ToString("G17", German),
        float f => f.ToString("G9", German),
        DateTimeOffset dto => FormatDateTime(column, dto.DateTime) + " " + dto.ToString("zzz", CultureInfo.InvariantCulture),
        DateTime dt => FormatDateTime(column, dt),
        bool b => b ? "TRUE" : "FALSE",
        byte[] bytes => FormatRaw(bytes),
        LobValue { Preview: null } blob => $"‹BLOB {FormatSize(blob.Length)}›",
        LobValue { Length: 0 } => DataText.LobEmpty,
        LobValue clob => SingleLine(clob.Preview!) + (clob.Length > clob.Preview!.Length ? " …" : ""),
        NotNullMarker marker => $"‹{marker.DataType}›",
        string s => SingleLine(s.Length > MaxTextLength ? s[..MaxTextLength] + " …" : s),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture),
    };

    /// <summary>
    /// Formats an invariant number string without going through double/decimal (no precision loss):
    /// "1234567.5" with scale 2 → "1.234.567,50".
    /// </summary>
    public static string FormatNumber(string invariant, int? scale)
    {
        if (invariant.Contains('E', StringComparison.OrdinalIgnoreCase))
        {
            return invariant; // scientific notation from BINARY_DOUBLE-like values: leave as is
        }

        var negative = invariant.StartsWith('-');
        var unsigned = negative ? invariant[1..] : invariant;
        var dot = unsigned.IndexOf('.', StringComparison.Ordinal);
        var integer = dot < 0 ? unsigned : unsigned[..dot];
        var fraction = dot < 0 ? "" : unsigned[(dot + 1)..];

        if (scale is > 0 && fraction.Length < scale)
        {
            fraction = fraction.PadRight(scale.Value, '0');
        }

        var grouped = new StringBuilder();
        for (var i = 0; i < integer.Length; i++)
        {
            if (i > 0 && (integer.Length - i) % 3 == 0)
            {
                grouped.Append('.');
            }

            grouped.Append(integer[i]);
        }

        return (negative ? "-" : "") + grouped + (fraction.Length > 0 ? "," + fraction : "");
    }

    private static string FormatDateTime(ColumnInfo column, DateTime value)
    {
        var text = value.ToString("dd.MM.yyyy HH:mm:ss", CultureInfo.InvariantCulture);
        var category = ColumnCategories.Of(column);
        if (category is ColumnCategory.Timestamp or ColumnCategory.TimestampWithTimeZone)
        {
            var digits = Math.Clamp(ColumnCategories.FractionalDigits(column), 0, 7);
            if (digits > 0)
            {
                text += "," + value.ToString(new string('f', digits), CultureInfo.InvariantCulture);
            }
        }

        return text;
    }

    private static string FormatRaw(byte[] bytes) =>
        Convert.ToHexString(bytes, 0, Math.Min(bytes.Length, MaxRawBytes)) + (bytes.Length > MaxRawBytes ? " …" : "");

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => (bytes / 1024.0).ToString("0.#", German) + " KB",
        _ => (bytes / (1024.0 * 1024)).ToString("0.#", German) + " MB",
    };

    private static string SingleLine(string text) =>
        text.Contains('\n', StringComparison.Ordinal) || text.Contains('\r', StringComparison.Ordinal)
            ? text.Replace("\r\n", " ⏎ ", StringComparison.Ordinal).Replace('\n', '⏎').Replace('\r', '⏎')
            : text;
}
