using System.Text.RegularExpressions;
using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Tests.Query;

public partial class QueryBuilderTests
{
    private static ColumnInfo Col(string name, string type, bool nullable = true, int? length = null, int? precision = null, int? scale = null) =>
        new(name, type, length, false, precision, scale, nullable, false, null, 0);

    private static readonly TableDetails Kunden = new(
        new TableSummary("APP", "KUNDEN", TableKind.Table),
        [
            Col("KUNDE_ID", "NUMBER", nullable: false, precision: 10, scale: 0),
            Col("NAME", "VARCHAR2", nullable: false, length: 100),
            Col("KUERZEL", "CHAR", length: 3),
            Col("ERSTELLT_AM", "DATE"),
            Col("GEAENDERT", "TIMESTAMP(6)", scale: 6),
            Col("NOTIZ", "CLOB"),
            Col("BILD", "BLOB"),
            Col("XML", "XMLTYPE"),
            Col("Mixed Case", "VARCHAR2", length: 10),
        ],
        ["KUNDE_ID"],
        [],
        false);

    private static readonly TableDetails Log = Kunden with { Table = new TableSummary("APP", "LOG", TableKind.Table), PrimaryKey = [] };

    private static readonly TableDetails View = Kunden with { Table = new TableSummary("APP", "V_KUNDEN", TableKind.View), PrimaryKey = [] };

    private static readonly PageSpec FirstPage = new(0, 500);

    private static SelectQuery Select(TableDetails table, IReadOnlyList<FilterCondition>? filters = null, IReadOnlyList<SortSpec>? sorts = null)
    {
        var query = QueryBuilder.BuildSelect(table, filters ?? [], sorts ?? [], FirstPage);
        AssertBindsMatch(query.Sql, query.Parameters);
        return query;
    }

    private static (string Where, IReadOnlyList<QueryParameter> Parameters) Where(params FilterCondition[] filters)
    {
        var query = QueryBuilder.BuildCount(Kunden, filters);
        AssertBindsMatch(query.Sql, query.Parameters);
        var index = query.Sql.IndexOf("WHERE ", StringComparison.Ordinal);
        return (index < 0 ? "" : query.Sql[(index + 6)..], query.Parameters);
    }

    /// <summary>Every bind variable in the SQL has a parameter and vice versa (unused binds fail with ORA-01036).</summary>
    private static void AssertBindsMatch(string sql, IReadOnlyList<QueryParameter> parameters)
    {
        var inSql = BindRegex().Matches(sql).Select(m => m.Groups[1].Value).ToHashSet();
        Assert.Equal(inSql.Order(), parameters.Select(p => p.Name).Order());
        Assert.Equal(parameters.Count, parameters.Select(p => p.Name).Distinct().Count());
    }

    [GeneratedRegex(@":(\w+)")]
    private static partial Regex BindRegex();

    [Fact]
    public void Select_lists_rowid_and_projects_each_column_by_type()
    {
        var query = Select(Kunden);

        Assert.StartsWith("SELECT t.ROWID,", query.Sql);
        Assert.Contains("t.\"KUNDE_ID\"", query.Sql);
        Assert.Contains("DBMS_LOB.SUBSTR(t.\"NOTIZ\", 200, 1)", query.Sql);
        Assert.Contains("DBMS_LOB.GETLENGTH(t.\"NOTIZ\")", query.Sql);
        Assert.Contains("DBMS_LOB.GETLENGTH(t.\"BILD\")", query.Sql);
        Assert.Contains("CASE WHEN t.\"XML\" IS NULL THEN 0 ELSE 1 END", query.Sql);
        Assert.Contains("t.\"Mixed Case\"", query.Sql);
        Assert.Contains("FROM \"APP\".\"KUNDEN\" t", query.Sql);
        Assert.EndsWith("OFFSET :p_offset ROWS FETCH NEXT :p_limit ROWS ONLY", query.Sql);
        Assert.True(query.HasRowId);
        Assert.Equal(RowKeyKind.PrimaryKey, query.RowKey);
        Assert.Equal(
            [Projection.Value, Projection.Value, Projection.Value, Projection.Value, Projection.Value, Projection.ClobPreview, Projection.BlobLength, Projection.NullMarker, Projection.Value],
            query.Columns.Select(c => c.Projection));
    }

    [Fact]
    public void Default_order_is_the_primary_key()
    {
        Assert.Contains("ORDER BY t.\"KUNDE_ID\"\n", Select(Kunden).Sql);
    }

