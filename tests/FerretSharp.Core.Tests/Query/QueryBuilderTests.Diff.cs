using FerretSharp.Core.Oracle;
using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Tests.Query;

/// <summary>The data diff against a start SCN (WP-44).</summary>
public partial class QueryBuilderTests
{
    private static readonly TableDetails Notes = new(
        new TableSummary("APP", "NOTES", TableKind.Table),
        [
            Col("TENANT", "NUMBER", nullable: false, precision: 5, scale: 0),
            Col("NOTE_ID", "NUMBER", nullable: false, precision: 10, scale: 0),
            Col("NAME", "VARCHAR2", length: 50),
            Col("TEXT", "CLOB"),
            Col("DOC", "XMLTYPE"),
        ],
        ["NOTE_ID", "TENANT"],
        [],
        false);

    private static DiffQuery Diff(TableDetails table, params FilterCondition[] filters)
    {
        var query = QueryBuilder.BuildDiff(table, filters, 4711, 21);
        AssertBindsMatch(query.Sql, query.Parameters);
        Assert.True(OracleSession.IsReadOnlyStatement(query.Sql));
        return query;
    }

    [Fact]
    public void A_diff_is_both_minus_of_the_filter_now_and_as_of_the_start_marked_by_side_and_ordered_by_the_key()
    {
        var query = Diff(Notes, new FilterCondition("NAME", FilterOperator.Equals, ["x"]));

        const string list = "t.\"TENANT\" AS C0, t.\"NOTE_ID\" AS C1, t.\"NAME\" AS C2, DBMS_LOB.SUBSTR(t.\"TEXT\", 200, 1) AS C3_P, DBMS_LOB.GETLENGTH(t.\"TEXT\") AS C3_L";
        Assert.Equal(
            "SELECT FS_SIDE, C0, C1, C2, C3_P, C3_L\n"
            + "  FROM (SELECT 1 AS FS_SIDE, n.*\n"
            + $"          FROM (SELECT {list}\n"
            + "                  FROM \"APP\".\"NOTES\" t\n"
            + "                 WHERE t.\"NAME\" = :p0\n"
            + "                MINUS\n"
            + $"                SELECT {list}\n"
            + "                  FROM \"APP\".\"NOTES\" AS OF SCN :d_scn0 t\n"
            + "                 WHERE t.\"NAME\" = :p2) n\n"
            + "        UNION ALL\n"
            + "        SELECT 0 AS FS_SIDE, o.*\n"
            + $"          FROM (SELECT {list}\n"
            + "                  FROM \"APP\".\"NOTES\" AS OF SCN :d_scn1 t\n"
            + "                 WHERE t.\"NAME\" = :p4\n"
            + "                MINUS\n"
            + $"                SELECT {list}\n"
            + "                  FROM \"APP\".\"NOTES\" t\n"
            + "                 WHERE t.\"NAME\" = :p5) o)\n"
            + " ORDER BY C1, C0, FS_SIDE\n"
            + " FETCH FIRST :d_limit ROWS ONLY",
            query.Sql);
        Assert.Equal(4711L, query.Parameters.Single(p => p.Name == "d_scn0").Value);
        Assert.Equal(21, query.Parameters.Single(p => p.Name == "d_limit").Value);
        Assert.All(query.Parameters.Where(p => p.Name.StartsWith('p')), p => Assert.Equal("x", p.Value));
        Assert.Equal([4], query.Omitted); // XMLTYPE: a set operation cannot compare it
        Assert.Equal(RowKeyKind.PrimaryKey, query.RowKey);
        Assert.False(query.HasRowId);
    }

    [Fact]
    public void A_table_without_primary_key_is_compared_and_ordered_by_rowid_and_a_view_not_at_all()
    {
        var query = Diff(Log);

        Assert.StartsWith("SELECT FS_SIDE, FS_RID, C0, ", query.Sql, StringComparison.Ordinal);
        Assert.Contains("(SELECT t.ROWID AS FS_RID, t.\"KUNDE_ID\" AS C0", query.Sql, StringComparison.Ordinal);
        Assert.Contains(" ORDER BY FS_RID, FS_SIDE\n", query.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain(" WHERE ", query.Sql, StringComparison.Ordinal);
        Assert.Contains("DBMS_LOB.GETLENGTH(t.\"BILD\") AS C6,", query.Sql, StringComparison.Ordinal); // a BLOB by its length
        Assert.True(query.HasRowId);
        Assert.Equal(RowKeyKind.RowId, query.RowKey);

        Assert.Throws<ArgumentException>(() => QueryBuilder.BuildDiff(View, [], 4711, 21));
    }
}
