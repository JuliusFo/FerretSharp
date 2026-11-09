using FerretSharp.Core.Oracle;
using FerretSharp.Core.Query;
using FerretSharp.Core.Resources;
using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Tests.Query.SqlEditor;

/// <summary>The condition of a table tab (WP-79): what may stand after WHERE, so it stays a condition of the grid's statement.</summary>
public class WhereConditionTests
{
    /// <summary>
    /// Conditions as colleagues write them in SQL Developer: an IN on another table with another IN in it, whatever ends a WHERE
    /// inside parentheses, ; and ) in literals, a line comment at the end, and words that end a WHERE only as keywords.
    /// </summary>
    [Theory]
    [InlineData("STATUS = 'OPEN'")]
    [InlineData("KUNDE_ID IN (SELECT KUNDE_ID FROM AUFTRAG WHERE POS_ID IN (SELECT POS_ID FROM POSITION WHERE MENGE > 3))")]
    [InlineData("ID IN (SELECT ID FROM A UNION SELECT ID FROM B)")]
    [InlineData("ID = (SELECT MAX(ID) FROM A GROUP BY X HAVING COUNT(*) > 1 ORDER BY 1 FETCH FIRST 1 ROW ONLY)")]
    [InlineData("NAME = 'a;b)' -- why: see ticket")]
    [InlineData("NAME = q'[it's ; ( fine]'")]
    [InlineData("\"Mixed Case\" IS NOT NULL /* closed */")]
    [InlineData("OFFSET > 5 AND FETCH = 'x' AND MODEL = 1")]
    [InlineData("A = 1\nOR B = 2")]
    [InlineData("ID IN (SELECT ID FROM (SELECT * FROM T PIVOT (MAX(V) FOR K IN ('a' AS A))))")] // FOR without UPDATE
    public void A_condition_that_stays_a_condition_passes(string condition)
    {
        Assert.Null(WhereCondition.Problem(condition));

        // What the check lets through, the session's read-only tripwire takes as well: it would throw – a programming error to
        // it – and break the tab (review of WP-79).
        var query = QueryBuilder.BuildSelect(OneColumn, RowFilter.Where(condition), [], new PageSpec(0, 500));
        Assert.True(OracleSession.IsReadOnlyStatement(query.Sql), query.Sql);
    }

    private static readonly TableDetails OneColumn = new(new TableSummary("APP", "KUNDEN", TableKind.Table),
        [new ColumnInfo("KUNDE_ID", "NUMBER", null, false, 10, 0, false, false, null, 0)], ["KUNDE_ID"], [], false);

    public static TheoryData<string, string> Refused => new()
    {
        { "", QueryText.ConditionEmpty },
        { "  -- only a comment", QueryText.ConditionEmpty },
        { "NAME = 'open", QueryText.ConditionTextOpen },
        { "NAME = q'[open'", QueryText.ConditionTextOpen },
        { "\"Mixed = 1", QueryText.ConditionNameOpen },
        { "A = 1 /* open", QueryText.ConditionCommentOpen },
        { "(A = 1", QueryText.ConditionParenthesisOpen },
        // Out of the parentheses the statement puts around the condition: the rest would no longer be a condition.
        { "1 = 1) UNION SELECT * FROM SECRETS WHERE (1 = 1", QueryText.ConditionParenthesisClosed },
        { "ID = :id", TextFormat.Format(QueryText.ConditionBind, ":id") },
        { "A = 1 ORDER BY NAME", TextFormat.Format(QueryText.ConditionTopLevel, "ORDER") },
        { "A = 1 UNION SELECT 1 FROM dual", TextFormat.Format(QueryText.ConditionTopLevel, "UNION") },
        { "A = 1 FOR UPDATE", TextFormat.Format(QueryText.ConditionTopLevel, "FOR UPDATE") },
        { "A = 1; DELETE FROM X", TextFormat.Format(QueryText.ConditionTopLevel, ";") },
        // Inside parentheses too: the read-only tripwire refuses both at any depth (review of WP-79).
        { "ID IN (SELECT ID FROM B FOR UPDATE)", TextFormat.Format(QueryText.ConditionTopLevel, "FOR UPDATE") },
        { "ID IN (SELECT ID FROM B;)", TextFormat.Format(QueryText.ConditionTopLevel, ";") },
        { "A = 1 group by A", TextFormat.Format(QueryText.ConditionTopLevel, "GROUP") },
        { "A = 1 CONNECT BY PRIOR ID = PARENT", TextFormat.Format(QueryText.ConditionTopLevel, "CONNECT") },
        { "A = 1 FETCH FIRST 5 ROWS ONLY", TextFormat.Format(QueryText.ConditionTopLevel, "FETCH") },
        { "A = 1 OFFSET 5 ROWS", TextFormat.Format(QueryText.ConditionTopLevel, "OFFSET") },
    };

