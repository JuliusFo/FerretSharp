using FerretSharp.Core.AppTrace;
using FerretSharp.Core.Connections;

namespace FerretSharp.Core.Tests.AppTrace;

/// <summary>WP-33: a traced app is matched to a connection by its environment, else by its data source.</summary>
public sealed class AppConnectionMatchTests
{
    private static ConnectionProfile HostPort(string name, string host, int port, string? service, string? sid = null, string user = "app", string? environment = null) =>
        new(Guid.NewGuid(), name, ConnectionKind.Test, new HostPortAddress(host, port, service, sid), user, null, false, AppEnvironment: environment);

    private static ConnectionProfile Tns(string name, string alias, string? environment = null) =>
        new(Guid.NewGuid(), name, ConnectionKind.Prod, new TnsAliasAddress(alias, null), "reader", null, true, AppEnvironment: environment);

    private static readonly ConnectionProfile ErpDev = HostPort("ERP Dev", "db-dev", 1521, "ERPDEV", environment: "erp-dev");
    private static readonly ConnectionProfile ErpTest = HostPort("ERP Test", "db-test", 1531, "ERPTEST");
    private static readonly ConnectionProfile ErpTestReader = HostPort("ERP Test (reader)", "DB-TEST", 1531, "erptest", user: "reader");
    private static readonly ConnectionProfile ErpProd = Tns("ERP Prod", "ERP_PROD", environment: "erp-prod");
    private static readonly ConnectionProfile Legacy = HostPort("Legacy", "old-db", 1521, null, sid: "ORCL");
    private static readonly ConnectionProfile[] All = [ErpDev, ErpTest, ErpTestReader, ErpProd, Legacy];

    [Theory]
    [InlineData("erp-dev")]
    [InlineData("ERP-Dev")]
    [InlineData(" erp-dev ")]
    public void The_environment_wins(string environment)
    {
        var match = AppConnectionMatch.Find(All, environment, "db-test:1531/ERPTEST");

        Assert.Equal((ErpDev, MatchKind.Environment), (match.Profile, match.Kind));
    }

    [Theory]
    [InlineData("ERP_PROD", "ERP Prod")]
    [InlineData("erp_prod", "ERP Prod")]
    [InlineData("db-dev:1521/ERPDEV", "ERP Dev")]
    [InlineData("//db-dev/erpdev", "ERP Dev")] // default port
    [InlineData("db-dev:1521/ERPDEV:dedicated", "ERP Dev")]
    [InlineData("(DESCRIPTION=(ADDRESS=(PROTOCOL=TCP)(HOST=old-db)(PORT=1521))(CONNECT_DATA=(SID=ORCL)))", "Legacy")]
    [InlineData("(DESCRIPTION = (ADDRESS = (PROTOCOL = TCP)(HOST = db-dev)(PORT = 1521))(CONNECT_DATA = (SERVICE_NAME = ERPDEV)))", "ERP Dev")]
    public void Without_a_known_environment_the_data_source_decides(string dataSource, string expected)
    {
        var match = AppConnectionMatch.Find(All, "unknown-env", dataSource);

        Assert.Equal((expected, MatchKind.DataSource), (match.Profile?.Name, match.Kind));
    }

    [Fact]
    public void Several_connections_on_one_database_are_candidates()
    {
        var match = AppConnectionMatch.Find(All, null, "db-test:1531/ERPTEST");

        Assert.Null(match.Profile);
        Assert.Equal(MatchKind.None, match.Kind);
        Assert.Equal([ErpTest, ErpTestReader], match.Candidates);
    }

    [Fact]
    public void Two_connections_with_the_same_environment_are_candidates()
    {
        var twin = ErpDev with { Id = Guid.NewGuid(), Name = "ERP Dev 2" };

        var match = AppConnectionMatch.Find([ErpDev, twin, ErpTest], "erp-dev", "db-test:1531/ERPTEST");

        Assert.Equal((null, 2), (match.Profile, match.Candidates.Count)); // the environment is meant, not the data source
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("nope", "db-x:1521/OTHER")]
    [InlineData(null, "db-test:1521/ERPTEST")] // other port
    [InlineData(null, "ERP_TEST")]
    public void Nothing_fits(string? environment, string? dataSource) =>
        Assert.Same(AppConnectionMatch.NoMatch, AppConnectionMatch.Find(All, environment, dataSource));

    [Fact]
    public void A_connection_without_service_does_not_match_a_service()
    {
        Assert.False(DataSourceAddress.Parse("old-db:1521/ORCL")!.Matches(Legacy.Address));
        Assert.Equal(new DataSourceAddress(null, 0, null, null, "ERP_PROD"), DataSourceAddress.Parse("ERP_PROD"));
        Assert.Null(DataSourceAddress.Parse("  "));
        Assert.Null(DataSourceAddress.Parse("(DESCRIPTION=(ADDRESS_LIST=))")); // no host
    }
}
