using FerretSharp.Core.Connections;

namespace FerretSharp.Core.Tests.Connections;

public class ConnectionProfileValidatorTests
{
    [Fact]
    public void Valid_profiles_have_no_errors()
    {
        Assert.Empty(ConnectionProfileValidator.Validate(TestProfiles.HostPort()));
        Assert.Empty(ConnectionProfileValidator.Validate(TestProfiles.HostPort(service: null, sid: "ORCL")));
        Assert.Empty(ConnectionProfileValidator.Validate(TestProfiles.Tns(tnsAdmin: null)));
    }

    [Fact]
    public void Reports_missing_name_user_and_host()
    {
        var profile = TestProfiles.HostPort() with { Name = " ", User = "", Address = new HostPortAddress("", 1521, "SVC", null) };

        var errors = ConnectionProfileValidator.Validate(profile);

        Assert.Contains(ConnectionField.Name, errors.Keys);
        Assert.Contains(ConnectionField.User, errors.Keys);
        Assert.Contains(ConnectionField.Host, errors.Keys);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65536)]
    public void Rejects_out_of_range_port(int port)
    {
        var profile = TestProfiles.HostPort() with { Address = new HostPortAddress("db", port, "SVC", null) };

        Assert.Contains(ConnectionField.Port, ConnectionProfileValidator.Validate(profile).Keys);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("SVC", "SID")]
    public void Requires_exactly_one_of_service_and_sid(string? service, string? sid)
    {
        var profile = TestProfiles.HostPort() with { Address = new HostPortAddress("db", 1521, service, sid) };

        Assert.Contains(ConnectionField.ServiceOrSid, ConnectionProfileValidator.Validate(profile).Keys);
    }

    [Fact]
    public void Requires_tns_alias()
    {
        Assert.Contains(ConnectionField.Alias, ConnectionProfileValidator.Validate(TestProfiles.Tns(alias: "")).Keys);
    }

    [Fact]
    public void Effective_schema_falls_back_to_upper_cased_user()
    {
        Assert.Equal("APP_USER", TestProfiles.HostPort().EffectiveSchema);
        Assert.Equal("ERP", TestProfiles.Tns().EffectiveSchema);
    }
}
