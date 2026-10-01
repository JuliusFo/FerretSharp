using FerretSharp.Core.Connections;
using FerretSharp.Core.Oracle;

namespace FerretSharp.Integration.Tests;

public class OracleConnectionTesterTests(OracleContainerFixture oracle)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Valid_credentials_succeed_with_server_version()
    {
        var (profile, password) = oracle.RequireProfile();

        var result = await new OracleConnectionTester().TestAsync(profile, password, Ct);

        Assert.True(result.Success, result.Message);
        Assert.False(string.IsNullOrEmpty(result.ServerVersion));
    }

    [Fact]
    public async Task Wrong_password_reports_ora_01017()
    {
        var (profile, _) = oracle.RequireProfile();

        var result = await new OracleConnectionTester().TestAsync(profile, "definitely-wrong", Ct);

        Assert.False(result.Success);
        Assert.Equal("ORA-01017", result.ErrorCode);
    }

    [Fact]
    public async Task Unknown_service_fails_with_oracle_error()
    {
        var (profile, password) = oracle.RequireProfile();
        var address = (HostPortAddress)profile.Address;
        profile = profile with { Address = address with { ServiceName = "NO_SUCH_SERVICE" } };

        var result = await new OracleConnectionTester().TestAsync(profile, password, Ct);

        Assert.False(result.Success);
        Assert.NotNull(result.ErrorCode);
    }
}
