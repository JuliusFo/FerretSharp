using System.Globalization;
using System.Numerics;
using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Data;

/// <summary>
/// Rows of a query result sorted in memory, once all of them are loaded – no read for a header click. In the order Oracle
/// sorts them in a FerretSharp session (<c>NLS_SORT=BINARY</c>): text by its characters (ordinal), numbers exactly (also
/// beyond decimal), RAW by its bytes, a time with zone by its instant; NULL last ascending and first descending. Rows that
/// compare equal keep their order – the query's own ORDER BY breaks ties, as with <see cref="SqlResultOrder"/>.
/// </summary>
public static class RowOrder
{
    /// <summary>The column's values compare in memory as Oracle compares them; INTERVAL is read as text, LOBs as a preview.</summary>
    public static bool CanSort(ColumnInfo column) => ColumnCategories.Of(column) is ColumnCategory.Text or ColumnCategory.Number
        or ColumnCategory.Date or ColumnCategory.Timestamp or ColumnCategory.TimestampWithTimeZone or ColumnCategory.Boolean
        or ColumnCategory.Raw;

    /// <summary>The rows in the order of <paramref name="sorts"/> (positions in <see cref="RowData.Values"/>); a new list.</summary>
    public static IReadOnlyList<RowData> Sort(IReadOnlyList<RowData> rows, IReadOnlyList<ResultSort> sorts)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(sorts);
        var indexed = rows.Select((row, i) => (Row: row, Index: i)).ToList();
        indexed.Sort((x, y) =>
        {
            foreach (var sort in sorts)
            {
                var result = Compare(x.Row.Values[sort.Column], y.Row.Values[sort.Column]);
                if (result != 0)
                {
                    return sort.Descending ? -result : result;
                }
            }

            return x.Index.CompareTo(y.Index);
        });
        return [.. indexed.Select(r => r.Row)];
    }

    /// <summary>Ascending, NULL after every value.</summary>
    public static int Compare(object? x, object? y) => (x, y) switch
    {
        (null, null) => 0,
        (null, _) => 1,
        (_, null) => -1,
        (string a, string b) => Math.Sign(string.CompareOrdinal(a, b)),
        (DateTime a, DateTime b) => a.CompareTo(b),
        (DateTimeOffset a, DateTimeOffset b) => a.CompareTo(b),
        (bool a, bool b) => a.CompareTo(b),
        (byte[] a, byte[] b) => Math.Sign(a.AsSpan().SequenceCompareTo(b)),
        (double or float, _) or (_, double or float) when IsNumber(x) && IsNumber(y) => CompareBinary(ToDouble(x), ToDouble(y)),
        _ when IsNumber(x) && IsNumber(y) => CompareExact(ExactOf(x), ExactOf(y)),
        _ => Math.Sign(string.CompareOrdinal(Convert.ToString(x, CultureInfo.InvariantCulture), Convert.ToString(y, CultureInfo.InvariantCulture))),
    };

    private static bool IsNumber(object value) =>
        value is BigNumber or decimal or double or float or long or int or short or byte or sbyte or ulong or uint or ushort;

    private static double ToDouble(object value) => value is BigNumber big
        ? double.Parse(big.Invariant, NumberStyles.Float, CultureInfo.InvariantCulture)
        : Convert.ToDouble(value, CultureInfo.InvariantCulture);

    /// <summary>BINARY_DOUBLE/FLOAT: Oracle sorts NaN above infinity.</summary>
    private static int CompareBinary(double a, double b) => (double.IsNaN(a), double.IsNaN(b)) switch
    {
        (true, true) => 0,
        (true, false) => 1,
        (false, true) => -1,
        _ => a.CompareTo(b),
    };

    /// <summary>A number as unscaled digits and the count of decimal places (negative for an exponent beyond them).</summary>
    private static (BigInteger Unscaled, int Scale) ExactOf(object value)
    {
        var text = value is BigNumber big ? big.Invariant : Convert.ToString(value, CultureInfo.InvariantCulture)!;
        var exponent = 0;
        if (text.IndexOfAny(['E', 'e']) is >= 0 and var e)
        {
            exponent = int.Parse(text.AsSpan(e + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            text = text[..e];
        }

        var scale = 0;
        if (text.IndexOf('.', StringComparison.Ordinal) is >= 0 and var dot)
        {
            scale = text.Length - dot - 1;
            text = text.Remove(dot, 1);
        }

        return (BigInteger.Parse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture), scale - exponent);
    }

    private static int CompareExact((BigInteger Unscaled, int Scale) a, (BigInteger Unscaled, int Scale) b)
    {
        var scale = Math.Max(a.Scale, b.Scale);
        return (a.Unscaled * BigInteger.Pow(10, scale - a.Scale)).CompareTo(b.Unscaled * BigInteger.Pow(10, scale - b.Scale));
    }
}
