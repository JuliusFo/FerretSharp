using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Tests.Schema;

public class TablespacesTests
{
    private static Tablespaces Of(TablespaceUse[] table, TablespaceUse[] schema, string? @default = "USERS") =>
        new(["DATA", "INDX", "USERS"], @default, null, table, schema, [new("DATA", 30), new("USERS", 2)]);

    private static TablespaceUse[] Uses(params (string Name, int Count)[] uses) => uses.Select(u => new TablespaceUse(u.Name, u.Count)).ToArray();

    [Fact]
    public void Indexes_follow_the_table_s_indexes_and_only_without_any_the_schema_s()
    {
        Assert.Equal(new TablespaceSuggestion("INDX", 3, 4, TablespaceBasis.TableIndexes), Of(Uses(("INDX", 3), ("USERS", 1)), Uses(("DATA", 99))).ForIndexes());
        Assert.Equal(new TablespaceSuggestion("INDX", 12, 14, TablespaceBasis.SchemaIndexes), Of([], Uses(("INDX", 12), ("USERS", 2))).ForIndexes());

        // The table's indexes lie in the default: the field's empty start says that already, the schema does not overrule it.
        Assert.Null(Of(Uses(("USERS", 2)), Uses(("INDX", 12))).ForIndexes());
    }

    [Fact]
    public void A_table_follows_the_schema_s_tables()
    {
        Assert.Equal(new TablespaceSuggestion("DATA", 30, 32, TablespaceBasis.SchemaTables), Of([], []).ForTable());
        Assert.Null(Of([], [], @default: "DATA").ForTable());
    }

    [Fact]
    public void Ties_go_by_name_and_a_tablespace_no_longer_to_choose_is_no_suggestion()
    {
        Assert.Equal("DATA", Of([], Uses(("INDX", 5), ("DATA", 5))).ForIndexes()?.Name);
        Assert.Null(Of([], Uses(("OLD_IDX", 9))).ForIndexes()); // offline, read-only or gone
        Assert.Equal("INDX", Of([], Uses(("INDX", 1)), @default: null).ForIndexes()?.Name); // another schema: its default is unknown
    }

    [Fact]
    public void Only_a_known_quota_warns()
    {
        var quota = Of([], []) with { Quota = new HashSet<string> { "USERS" } };

        Assert.True(quota.LacksQuota("INDX"));
        Assert.False(quota.LacksQuota("USERS"));
        Assert.False(quota.LacksQuota(null));
        Assert.False(Of([], []).LacksQuota("INDX"));
    }
}
