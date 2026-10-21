using FerretSharp.Core.Data;
using FerretSharp.Core.Forms;
using FerretSharp.UI.State;

namespace FerretSharp.UI.Tests.State;

/// <summary>"Compare selected" for the table and the result (review 3.44.0, B1: one rule instead of two copies).</summary>
public sealed class RowCompareRequestTests
{
    private static FormRow? Loaded(int index) =>
        index < 3 ? new FormRow(new RowData(new RowKey.PrimaryKey([(decimal)index]), [(decimal)index]), null) : null;

    [Fact]
    public void Loaded_rows_are_compared_and_the_others_counted()
    {
        var (request, refusal) = RowCompareRequest.Of([0, 2, 5], Loaded);

        Assert.Null(refusal);
        Assert.Equal([0, 2], request!.Rows.Select(r => r.Index));
        Assert.Equal(1, request.Missing);
    }

    [Fact]
    public void Fewer_than_two_loaded_rows_or_too_many_are_refused()
    {
        Assert.Equal((null, "Zum Vergleichen mindestens zwei geladene Zeilen markieren."), RowCompareRequest.Of([1, 7], Loaded));
        var (request, refusal) = RowCompareRequest.Of([.. Enumerable.Range(0, RowComparison.MaxRows + 1)], Loaded);
        Assert.Null(request);
        Assert.Contains(RowComparison.MaxRows.ToString(System.Globalization.CultureInfo.InvariantCulture), refusal);
    }
}
