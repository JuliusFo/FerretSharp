using FerretSharp.Core.Connections;
using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;
using FerretSharp.UI.State;

namespace FerretSharp.UI.Tests.State;

/// <summary>WP-70: what the object menu offers to copy and opens in the SQL editor.</summary>
public sealed class ObjectMenuFactsTests
{
    private static readonly TableSummary Kunden = new("APP", "KUNDEN", TableKind.Table);

    private static readonly ConnectionProfile App = new(Guid.NewGuid(), "Test", ConnectionKind.Test, new HostPortAddress("db", 1521, "S", null), "app", null, false);

    [Fact]
    public void Copy_offers_the_known_name_the_real_object_and_the_csharp_names_once_each()
    {
        Assert.Equal(["S_PRODUKT", "STAMM.PRODUKT", "Produkt", "db.Produkte"],
            ObjectMenuFacts.CopyTexts("STAMM", "PRODUKT", "S_PRODUKT", "Produkt", "db.Produkte"));
        Assert.Equal(["KUNDEN", "APP.KUNDEN"], ObjectMenuFacts.CopyTexts("APP", "KUNDEN", "KUNDEN"));
        Assert.Equal(["Kunden", "APP.\"Kunden\""], ObjectMenuFacts.CopyTexts("APP", "Kunden", "Kunden")); // quoted only where code needs it
    }

    /// <summary>Review of WP-70: a schema in the profile does not change the session's – APP's tables need their owner for TECH.</summary>
    [Fact]
    public void Sql_names_the_table_as_the_login_user_finds_it()
    {
        var profile = new ConnectionProfile(Guid.NewGuid(), "Test", ConnectionKind.Test, new HostPortAddress("db", 1521, "S", null), "tech", "APP", false);

        Assert.Equal("TECH", ObjectMenuFacts.SessionSchema(profile));
        Assert.Equal(("SELECT *\n  FROM APP.KUNDEN;\n", []), ObjectMenuFacts.SqlOf(Kunden, profile));
        var viaPublic = new TableSummary("STAMM", "PRODUKT", TableKind.Table, new SynonymInfo(SynonymInfo.PublicOwner, "S_PRODUKT"));
        Assert.Equal("SELECT *\n  FROM STAMM.PRODUKT;\n", ObjectMenuFacts.SqlOf(viaPublic, profile).Sql); // TECH could have its own S_PRODUKT
    }

    /// <summary>Review of WP-70: from a tab the menu acts on the tab – its filters and sort order, as "SQL" of its filter bar.</summary>
    [Fact]
    public void Sql_of_a_tab_takes_its_filters_once_its_structure_is_known()
    {
        var tab = new TableTab(Guid.NewGuid(), Kunden) { AppliedFilters = [FilterCondition.Of("ID", FilterOperator.Equals, "4711")] };
        Assert.Equal("SELECT *\n  FROM KUNDEN;\n", ObjectMenuFacts.SqlOf(Kunden, App, tab).Sql); // structure not loaded yet

        tab.Details = new TableDetails(Kunden, [new ColumnInfo("ID", "NUMBER", null, false, 10, 0, false, false, null, 1)], ["ID"], [], false);
        var (sql, variables) = ObjectMenuFacts.SqlOf(Kunden, App, tab);

        Assert.StartsWith("SELECT t.*\n  FROM \"APP\".\"KUNDEN\" t\n WHERE ", sql);
        Assert.Single(variables);
        tab.AppliedFilters = [];
        Assert.Equal("SELECT *\n  FROM KUNDEN;\n", ObjectMenuFacts.SqlOf(Kunden, App, tab).Sql); // nothing of the tab's own
    }
}
