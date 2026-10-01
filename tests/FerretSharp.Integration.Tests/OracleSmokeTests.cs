using Oracle.ManagedDataAccess.Client;

namespace FerretSharp.Integration.Tests;

public class OracleSmokeTests(OracleContainerFixture oracle)
{
    [Fact]
    public async Task Select_from_dual_returns_one()
    {
        var ct = TestContext.Current.CancellationToken;

        await using var connection = new OracleConnection(oracle.RequireConnectionString());
        await connection.OpenAsync(ct);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM DUAL";
        var result = await command.ExecuteScalarAsync(ct);

        Assert.Equal(1m, Convert.ToDecimal(result));
    }
}