    [Theory]
    [MemberData(nameof(Refused))]
    public void A_condition_that_would_change_the_statement_is_refused_with_the_reason(string condition, string reason) =>
        Assert.Equal(reason, WhereCondition.Problem(condition));

    /// <summary>
    /// The condition ends in a line comment: in its own lines and parentheses it comments out neither the ORDER BY nor the page,
    /// and the statement passes the session's read-only tripwire.
    /// </summary>
    [Fact]
    public void In_a_statement_the_condition_keeps_to_its_own_lines()
    {
        var table = new TableDetails(new TableSummary("APP", "KUNDEN", TableKind.Table),
            [new ColumnInfo("KUNDE_ID", "NUMBER", null, false, 10, 0, false, false, null, 0)], ["KUNDE_ID"], [], false);

        var query = QueryBuilder.BuildSelect(table, RowFilter.Where("KUNDE_ID > 3 -- recent"), [], new PageSpec(0, 500));

        Assert.Contains("\n WHERE (\nKUNDE_ID > 3 -- recent\n)\n ORDER BY t.\"KUNDE_ID\"\nOFFSET :p_offset", query.Sql, StringComparison.Ordinal);
        Assert.True(OracleSession.IsReadOnlyStatement(query.Sql));
        var count = QueryBuilder.BuildCount(table, RowFilter.Where("KUNDE_ID > 3 -- recent"));
        Assert.EndsWith("\n WHERE (\nKUNDE_ID > 3 -- recent\n)", count.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void A_refused_condition_builds_no_statement()
    {
        var table = new TableDetails(new TableSummary("APP", "KUNDEN", TableKind.Table),
            [new ColumnInfo("KUNDE_ID", "NUMBER", null, false, 10, 0, false, false, null, 0)], ["KUNDE_ID"], [], false);

        var refused = Assert.Throws<QueryValidationException>(() => QueryBuilder.BuildCount(table, RowFilter.Where("KUNDE_ID = 1 ORDER BY 1")));

        Assert.Equal(TextFormat.Format(QueryText.ConditionTopLevel, "ORDER"), refused.Errors[0]);
    }

    /// <summary>
    /// Handed on as a list of filter rows (a preset, LINQ, <c>SetFilterRows</c>), a condition would filter nothing: every row, the
    /// whole DbSet. It refuses to be one (review of WP-79); filter rows enumerate as before.
    /// </summary>
    [Fact]
    public void A_condition_is_no_list_of_filter_rows()
    {
        Assert.Throws<InvalidOperationException>(() => RowFilter.Where("ID = 1").ToList());
        Assert.Equal([FilterCondition.Of("ID", FilterOperator.IsNull)], RowFilter.Of([FilterCondition.Of("ID", FilterOperator.IsNull)]).ToList());
    }

    /// <summary>The cursor of the field lands at the same place in the statement the completion reads.</summary>
    [Fact]
    public void For_completion_the_condition_stands_as_the_where_of_a_query_on_the_table()
    {
        var (statement, cursor) = WhereCondition.ForCompletion(new TableRef("APP", "KUNDEN"), "NAME = 'x' AND K", 16);

        Assert.Equal("SELECT *\n  FROM \"APP\".\"KUNDEN\" t\n WHERE NAME = 'x' AND K", statement);
        Assert.Equal(statement.Length, cursor);
    }
}
