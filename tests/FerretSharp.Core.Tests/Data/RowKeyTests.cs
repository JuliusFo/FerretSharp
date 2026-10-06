using FerretSharp.Core.Data;

namespace FerretSharp.Core.Tests.Data;

public class RowKeyTests
{
    [Fact]
    public void Primary_keys_compare_by_value()
    {
        RowKey a = new RowKey.PrimaryKey([1m, "X"]);
        RowKey b = new RowKey.PrimaryKey(new List<object?> { 1m, "X" });

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, new RowKey.PrimaryKey([2m, "X"]));
        Assert.NotEqual(a, new RowKey.RowId("AAABBB"));
    }

    [Fact]
    public void Raw_primary_keys_compare_by_content()
    {
        // RAW(16) GUIDs come back as a new byte[] on every read; the tracker must still find the row after F5.
        RowKey a = new RowKey.PrimaryKey([new byte[] { 1, 2, 3 }, 1m]);
        RowKey b = new RowKey.PrimaryKey([new byte[] { 1, 2, 3 }, 1m]);

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, new RowKey.PrimaryKey([new byte[] { 1, 2, 4 }, 1m]));
        Assert.NotEqual(a, new RowKey.PrimaryKey([null, 1m]));
    }
}
