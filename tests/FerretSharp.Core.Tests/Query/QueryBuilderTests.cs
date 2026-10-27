using System.Text.RegularExpressions;
using FerretSharp.Core.Oracle;
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
    public void All_rows_are_the_page_query_without_paging()
    {
        IReadOnlyList<FilterCondition> filters = [new("NAME", FilterOperator.Contains, ["Ferret"])];
        IReadOnlyList<SortSpec> sorts = [new("NAME", true)];
        var page = Select(Kunden, filters, sorts);

        var all = QueryBuilder.BuildSelectAll(Kunden, filters, sorts);

        AssertBindsMatch(all.Sql, all.Parameters);
        Assert.Equal(page.Sql.Replace("\nOFFSET :p_offset ROWS FETCH NEXT :p_limit ROWS ONLY", "", StringComparison.Ordinal), all.Sql);
        Assert.Equal(page.Parameters.Where(p => p.Name is not ("p_offset" or "p_limit")), all.Parameters);
        Assert.Equal(page.Columns, all.Columns);
        Assert.True(StatementGuard.IsQuery(all.Sql));
    }

    [Fact]
    public void Select_star_has_the_filter_and_the_sorts_with_the_key_but_no_rowid_projection_or_page()
    {
        IReadOnlyList<FilterCondition> filters = [new("NAME", FilterOperator.Contains, ["Ferret"])];
        IReadOnlyList<SortSpec> sorts = [new("ERSTELLT_AM", true)];
        var all = QueryBuilder.BuildSelectAll(Kunden, filters, sorts);

        var star = QueryBuilder.BuildSelectStar(Kunden, filters, sorts);

        AssertBindsMatch(star.Sql, star.Parameters);
        Assert.StartsWith("SELECT t.*\n  FROM \"APP\".\"KUNDEN\" t\n WHERE ", star.Sql);
        Assert.Equal(all.Sql[all.Sql.IndexOf("\n WHERE ", StringComparison.Ordinal)..], star.Sql[star.Sql.IndexOf("\n WHERE ", StringComparison.Ordinal)..]);
        Assert.EndsWith("\n ORDER BY t.\"ERSTELLT_AM\" DESC, t.\"KUNDE_ID\"", star.Sql);
        Assert.Equal(all.Parameters, star.Parameters);
        Assert.DoesNotContain("ROWID", star.Sql, StringComparison.Ordinal);
        Assert.True(StatementGuard.IsQuery(star.Sql));
    }

    /// <summary>
    /// The SQL editor reads later pages by running the statement again: a table without key keeps ROWID as the last term of
    /// the order (not in the select list), or rows would repeat or go missing between pages.
    /// </summary>
    [Fact]
    public void Select_star_without_key_ends_the_order_with_rowid_a_view_without_key_has_only_the_sorts()
    {
        Assert.Equal("SELECT t.*\n  FROM \"APP\".\"LOG\" t\n ORDER BY t.\"NAME\", t.ROWID", QueryBuilder.BuildSelectStar(Log, [], [new("NAME", false)]).Sql);
        Assert.Equal("SELECT t.*\n  FROM \"APP\".\"LOG\" t\n ORDER BY t.ROWID", QueryBuilder.BuildSelectStar(Log, [], []).Sql);
        Assert.Equal("SELECT t.*\n  FROM \"APP\".\"V_KUNDEN\" t\n ORDER BY t.\"NAME\"", QueryBuilder.BuildSelectStar(View, [], [new("NAME", false)]).Sql);
        Assert.Equal("SELECT t.*\n  FROM \"APP\".\"V_KUNDEN\" t", QueryBuilder.BuildSelectStar(View, [], []).Sql);
    }

    [Fact]
    public void Column_values_select_one_column_in_the_order_of_all_rows()
    {
        IReadOnlyList<FilterCondition> filters = [new("NAME", FilterOperator.Contains, ["Ferret"])];
        IReadOnlyList<SortSpec> sorts = [new("ERSTELLT_AM", true)];
        var all = QueryBuilder.BuildSelectAll(Kunden, filters, sorts);

        var values = QueryBuilder.BuildColumnValues(Kunden, [1], filters, sorts, distinct: false);

        AssertBindsMatch(values.Sql, values.Parameters);
        Assert.StartsWith("SELECT t.\"NAME\"\n  FROM \"APP\".\"KUNDEN\" t\n WHERE ", values.Sql);
        Assert.Equal(all.Sql[all.Sql.IndexOf("\n WHERE ", StringComparison.Ordinal)..], values.Sql[values.Sql.IndexOf("\n WHERE ", StringComparison.Ordinal)..]);
        Assert.Equal(all.Parameters, values.Parameters);
        Assert.Equal([new ResultColumn(Kunden.Columns[1], Projection.Value)], values.Columns);
        Assert.Equal(RowKeyKind.None, values.RowKey);
        Assert.False(values.HasRowId);
        Assert.True(StatementGuard.IsQuery(values.Sql));
    }

    [Fact]
    public void Distinct_column_values_are_ordered_by_the_value()
    {
        var values = QueryBuilder.BuildColumnValues(Log, [2], [], [new("NAME", false)], distinct: true);

        Assert.Equal("SELECT DISTINCT t.\"KUERZEL\"\n  FROM \"APP\".\"LOG\" t\n ORDER BY t.\"KUERZEL\"", values.Sql);
        Assert.True(StatementGuard.IsQuery(values.Sql));
    }

    [Theory]
    [InlineData(5)] // CLOB
    [InlineData(6)] // BLOB
    [InlineData(7)] // XMLTYPE
    public void Column_values_refuse_columns_not_read_as_values(int column)
    {
        Assert.Throws<ArgumentException>(() => QueryBuilder.BuildColumnValues(Kunden, [column], [], [], distinct: false));
        Assert.Throws<ArgumentException>(() => QueryBuilder.BuildColumnValues(Kunden, [1, column], [], [], distinct: false));
    }

    [Fact]
    public void Values_of_several_columns_come_in_the_given_order()
    {
        IReadOnlyList<SortSpec> sorts = [new("ERSTELLT_AM", true)];
        var all = QueryBuilder.BuildSelectAll(Kunden, [], sorts);

        var values = QueryBuilder.BuildColumnValues(Kunden, [2, 0], [], sorts, distinct: false);

        Assert.StartsWith("SELECT t.\"" + Kunden.Columns[2].Name + "\", t.\"KUNDE_ID\"\n  FROM \"APP\".\"KUNDEN\" t\n ORDER BY ", values.Sql);
        Assert.Equal(all.Sql[all.Sql.IndexOf("\n ORDER BY ", StringComparison.Ordinal)..], values.Sql[values.Sql.IndexOf("\n ORDER BY ", StringComparison.Ordinal)..]);
        Assert.Equal([new ResultColumn(Kunden.Columns[2], Projection.Value), new ResultColumn(Kunden.Columns[0], Projection.Value)], values.Columns);
        Assert.True(StatementGuard.IsQuery(values.Sql));
    }

    [Fact]
    public void Column_values_need_a_column_and_distinct_only_one()
    {
        Assert.Throws<ArgumentException>(() => QueryBuilder.BuildColumnValues(Kunden, [], [], [], distinct: false));
        Assert.Throws<ArgumentException>(() => QueryBuilder.BuildColumnValues(Kunden, [1, 2], [], [], distinct: true));
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
    public void A_table_whose_primary_key_is_not_enforced_uses_rowid_a_view_keeps_its_declared_key()
    {
        var disabled = Select(Kunden with { PrimaryKeyEnforced = false }, sorts: [new("NAME", false)]);
        var view = Select(Kunden with { Table = View.Table, PrimaryKeyEnforced = false });

        Assert.Equal(RowKeyKind.RowId, disabled.RowKey);
        Assert.Contains("ORDER BY t.\"NAME\", t.ROWID\n", disabled.Sql);
        Assert.Equal(RowKeyKind.PrimaryKey, view.RowKey);
    }

    /// <summary>Review 3.31.0, A18 and A32: national columns were filtered in the database character set; "ends with" missed CHAR padding.</summary>
    [Fact]
    public void Text_filters_bind_national_columns_nationally_and_end_char_values_without_padding()
    {
        var table = Kunden with
        {
            Columns = [.. Kunden.Columns, Col("ORT", "NVARCHAR2", length: 40), Col("LAND", "NCHAR", length: 2)],
        };

        var query = Select(table, [
            FilterCondition.Of("ORT", FilterOperator.Equals, "Łódź"),
            FilterCondition.Of("LAND", FilterOperator.Equals, "PL"),
            FilterCondition.Of("ORT", FilterOperator.Contains, "ó"),
            FilterCondition.Of("KUERZEL", FilterOperator.EndsWith, "AB"),
        ]);

        Assert.Equal([OracleTypeHint.NVarchar2, OracleTypeHint.NChar, OracleTypeHint.NVarchar2, OracleTypeHint.Varchar2], query.Parameters.Take(4).Select(p => p.Type)); // then paging
        Assert.Contains("UPPER(RTRIM(t.\"KUERZEL\")) LIKE", query.Sql);
    }

    /// <summary>Review 3.40.0, A21: "contains" on an NCLOB bound its pattern as VARCHAR2 – the rest of A18.</summary>
    [Fact]
    public void A_text_search_in_an_nclob_binds_nationally()
    {
        var table = Kunden with { Columns = [.. Kunden.Columns, Col("BEMERKUNG", "NCLOB")] };

        var query = Select(table, [FilterCondition.Of("BEMERKUNG", FilterOperator.Contains, "Łódź"), FilterCondition.Of("NOTIZ", FilterOperator.Contains, "x")]);

        Assert.Equal([OracleTypeHint.NVarchar2, OracleTypeHint.Varchar2], query.Parameters.Take(2).Select(p => p.Type)); // NOTIZ is a CLOB
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

        Assert.Equal("(t.\"ERSTELLT_AM\" >= :p0 AND t.\"ERSTELLT_AM\" < :p1)", where);
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

    [Fact]
    public void Lookup_fills_the_in_list_up_to_a_fixed_size_with_the_last_key()
    {
        var query = QueryBuilder.BuildLookup(Kunden, keyColumn: 0, displayColumns: [1], [4711m, 4712m, 4713m]);

        AssertBindsMatch(query.Sql, query.Parameters);
        Assert.Equal(
            "SELECT t.\"KUNDE_ID\", t.\"NAME\"\n  FROM \"APP\".\"KUNDEN\" t\n WHERE t.\"KUNDE_ID\" IN (:k0, :k1, :k2, :k3, :k4, :k5, :k6, :k7, :k8, :k9)",
            query.Sql);
        Assert.Equal([4711m, 4712m, 4713m, 4713m, 4713m, 4713m, 4713m, 4713m, 4713m, 4713m], query.Parameters.Select(p => p.Value));
        Assert.All(query.Parameters, p => Assert.Equal(OracleTypeHint.Number, p.Type));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 10)]
    [InlineData(10, 10)]
    [InlineData(11, 50)]
    [InlineData(99, 100)]
    [InlineData(101, 500)]
    [InlineData(500, 500)]
    public void Lookup_uses_only_a_few_statement_shapes(int keys, int binds)
    {
        var query = QueryBuilder.BuildLookup(Kunden, 0, [1], Enumerable.Range(1, keys).Select(i => (object)(decimal)i).ToList());

        Assert.Equal(binds, query.Parameters.Count);
    }

    [Fact]
    public void Lookup_binds_with_the_key_columns_type_and_quotes_names()
    {
        var query = QueryBuilder.BuildLookup(Kunden, keyColumn: 2, displayColumns: [8], ["AB"]);

        Assert.Equal("SELECT t.\"KUERZEL\", t.\"Mixed Case\"\n  FROM \"APP\".\"KUNDEN\" t\n WHERE t.\"KUERZEL\" IN (:k0)", query.Sql);
        Assert.Equal(OracleTypeHint.Char, Assert.Single(query.Parameters).Type);
    }

    [Fact]
    public void Lookup_refuses_no_keys_too_many_keys_and_null()
    {
        Assert.Throws<ArgumentException>(() => QueryBuilder.BuildLookup(Kunden, 0, [1], []));
        Assert.Throws<ArgumentException>(() => QueryBuilder.BuildLookup(Kunden, 0, [1], Enumerable.Range(0, 501).Select(i => (object)(decimal)i).ToList()));
        Assert.Throws<ArgumentException>(() => QueryBuilder.BuildLookup(Kunden, 0, [1], [1m, null!]));
        Assert.Throws<ArgumentException>(() => QueryBuilder.BuildLookup(Kunden, 0, [], [1m]));
    }

    /// <summary>WP-65: several display columns in one lookup, in the order the name joins them.</summary>
    [Fact]
    public void Lookup_reads_several_display_columns_in_their_order()
    {
        var query = QueryBuilder.BuildLookup(Kunden, keyColumn: 0, displayColumns: [8, 1], [4711m]);

        AssertBindsMatch(query.Sql, query.Parameters);
        Assert.Equal("SELECT t.\"KUNDE_ID\", t.\"Mixed Case\", t.\"NAME\"\n  FROM \"APP\".\"KUNDEN\" t\n WHERE t.\"KUNDE_ID\" IN (:k0)", query.Sql);
    }
}
