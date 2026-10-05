using FerretSharp.Core.Query;

namespace FerretSharp.Core.Tests.Query;

public sealed class ExecutionPlanTests
{
    [Fact]
    public void The_sql_id_is_the_one_oracle_gives_the_text()
    {
        // V$SQL in Oracle 23 Free for exactly this text (spike of WP-14).
        const string sql = "SELECT /*+ GATHER_PLAN_STATISTICS */ \"ID\", \"N\" FROM \"PS_T\" WHERE \"ID\" > :p_0 ORDER BY \"ID\" OFFSET :p_offset ROWS FETCH NEXT :p_limit ROWS ONLY";

        Assert.Equal("aubfwnuqbygmt", Plans.SqlId(sql));
        Assert.NotEqual(Plans.SqlId(sql), Plans.SqlId(sql + " "));
    }

    [Theory]
    [InlineData("SELECT t.ROWID FROM \"KUNDEN\" t", "SELECT /*+ GATHER_PLAN_STATISTICS */ t.ROWID FROM \"KUNDEN\" t")]
    [InlineData("select x from y", "select /*+ GATHER_PLAN_STATISTICS */ x from y")]
    [InlineData("SELECT /*+ INDEX(t PK_T) */ * FROM T t", "SELECT /*+ GATHER_PLAN_STATISTICS INDEX(t PK_T) */ * FROM T t")]
    [InlineData("WITH a AS (SELECT 1 x FROM DUAL) SELECT x FROM a", "WITH a AS (SELECT /*+ GATHER_PLAN_STATISTICS */ 1 x FROM DUAL) SELECT x FROM a")]
    public void The_statistics_hint_goes_after_the_first_select_joining_an_existing_hint(string sql, string expected) =>
        Assert.Equal(expected, Plans.WithStatistics(sql));

    private static PlanStep Step(int id, long? rows, long? actual, long starts = 1) =>
        new(id, id == 0 ? null : 0, id == 0 ? 0 : 1, "TABLE ACCESS", "FULL", "APP", "T", 10, rows, null, null, null, actual, starts);

    [Fact]
    public void Misestimates_are_marked_both_ways_only_for_the_whole_result()
    {
        var whole = new ExecutionPlan(PlanSource.Actual, "", [], WholeResult: true);
        var firstPage = whole with { WholeResult = false };

        Assert.True(whole.IsMisestimate(Step(1, rows: 10, actual: 5000)));   // underestimated
        Assert.True(whole.IsMisestimate(Step(1, rows: 5000, actual: 10)));   // overestimated
        Assert.True(firstPage.IsMisestimate(Step(1, rows: 10, actual: 5000)));
        Assert.False(firstPage.IsMisestimate(Step(1, rows: 5000, actual: 10))); // fetching stopped after the first page
        Assert.False(whole.IsMisestimate(Step(1, rows: 2, actual: 50)));    // too few rows to matter
        Assert.False(whole.IsMisestimate(Step(1, rows: 100, actual: 990, starts: 10))); // per start: 1000 expected in total
        Assert.False(new ExecutionPlan(PlanSource.Estimated, "", []).IsMisestimate(Step(1, 10, 5000)));
    }

    [Fact]
    public void The_text_form_looks_like_dbms_xplan_with_the_predicates()
    {
        var plan = new ExecutionPlan(PlanSource.Actual, "SELECT …",
        [
            new PlanStep(0, null, 0, "SELECT STATEMENT", null, null, null, 4, null, null, null, null, 5, 1, TimeSpan.FromMilliseconds(2.27), 7),
            new PlanStep(1, 0, 1, "TABLE ACCESS", "FULL", "APP", "PS_T", 4, 990, 19800, null, "\"ID\">10", 990, 1, TimeSpan.FromMilliseconds(0.03), 7),
        ], SqlId: "aubfwnuqbygmt", ChildNumber: 0, WholeResult: true, RowsFetched: 5);

        var text = Plans.Format(plan);

        Assert.StartsWith("SQL_ID aubfwnuqbygmt, child number 0", text);
        Assert.Contains("| Id | Operation          | Name | Starts | E-Rows | A-Rows | A-Time      | Buffers | Cost |", text);
        Assert.Contains("| *1 |  TABLE ACCESS FULL | PS_T |      1 |    990 |    990 | 00:00:00.00 |       7 |    4 |", text);
        Assert.EndsWith("   1 - filter(\"ID\">10)", text);
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData(99_999L, "99999")]
    [InlineData(123_456L, "123K")]
    [InlineData(250_000_000L, "250M")]
    public void Large_numbers_are_shortened(long? value, string expected) => Assert.Equal(expected, Plans.Number(value));
}
