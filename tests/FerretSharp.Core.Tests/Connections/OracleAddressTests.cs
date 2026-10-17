using FerretSharp.Core.Connections;
using FerretSharp.Core.Oracle;

namespace FerretSharp.Core.Tests.Connections;

/// <summary>R5b (review 3.31.0, B6): one parser and one comparison for addresses (traced apps, import).</summary>
public sealed class OracleAddressTests
{
    /// <summary>What FerretSharp writes as data source reads back as the same address – what a traced app reports of it.</summary>
    [Theory]
    [InlineData("db01", 1521, "ERP", null)]
    [InlineData("db01", 1531, null, "ORCL")]
    public void The_data_source_of_a_profile_reads_back_as_its_address(string host, int port, string? service, string? sid)
    {
        var address = new HostPortAddress(host, port, service, sid);

        Assert.True(OracleAddress.Parse(OracleConnectionStringFactory.DataSourceFor(address))!.SameAs(address));
    }

    /// <summary>R6b (review 3.40.0, B11): one text for the connection and the link – EZConnect has no SID, so a descriptor.</summary>
    [Fact]
    public void The_data_source_is_ezconnect_with_a_service_a_descriptor_with_a_sid_else_the_alias()
    {
        Assert.Equal("db01:1521/ERP", new HostPortAddress(" db01 ", 1521, "ERP ", null).ToDataSource());
        Assert.Equal("(DESCRIPTION=(ADDRESS=(PROTOCOL=TCP)(HOST=db01)(PORT=1531))(CONNECT_DATA=(SID=ORCL)))", new HostPortAddress("db01", 1531, null, "ORCL").ToDataSource());
        Assert.Equal("ERP_PROD", new TnsAliasAddress(" ERP_PROD", @"C:\oracle").ToDataSource());
        Assert.Null(new HostPortAddress("db01", 1521, "", null).ToDataSource());
    }

    [Theory]
    [InlineData("db01:1521/ERP", "db01", 1521, "ERP")]
    [InlineData("//db01/ERP", "db01", 1521, "ERP")]
    [InlineData("db01:1522/ERP:dedicated", "db01", 1522, "ERP")]
    [InlineData("//db01:1523", "db01", 1523, null)]
    public void EzConnect(string text, string host, int port, string? service) =>
        Assert.Equal(new HostPortAddress(host, port, service, null), OracleAddress.Parse(text));

    [Fact]
    public void A_bare_name_is_a_tns_alias() => Assert.Equal(new TnsAliasAddress("ERP_PROD", null), OracleAddress.Parse(" ERP_PROD "));
}