    [Fact]
    public void User_sort_comes_first_and_primary_key_breaks_ties_without_duplicates()
    {
        var sql = Select(Kunden, sorts: [new("NAME", true), new("KUNDE_ID", false)]).Sql;

        Assert.Contains("ORDER BY t.\"NAME\" DESC, t.\"KUNDE_ID\"\n", sql);
    }

    [Fact]
    public void Table_without_primary_key_uses_rowid_as_tiebreaker()
    {
        var query = Select(Log, sorts: [new("NAME", false)]);

        Assert.Contains("ORDER BY t.\"NAME\", t.ROWID\n", query.Sql);
        Assert.Equal(RowKeyKind.RowId, query.RowKey);
    }

    [Fact]
    public void View_without_key_has_no_rowid_and_orders_by_all_sortable_columns()
    {
        var query = Select(View);

        Assert.False(query.HasRowId);
        Assert.DoesNotContain("ROWID", query.Sql);
        Assert.Equal(RowKeyKind.None, query.RowKey);
        Assert.Contains("ORDER BY t.\"KUNDE_ID\", t.\"NAME\", t.\"KUERZEL\", t.\"ERSTELLT_AM\", t.\"GEAENDERT\", t.\"Mixed Case\"\n", query.Sql);
    }

    [Fact]
    public void Sorting_by_a_lob_is_rejected()
    {
        var ex = Assert.Throws<QueryValidationException>(() => QueryBuilder.BuildSelect(Kunden, [], [new("NOTIZ", false)], FirstPage));

        Assert.Contains(-1, ex.Errors.Keys);
    }

    [Fact]
    public void Text_equality_binds_varchar2_and_char_columns_as_char()
    {
        var (where, parameters) = Where(FilterCondition.Of("NAME", FilterOperator.Equals, "Müller"), FilterCondition.Of("KUERZEL", FilterOperator.Equals, "AB"));

        Assert.Equal("t.\"NAME\" = :p0\n   AND t.\"KUERZEL\" = :p1", where);
        Assert.Equal(("Müller", OracleTypeHint.Varchar2), (parameters[0].Value, parameters[0].Type));
        Assert.Equal(("AB", OracleTypeHint.Char), (parameters[1].Value, parameters[1].Type));
    }

    [Fact]
    public void Not_equals_includes_null_for_nullable_columns_only()
    {
        Assert.Equal("(t.\"KUERZEL\" <> :p0 OR t.\"KUERZEL\" IS NULL)", Where(FilterCondition.Of("KUERZEL", FilterOperator.NotEquals, "AB")).Where);
        Assert.Equal("t.\"NAME\" <> :p0", Where(FilterCondition.Of("NAME", FilterOperator.NotEquals, "x")).Where);
    }

    [Theory]
    [InlineData(FilterOperator.Contains, "%50\\%\\_a\\\\b%")]
    [InlineData(FilterOperator.StartsWith, "50\\%\\_a\\\\b%")]
    [InlineData(FilterOperator.EndsWith, "%50\\%\\_a\\\\b")]
    public void Like_operators_escape_wildcards_and_ignore_case(FilterOperator op, string expectedPattern)
    {
        var (where, parameters) = Where(FilterCondition.Of("NAME", op, "50%_a\\b"));

        Assert.Equal("UPPER(t.\"NAME\") LIKE UPPER(:p0) ESCAPE '\\'", where);
        Assert.Equal(expectedPattern, parameters[0].Value);
    }

    [Fact]
    public void Numbers_accept_german_and_invariant_notation()
    {
        var (where, parameters) = Where(
            FilterCondition.Of("KUNDE_ID", FilterOperator.Gte, "1.234,5"),
            FilterCondition.Of("KUNDE_ID", FilterOperator.Lt, "99999.25"));

        Assert.Equal("t.\"KUNDE_ID\" >= :p0\n   AND t.\"KUNDE_ID\" < :p1", where);
        Assert.Equal([1234.5m, 99999.25m], parameters.Select(p => p.Value));
        Assert.All(parameters, p => Assert.Equal(OracleTypeHint.Number, p.Type));
    }

    [Fact]
    public void Date_without_time_means_the_whole_day()
    {
        var (where, parameters) = Where(FilterCondition.Of("ERSTELLT_AM", FilterOperator.Equals, "1.10.2026"));

        Assert.Equal("(t.\"ERSTELLT_AM\" >= :p0 AND t.\"ERSTELLT_AM\" < :p1)", where);
        Assert.Equal([new DateTime(2026, 10, 1), new DateTime(2026, 10, 2)], parameters.Select(p => p.Value));
        Assert.All(parameters, p => Assert.Equal(OracleTypeHint.Date, p.Type));
    }

