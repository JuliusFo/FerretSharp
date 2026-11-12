using FerretSharp.Core.Oracle;
using FerretSharp.Core.ServerLoad;

namespace FerretSharp.Core.Tests.Oracle;

public sealed class OracleServerLoadReaderTests
{
    /// <summary>
    /// Review 3.70.0, A18: the statement info of the server load read with an IN list of every length – up to 100 statements
    /// of their own in the shared pool of the database being watched. The list is filled up to a few fixed sizes.
    /// </summary>
    [Theory]
    [InlineData(1, 1)]
    [InlineData(3, 10)]
    [InlineData(37, 50)]
    [InlineData(ServerLoadMonitor.InfoBatch, 100)]
    public void Statement_info_is_read_with_an_in_list_of_a_fixed_size(int ids, int size)
    {
        var sqlIds = Enumerable.Range(0, ids).Select(i => $"sql{i}").ToList();

        var (sql, parameters) = OracleServerLoadReader.InfoLookup(sqlIds);

        Assert.Equal(size, parameters.Count);
        Assert.Contains($":id{size - 1})", sql, StringComparison.Ordinal);
        Assert.Equal(sqlIds, parameters.Select(p => (string)p.Value!).Distinct());
    }
}
