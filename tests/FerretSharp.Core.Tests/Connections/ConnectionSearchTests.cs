using FerretSharp.Core.Connections;

namespace FerretSharp.Core.Tests.Connections;

public class ConnectionSearchTests
{
    private static ConnectionProfile Make(string name, ConnectionKind kind, string? group, string host = "db01", string user = "app") => new(
        Guid.NewGuid(), name, kind, new HostPortAddress(host, 1521, "SVC", null), user, null, false, group);

    private static readonly ConnectionProfile ErpProd = Make("ERP Prod", ConnectionKind.Prod, "ERP", host: "prod-db");
    private static readonly ConnectionProfile ErpDev = Make("ERP Dev", ConnectionKind.Dev, "ERP");
    private static readonly ConnectionProfile ErpTest = Make("ERP Test", ConnectionKind.Test, "erp ");
    private static readonly ConnectionProfile KasseTest = Make("Kasse Test", ConnectionKind.Test, "Kasse", user: "kasse_reader");
    private static readonly ConnectionProfile Loose = Make("Spielwiese", ConnectionKind.Other, null);
    private static readonly ConnectionProfile[] All = [Loose, KasseTest, ErpProd, ErpTest, ErpDev];

    [Fact]
    public void Groups_alphabetically_with_ungrouped_last_and_merges_case_variants()
    {
        var groups = ConnectionSearch.Group(All);

        Assert.Equal(["ERP", "Kasse", null], groups.Select(g => g.Name));
        Assert.Equal(3, groups[0].Profiles.Count);
    }

    [Fact]
    public void Orders_by_kind_then_name_within_a_group()
    {
        var erp = ConnectionSearch.Group(All)[0];

        Assert.Equal(["ERP Dev", "ERP Test", "ERP Prod"], erp.Profiles.Select(p => p.Name));
    }

    [Theory]
    [InlineData("kasse", "Kasse Test")]
    [InlineData("prod-db", "ERP Prod")]
    [InlineData("KASSE_READER", "Kasse Test")]
    [InlineData("erp prod", "ERP Prod")]
    [InlineData("spiel", "Spielwiese")]
    public void Search_matches_name_group_address_user_and_requires_all_terms(string query, string expected)
    {
        var hit = Assert.Single(ConnectionSearch.Group(All, query).SelectMany(g => g.Profiles));

        Assert.Equal(expected, hit.Name);
    }

    [Fact]
    public void Group_names_are_distinct_trimmed_and_sorted()
    {
        Assert.Equal(["ERP", "Kasse"], ConnectionSearch.GroupNames(All));
    }
}
