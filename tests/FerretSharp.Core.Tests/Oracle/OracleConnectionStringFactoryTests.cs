using FerretSharp.Core.Connections;
using FerretSharp.Core.Oracle;
using FerretSharp.Core.Tests.Connections;
using Oracle.ManagedDataAccess.Client;

namespace FerretSharp.Core.Tests.Oracle;

public class OracleConnectionStringFactoryTests
{
    private static readonly Func<string, string?> NoEnvironment = _ => null;

    [Fact]
    public void Service_name_uses_ez_connect()
    {
        var cs = Parse(OracleConnectionStringFactory.Create(TestProfiles.HostPort(), "pw", getEnvironmentVariable: NoEnvironment));

        Assert.Equal("db01.example.com:1521/ORCLPDB", cs.DataSource);
        Assert.Equal("app_user", cs.UserID);
    }

    [Fact]
    public void Sid_uses_connect_descriptor()
    {
        var profile = TestProfiles.HostPort(service: null, sid: "ORCL");

        var cs = Parse(OracleConnectionStringFactory.Create(profile, "pw", getEnvironmentVariable: NoEnvironment));

        Assert.Equal("(DESCRIPTION=(ADDRESS=(PROTOCOL=TCP)(HOST=db01.example.com)(PORT=1521))(CONNECT_DATA=(SID=ORCL)))", cs.DataSource);
    }

    [Fact]
    public void Pooling_is_always_disabled()
    {
        var cs = Parse(OracleConnectionStringFactory.Create(TestProfiles.HostPort(), "pw", getEnvironmentVariable: NoEnvironment));

        Assert.False(cs.Pooling);
        Assert.Equal(OracleConnectionStringFactory.DefaultConnectTimeoutSeconds, cs.ConnectionTimeout);
    }

    [Theory]
    [InlineData("simple")]
    [InlineData("with;semicolon")]
    [InlineData("with\"quote")]
    [InlineData("with'single=and spaces ")]
    public void Passwords_with_special_characters_round_trip(string password)
    {
        var cs = Parse(OracleConnectionStringFactory.Create(TestProfiles.HostPort(), password, getEnvironmentVariable: NoEnvironment));

        Assert.Equal(password, cs.Password);
    }

    [Fact]
    public void Tns_alias_uses_profile_tns_admin_path()
    {
        var connectionString = OracleConnectionStringFactory.Create(TestProfiles.Tns(), "pw", getEnvironmentVariable: NoEnvironment);

        var cs = Parse(connectionString);
        Assert.Equal("ERP_PROD", cs.DataSource);
        Assert.Equal(@"C:\oracle\network\admin", cs["Tns_Admin"]);
    }

    [Fact]
    public void Tns_alias_falls_back_to_environment_variable()
    {
        var connectionString = OracleConnectionStringFactory.Create(
            TestProfiles.Tns(tnsAdmin: null), "pw", getEnvironmentVariable: name => name == "TNS_ADMIN" ? @"D:\tns" : null);

        Assert.Equal(@"D:\tns", Parse(connectionString)["Tns_Admin"]);
    }

    [Fact]
    public void Tns_alias_without_any_tns_admin_is_a_configuration_error()
    {
        var ex = Assert.Throws<ConnectionConfigurationException>(
            () => OracleConnectionStringFactory.Create(TestProfiles.Tns(tnsAdmin: null), "pw", getEnvironmentVariable: NoEnvironment));

        Assert.Contains("TNS_ADMIN", ex.Message);
    }

    [Fact]
    public void Host_port_address_without_service_or_sid_is_a_configuration_error()
    {
        var profile = TestProfiles.HostPort() with { Address = new HostPortAddress("db", 1521, null, null) };

        Assert.Throws<ConnectionConfigurationException>(() => OracleConnectionStringFactory.Create(profile, "pw"));
    }

    private static OracleConnectionStringBuilder Parse(string connectionString) => new(connectionString);
}
