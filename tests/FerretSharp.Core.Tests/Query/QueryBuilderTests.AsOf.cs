using FerretSharp.Core.Data;
using FerretSharp.Core.Oracle;
using FerretSharp.Core.Query;

namespace FerretSharp.Core.Tests.Query;

/// <summary>Reading a table as it was at an SCN (time travel and restoring rows, WP-78, ADR 0048).</summary>
public partial class QueryBuilderTests
{
    private static readonly FilterCondition NameIsX = new("NAME", FilterOperator.Equals, ["x"]);

    /// <summary>Every statement of the read path names its table AS OF SCN, binds the SCN and stays a plain query.</summary>
    public static TheoryData<string> AsOfStatements => ["page", "all", "count", "values", "lob", "keys", "lookup"];

    private static QuerySpec AsOfStatement(string kind, long? asOf)
    {
        var key = new RowKey.PrimaryKey([1m]);
        return kind switch
        {
            "page" => Spec(QueryBuilder.BuildSelect(Kunden, [NameIsX], [], FirstPage, asOf)),
            "all" => Spec(QueryBuilder.BuildSelectAll(Kunden, [NameIsX], [], asOf)),
            "count" => QueryBuilder.BuildCount(Kunden, [NameIsX], asOf),
            "values" => Spec(QueryBuilder.BuildColumnValues(Kunden, [1], [NameIsX], [], distinct: true, asOf)),
            "lob" => QueryBuilder.BuildSelectLob(Kunden, key, 5, asOf),
            "keys" => Spec(QueryBuilder.BuildRowsByKeys(Kunden, [0], [[1m]], wholeLobs: true, asOf)),
            _ => QueryBuilder.BuildLookup(Kunden, 0, [1], [1m], asOf),
        };

        static QuerySpec Spec(SelectQuery query) => new(query.Sql, query.Parameters);
    }

    [Theory]
    [MemberData(nameof(AsOfStatements))]
    public void Every_statement_of_the_read_path_reads_its_table_as_of_the_scn(string kind)
    {
        var now = AsOfStatement(kind, asOf: null);
        var then = AsOfStatement(kind, asOf: 4711);
        AssertBindsMatch(then.Sql, then.Parameters);

        Assert.DoesNotContain("AS OF", now.Sql, StringComparison.Ordinal);
        Assert.Contains("\"APP\".\"KUNDEN\" AS OF SCN :p_asof", then.Sql, StringComparison.Ordinal);
        Assert.Equal(4711L, then.Parameters.Single(p => p.Name == "p_asof").Value);
        Assert.True(OracleSession.IsReadOnlyStatement(then.Sql));
        // Only the FROM changes: the same statement otherwise (the binds may be numbered on), so the grid and the tab of then read alike.
        Assert.Equal(BindRegex().Replace(now.Sql, ":b"), BindRegex().Replace(then.Sql.Replace(" AS OF SCN :p_asof", "", StringComparison.Ordinal), ":b"));
    }

    [Fact]
    public void The_filter_as_sql_of_a_time_travel_tab_names_the_scn_as_a_number()
    {
        var query = QueryBuilder.BuildSelectStar(Kunden, [NameIsX], [], asOf: 4711);

        Assert.StartsWith("SELECT t.*\n  FROM \"APP\".\"KUNDEN\" AS OF SCN 4711 t\n WHERE", query.Sql, StringComparison.Ordinal);
        AssertBindsMatch(query.Sql, query.Parameters); // the filter's value stays a bind
    }

    [Fact]
    public void The_points_in_time_are_plain_queries_without_rights_or_packs()
    {
        // TIMESTAMP_TO_SCN and USERENV need no right; V$DATABASE and V$UNDOSTAT are tried and left out without one (ADR 0045).
        foreach (var sql in new[] { QueryBuilder.CurrentScnSql, QueryBuilder.ScnBackSql, QueryBuilder.ExactScnSql, QueryBuilder.UndoReachSql })
        {
            Assert.True(OracleSession.IsReadOnlyStatement(sql), sql);
        }

        Assert.Contains("SYSTIMESTAMP - NUMTODSINTERVAL(:p_back, 'SECOND')", QueryBuilder.ScnBackSql, StringComparison.Ordinal);
        Assert.Contains("SYS_CONTEXT('USERENV', 'DB_UNIQUE_NAME')", QueryBuilder.CurrentScnSql, StringComparison.Ordinal);
    }
}