    [Fact]
    public void Date_with_time_is_an_exact_comparison_and_timestamps_bind_as_timestamp()
    {
        var (where, parameters) = Where(FilterCondition.Of("GEAENDERT", FilterOperator.Equals, "2026-10-01 14:30"));

        Assert.Equal("t.\"GEAENDERT\" = :p0", where);
        Assert.Equal(new DateTime(2026, 10, 1, 14, 30, 0), parameters[0].Value);
        Assert.Equal(OracleTypeHint.TimeStamp, parameters[0].Type);
    }

    [Theory]
    [InlineData(FilterOperator.Gt, "t.\"ERSTELLT_AM\" >= :p0", 2)]
    [InlineData(FilterOperator.Gte, "t.\"ERSTELLT_AM\" >= :p0", 1)]
    [InlineData(FilterOperator.Lt, "t.\"ERSTELLT_AM\" < :p0", 1)]
    [InlineData(FilterOperator.Lte, "t.\"ERSTELLT_AM\" < :p0", 2)]
    public void Date_only_comparisons_respect_whole_days(FilterOperator op, string expected, int expectedDay)
    {
        var (where, parameters) = Where(FilterCondition.Of("ERSTELLT_AM", op, "01.10.2026"));

        Assert.Equal(expected, where);
        Assert.Equal(new DateTime(2026, 10, expectedDay), Assert.Single(parameters).Value);
    }

    [Fact]
    public void Date_between_includes_the_last_day()
    {
        var (where, parameters) = Where(FilterCondition.Of("ERSTELLT_AM", FilterOperator.Between, "1.10.2026", "31.10.2026"));

        Assert.Equal("t.\"ERSTELLT_AM\" >= :p0 AND t.\"ERSTELLT_AM\" < :p1", where);
        Assert.Equal([new DateTime(2026, 10, 1), new DateTime(2026, 11, 1)], parameters.Select(p => p.Value));
    }

    [Fact]
    public void In_lists_are_split_into_chunks_of_1000()
    {
        var values = Enumerable.Range(1, 2500).Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray();

        var (where, parameters) = Where(FilterCondition.Of("KUNDE_ID", FilterOperator.In, values));

        Assert.Equal(2500, parameters.Count);
        Assert.Equal(3, Regex.Matches(where, " IN \\(").Count);
        Assert.StartsWith("(t.\"KUNDE_ID\" IN (:p0, ", where);
        Assert.Contains(") OR t.\"KUNDE_ID\" IN (:p1000, ", where);
    }

    [Fact]
    public void Null_checks_need_no_values_and_disabled_filters_are_ignored()
    {
        var (where, parameters) = Where(
            FilterCondition.Of("KUERZEL", FilterOperator.IsNull),
            new FilterCondition("NAME", FilterOperator.Equals, ["ignored"], Enabled: false),
            FilterCondition.Of("NOTIZ", FilterOperator.IsNotNull));

        Assert.Equal("t.\"KUERZEL\" IS NULL\n   AND t.\"NOTIZ\" IS NOT NULL", where);
        Assert.Empty(parameters);
    }

    [Fact]
    public void Count_uses_the_same_filters_without_paging()
    {
        var query = QueryBuilder.BuildCount(Kunden, [FilterCondition.Of("NAME", FilterOperator.StartsWith, "A")]);

        Assert.Equal("SELECT COUNT(*)\n  FROM \"APP\".\"KUNDEN\" t\n WHERE UPPER(t.\"NAME\") LIKE UPPER(:p0) ESCAPE '\\'", query.Sql);
    }

    [Fact]
    public void Invalid_filters_are_reported_by_index()
    {
        FilterCondition[] filters =
        [
            FilterCondition.Of("NAME", FilterOperator.Equals, ""),
            FilterCondition.Of("KUNDE_ID", FilterOperator.Equals, "abc"),
            FilterCondition.Of("KUNDE_ID", FilterOperator.Contains, "1"),
            FilterCondition.Of("NOPE", FilterOperator.IsNull),
            FilterCondition.Of("ERSTELLT_AM", FilterOperator.Between, "1.1.2026"),
            FilterCondition.Of("NAME", FilterOperator.Contains, "ok"),
        ];

        var ex = Assert.Throws<QueryValidationException>(() => QueryBuilder.BuildSelect(Kunden, filters, [], FirstPage));

        Assert.Equal([0, 1, 2, 3, 4], ex.Errors.Keys.Order());
        Assert.Contains("NULL", ex.Errors[0]);
    }
}
