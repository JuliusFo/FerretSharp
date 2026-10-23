using FerretSharp.Core.Data;
using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Tests.Data;

/// <summary>Loaded result rows sorted in memory as Oracle sorts them in a FerretSharp session (NLS_SORT=BINARY).</summary>
public sealed class RowOrderTests
{
    private static RowData Row(params object?[] values) => new(RowKey.None.Instance, values);

    private static IReadOnlyList<object?> Sorted(IReadOnlyList<object?> values, bool descending = false) =>
        [.. RowOrder.Sort([.. values.Select(v => Row(v))], [new ResultSort(0, descending)]).Select(r => r.Values[0])];

    [Fact]
    public void Null_comes_last_ascending_and_first_descending()
    {
        Assert.Equal([1m, 2m, null], Sorted([2m, null, 1m]));
        Assert.Equal([null, 2m, 1m], Sorted([2m, null, 1m], descending: true));
    }

    /// <summary>BINARY, not GERMAN: digits before capitals before small letters, umlauts after z.</summary>
    [Fact]
    public void Text_sorts_by_its_characters()
    {
        Assert.Equal(["10", "9", "Zander", "apfel", "zebra", "Äpfel"], Sorted(["zebra", "Äpfel", "apfel", "Zander", "9", "10"]));
    }

    [Fact]
    public void Numbers_sort_exactly_also_beyond_decimal()
    {
        var big = new BigNumber("12345678901234567890123456789012345678");
        var bigger = new BigNumber("12345678901234567890123456789012345679");
        var tiny = new BigNumber("0.00000000000000000000000000000001");

        Assert.Equal([-5m, 0m, tiny, 1.5m, 2, big, bigger], Sorted([bigger, 1.5m, big, 2, tiny, 0m, -5m]));
        Assert.Equal([new BigNumber("-1E+40"), 3m, new BigNumber("2.5E+30")], Sorted([new BigNumber("2.5E+30"), 3m, new BigNumber("-1E+40")]));
    }

    [Fact]
    public void Binary_doubles_put_nan_above_infinity()
    {
        Assert.Equal([double.NegativeInfinity, -1.5, 2.0, double.PositiveInfinity, double.NaN],
            Sorted([double.NaN, 2.0, double.PositiveInfinity, -1.5, double.NegativeInfinity]));
    }

    [Fact]
    public void Times_with_zone_sort_by_their_instant_raw_by_its_bytes()
    {
        var early = new DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.FromHours(2)); // 10:00 UTC
        var late = new DateTimeOffset(2026, 10, 23, 11, 0, 0, TimeSpan.Zero);

        Assert.Equal([early, late], Sorted([late, early]));
        Assert.Equal([new byte[] { 0x01 }, new byte[] { 0x01, 0x00 }, new byte[] { 0xFF }], Sorted([new byte[] { 0xFF }, new byte[] { 0x01, 0x00 }, new byte[] { 0x01 }]));
        Assert.Equal([false, true], Sorted([true, false]));
    }

    [Fact]
    public void Equal_rows_keep_their_order_and_later_keys_break_ties()
    {
        var rows = new[] { Row("B", 1m), Row("A", 2m), Row("B", 3m), Row("A", 4m) };

        Assert.Equal([rows[1], rows[3], rows[0], rows[2]], RowOrder.Sort(rows, [new ResultSort(0, false)]));
        Assert.Equal([rows[3], rows[1], rows[2], rows[0]], RowOrder.Sort(rows, [new ResultSort(0, false), new ResultSort(1, true)]));
    }

    [Theory]
    [InlineData("VARCHAR2", true)]
    [InlineData("NUMBER", true)]
    [InlineData("DATE", true)]
    [InlineData("TIMESTAMP(6) WITH TIME ZONE", true)]
    [InlineData("RAW", true)]
    [InlineData("INTERVAL DAY(2) TO SECOND(6)", false)]
    [InlineData("CLOB", false)]
    [InlineData("XMLTYPE", false)]
    public void Only_values_that_compare_like_in_oracle_sort_in_memory(string type, bool sorts)
    {
        Assert.Equal(sorts, RowOrder.CanSort(new ColumnInfo("C", type, null, true, null, null, false, false, null, 1)));
    }
}
