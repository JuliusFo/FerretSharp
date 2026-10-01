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
}
