using FerretSharp.Core.Oracle;
using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Tests.Query;

/// <summary>Rows by key for following relationships (WP-42).</summary>
public partial class QueryBuilderTests
{
    private static readonly TableDetails Position = new(
        new TableSummary("APP", "POSITION", TableKind.Table),
        [
            Col("AUFTRAG_ID", "NUMBER", nullable: false, precision: 10, scale: 0),
            Col("POS_NR", "NUMBER", nullable: false, precision: 4, scale: 0),
            Col("ARTIKEL", "NVARCHAR2", length: 20),
        ],
        ["AUFTRAG_ID", "POS_NR"],
        [],
        false);

    private static SelectQuery ByKeys(TableDetails table, IReadOnlyList<int>? columns, IReadOnlyList<IReadOnlyList<object>> keys, bool wholeLobs = false)
    {
        var query = QueryBuilder.BuildRowsByKeys(table, columns, keys, wholeLobs);
        AssertBindsMatch(query.Sql, query.Parameters);
        Assert.True(OracleSession.IsReadOnlyStatement(query.Sql));
        return query;
    }

    [Fact]
    public void Rows_by_one_key_column_read_like_the_grid_and_are_ordered_by_the_row_key()
    {
        var query = ByKeys(Kunden, [0], [[4711m], [4712m]]);

        Assert.StartsWith("SELECT t.ROWID,", query.Sql);
        Assert.Contains("DBMS_LOB.SUBSTR(t.\"NOTIZ\", 200, 1)", query.Sql);
        Assert.EndsWith(
            "\n  FROM \"APP\".\"KUNDEN\" t\n WHERE t.\"KUNDE_ID\" IN (:k0, :k1, :k2, :k3, :k4, :k5, :k6, :k7, :k8, :k9)\n ORDER BY t.\"KUNDE_ID\"",
            query.Sql);
        Assert.Equal([4711m, 4712m, 4712m, 4712m, 4712m, 4712m, 4712m, 4712m, 4712m, 4712m], query.Parameters.Select(p => p.Value));
        Assert.All(query.Parameters, p => Assert.Equal(OracleTypeHint.Number, p.Type));
        Assert.Equal(RowKeyKind.PrimaryKey, query.RowKey);
        Assert.Equal(Kunden.Columns.Count, query.Columns.Count);
    }

    [Fact]
    public void Rows_by_a_composite_key_compare_tuples_bound_with_each_columns_type()
    {
        var query = ByKeys(Position, [0, 2], [[1m, "A-1"]]);

        Assert.Contains("\n WHERE (t.\"AUFTRAG_ID\", t.\"ARTIKEL\") IN ((:k0_0, :k0_1))", query.Sql);
        Assert.Equal([OracleTypeHint.Number, OracleTypeHint.NVarchar2], query.Parameters.Select(p => p.Type));
        Assert.Equal([1m, "A-1"], query.Parameters.Select(p => p.Value));
    }

    [Fact]
    public void Rows_by_rowid_for_a_table_without_key()
    {
        var query = ByKeys(Log, null, [["AAAR3sAAEAAAACXAAA"], ["AAAR3sAAEAAAACXAAB"]]);

        Assert.Contains("\n WHERE t.ROWID IN (:k0, :k1, :k2, :k3, :k4, :k5, :k6, :k7, :k8, :k9)\n ORDER BY t.ROWID", query.Sql);
        Assert.All(query.Parameters, p => Assert.Equal(OracleTypeHint.Varchar2, p.Type));
        Assert.Equal(RowKeyKind.RowId, query.RowKey);
    }

    [Fact]
    public void Whole_lobs_select_the_columns_themselves_but_other_types_stay_markers()
    {
        var query = ByKeys(Kunden, [0], [[1m]], wholeLobs: true);

        Assert.Contains("t.\"NOTIZ\",", query.Sql);
        Assert.Contains("t.\"BILD\",", query.Sql);
        Assert.DoesNotContain("DBMS_LOB", query.Sql);
        Assert.Contains("CASE WHEN t.\"XML\" IS NULL THEN 0 ELSE 1 END", query.Sql);
        Assert.Equal(
            [Projection.Value, Projection.Value, Projection.NullMarker],
            query.Columns.Where(c => c.Column.Name is "NOTIZ" or "BILD" or "XML").Select(c => c.Projection));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(11, 50)]
    [InlineData(500, 500)]
    public void Rows_by_key_use_only_a_few_statement_shapes(int keys, int tuples)
    {
        var query = ByKeys(Position, [0, 1], [.. Enumerable.Range(1, keys).Select(i => (IReadOnlyList<object>)[(decimal)i, 1m])]);

        Assert.Equal(tuples * 2, query.Parameters.Count);
    }

    [Fact]
    public void Rows_by_key_refuse_no_keys_too_many_keys_keys_of_the_wrong_size_and_null()
    {
        Assert.Throws<ArgumentException>(() => QueryBuilder.BuildRowsByKeys(Kunden, [0], [], false));
        Assert.Throws<ArgumentException>(() => QueryBuilder.BuildRowsByKeys(Kunden, [0], [.. Enumerable.Range(0, 501).Select(i => (IReadOnlyList<object>)[(decimal)i])], false));
        Assert.Throws<ArgumentException>(() => QueryBuilder.BuildRowsByKeys(Position, [0, 1], [[1m]], false));
        Assert.Throws<ArgumentException>(() => QueryBuilder.BuildRowsByKeys(Kunden, [0], [[null!]], false));
        Assert.Throws<ArgumentException>(() => QueryBuilder.BuildRowsByKeys(Kunden, [], [[1m]], false));
    }
}
