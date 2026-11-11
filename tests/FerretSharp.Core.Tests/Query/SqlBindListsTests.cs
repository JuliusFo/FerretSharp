using FerretSharp.Core.Query;

namespace FerretSharp.Core.Tests.Query;

/// <summary>WP-84: a list parameter spread over one bind per value – only where it stands alone in IN ( … ).</summary>
public sealed class SqlBindListsTests
{
    [Fact]
    public void Each_use_in_an_in_list_becomes_one_bind_per_value()
    {
        var list = SqlBindLists.Expand(
            "SELECT * FROM orders WHERE customer_id IN (:ids);\nSELECT * FROM invoice WHERE customer_id NOT IN ( :IDS ) -- :ids in a comment\n  AND note <> ':ids'",
            "ids", 3, out var refusal);

        Assert.Null(refusal);
        Assert.NotNull(list);
        Assert.Equal(["ids_1", "ids_2", "ids_3"], list.Names);
        Assert.Equal(
            "SELECT * FROM orders WHERE customer_id IN (:ids_1, :ids_2, :ids_3);\nSELECT * FROM invoice WHERE customer_id NOT IN ( :ids_1, :ids_2, :ids_3 ) -- :ids in a comment\n  AND note <> ':ids'",
            list.Text);
    }

    [Theory]
    [InlineData("SELECT * FROM t WHERE id = :ids")]
    [InlineData("SELECT * FROM t WHERE id IN (:ids, 4)")]
    [InlineData("SELECT * FROM t WHERE id IN (1, :ids)")]
    [InlineData("SELECT f(:ids) FROM t WHERE id IN (:ids)")]
    public void A_use_outside_an_in_list_of_its_own_is_refused(string script)
    {
        Assert.Null(SqlBindLists.Expand(script, "ids", 2, out var refusal));
        Assert.Contains(":ids", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void A_script_without_the_bind_and_too_many_values_are_refused()
    {
        Assert.Null(SqlBindLists.Expand("SELECT 1 FROM dual", "ids", 2, out var missing));
        Assert.NotNull(missing);
        Assert.Null(SqlBindLists.Expand("SELECT * FROM t WHERE id IN (:ids)", "ids", SqlBindLists.MaxValues + 1, out var tooMany));
        Assert.Contains("1000", tooMany, StringComparison.Ordinal);
        Assert.NotNull(SqlBindLists.Expand("SELECT * FROM t WHERE id IN (:ids)", "ids", SqlBindLists.MaxValues, out _));
    }

    [Fact]
    public void New_names_avoid_binds_the_script_has_and_stay_short_enough_for_oracle()
    {
        var taken = SqlBindLists.Expand("SELECT * FROM t WHERE id IN (:ids) AND x = :ids_2", "ids", 2, out _)!;
        var quoted = SqlBindLists.Expand("SELECT * FROM t WHERE id IN (:\"my ids\")", "my ids", 2, out _)!;
        var longName = SqlBindLists.Expand("SELECT * FROM t WHERE id IN (:a_rather_long_bind_name_of_30)", "a_rather_long_bind_name_of_30", 12, out _)!;

        Assert.Equal(["v1_1", "v1_2"], taken.Names);
        Assert.Equal(["v_1", "v_2"], quoted.Names);
        Assert.All(longName.Names, n => Assert.True(n.Length <= 30, n));
        Assert.Equal(12, longName.Names.Distinct().Count());
    }

    /// <summary>Review WP-84, S2: with every candidate of the stems taken, naming looped forever (the window froze).</summary>
    [Fact]
    public void Naming_ends_when_the_short_stems_are_taken_too_and_reserved_names_count()
    {
        const string bind = "a_rather_long_bind_name_of_28";
        var taken = string.Join(" AND ", Enumerable.Range(0, 40).Select(i => $"x{i} = :{(i == 0 ? bind[..28] : "v" + i)}_1"));

        var list = SqlBindLists.Expand($"SELECT * FROM t WHERE id IN (:{bind}) AND {taken}", bind, 2, out _)!;
        var reserved = SqlBindLists.Expand("SELECT * FROM t WHERE id IN (:ids)", "ids", 2, out _, ["IDS_1"])!;

        Assert.Equal(["v40_1", "v40_2"], list.Names);
        Assert.Equal(["v1_1", "v1_2"], reserved.Names);
    }

    /// <summary>Review WP-84, M1: many uses in a large script grew the text quadratically, to gigabytes.</summary>
    [Fact]
    public void A_spread_beyond_the_size_limit_is_refused()
    {
        var script = string.Join("\nUNION ALL ", Enumerable.Repeat("SELECT 1 FROM t WHERE id IN (:ids)", 2_000));

        Assert.Null(SqlBindLists.Expand(script, "ids", SqlBindLists.MaxValues, out var refusal));
        Assert.Contains(":ids", refusal, StringComparison.Ordinal);
        var small = SqlBindLists.Expand(script, "ids", 2, out _)!;
        Assert.Equal(2_000, small.Text.Split(":ids_2").Length - 1);
    }
}
