using System.Text.RegularExpressions;
using FerretSharp.Core.Data;
using FerretSharp.Core.Oracle;
using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Tests.Query;

public class DmlBuilderTests
{
    private static ColumnInfo Col(string name, string type, int? length = null) => new(name, type, length, true, null, null, true, false, null, 0);

    private static readonly TableDetails Position = new(
        new TableSummary("APP", "Pos Tabelle", TableKind.Table),
        [Col("MANDANT", "CHAR", 3), Col("NR", "NUMBER"), Col("MENGE", "NUMBER"), Col("Text", "NVARCHAR2", 20), Col("DATUM", "DATE")],
        ["MANDANT", "NR"], [], false);

    private static readonly TableDetails Notizen = new(
        new TableSummary("APP", "NOTIZEN", TableKind.Table), [Col("TXT", "VARCHAR2", 100), Col("WANN", "TIMESTAMP(6)")], [], [], false);

    private static readonly RowKey PosKey = new RowKey.PrimaryKey(["A1 ", 7m]);
    private static readonly RowKey RowIdKey = new RowKey.RowId("AAAR3sAAEAAAACXAAA");

    /// <summary>Every bind variable must occur in the statement (ORA-01036 otherwise) and the other way round.</summary>
    private static void AssertBindsMatch(QuerySpec spec)
    {
        var inSql = Regex.Matches(spec.Sql, @":(\w+)").Select(m => m.Groups[1].Value).ToHashSet();
        Assert.Equal(inSql.Order(), spec.Parameters.Select(p => p.Name).Order());
    }

    [Fact]
    public void Update_sets_changed_columns_by_primary_key_with_column_bind_types()
    {
        var spec = DmlBuilder.Update(Position, PosKey, new Dictionary<int, object?> { [2] = 5m, [3] = "Ä", [4] = null });

        Assert.Equal(
            "UPDATE \"APP\".\"Pos Tabelle\"\n   SET \"MENGE\" = :v2,\n       \"Text\" = :v3,\n       \"DATUM\" = :v4\n WHERE \"MANDANT\" = :k0 AND \"NR\" = :k1",
            spec.Sql);
        Assert.Equal(
            [("v2", 5m, OracleTypeHint.Number), ("v3", "Ä", OracleTypeHint.NVarchar2), ("v4", null, OracleTypeHint.Date), ("k0", "A1 ", OracleTypeHint.Char), ("k1", 7m, OracleTypeHint.Number)],
            spec.Parameters.Select(p => (p.Name, p.Value, p.Type)));
        AssertBindsMatch(spec);
        Assert.True(OracleSession.IsWriteStatement(spec.Sql));
    }

    [Fact]
    public void Tables_without_primary_key_are_written_by_rowid()
    {
        var update = DmlBuilder.Update(Notizen, RowIdKey, new Dictionary<int, object?> { [0] = "neu" });
        var delete = DmlBuilder.Delete(Notizen, RowIdKey);

        Assert.EndsWith("WHERE ROWID = :k_rowid", update.Sql);
        Assert.Equal("DELETE FROM \"APP\".\"NOTIZEN\"\n WHERE ROWID = :k_rowid", delete.Sql);
        Assert.Equal("AAAR3sAAEAAAACXAAA", delete.Parameters.Single().Value);
        AssertBindsMatch(update);
        AssertBindsMatch(delete);
        Assert.True(OracleSession.IsWriteStatement(delete.Sql));
    }

    [Fact]
    public void Lock_selects_the_compared_columns_and_waits_at_most_n_seconds()
    {
        var spec = DmlBuilder.Lock(Position, PosKey, [2, 4], 3);

        Assert.Equal("SELECT \"MENGE\", \"DATUM\"\n  FROM \"APP\".\"Pos Tabelle\"\n WHERE \"MANDANT\" = :k0 AND \"NR\" = :k1\n   FOR UPDATE WAIT 3", spec.Sql);
        AssertBindsMatch(spec);
        Assert.True(OracleSession.IsLockStatement(spec.Sql));
        Assert.False(OracleSession.IsReadOnlyStatement(spec.Sql)); // never through the normal query path
        Assert.StartsWith("SELECT ROWID", DmlBuilder.Lock(Notizen, RowIdKey, [], 3).Sql);
        Assert.Throws<ArgumentOutOfRangeException>(() => DmlBuilder.Lock(Notizen, RowIdKey, [], 61));
    }

    [Fact]
    public void Insert_lists_only_given_columns_and_returns_the_rowid()
    {
        var spec = DmlBuilder.Insert(Position, new Dictionary<int, object?> { [1] = 8m, [0] = "A1" });

        Assert.Equal("INSERT INTO \"APP\".\"Pos Tabelle\" (\"MANDANT\", \"NR\")\nVALUES (:v0, :v1)\nRETURNING ROWID INTO :p_rowid", spec.Sql);
        var output = spec.Parameters.Single(p => p.Output);
        Assert.Equal((DmlBuilder.RowIdOutput, OracleTypeHint.RowId), (output.Name, output.Type));
        AssertBindsMatch(spec);
        Assert.True(OracleSession.IsWriteStatement(spec.Sql));

        Assert.Equal("INSERT INTO \"APP\".\"NOTIZEN\" (\"TXT\")\nVALUES (DEFAULT)\nRETURNING ROWID INTO :p_rowid", DmlBuilder.Insert(Notizen, new Dictionary<int, object?>()).Sql);
    }

    [Fact]
    public void Rows_without_key_or_with_null_key_cannot_be_written()
    {
        Assert.Throws<InvalidOperationException>(() => DmlBuilder.Delete(Notizen, RowKey.None.Instance));
        Assert.Throws<InvalidOperationException>(() => DmlBuilder.Delete(Position, new RowKey.PrimaryKey(["A1", null])));
    }

    [Fact]
    public void Values_that_look_like_sql_end_up_as_binds_only()
    {
        var spec = DmlBuilder.Update(Notizen, RowIdKey, new Dictionary<int, object?> { [0] = "x'; DROP TABLE t; --" });

        Assert.DoesNotContain("DROP", spec.Sql);
        Assert.True(OracleSession.IsWriteStatement(spec.Sql));
    }

    [Fact]
    public void Describe_lists_lock_and_dml_per_operation()
    {
        var tracker = new ChangeTracker(Position);
        tracker.SetValue(new RowData(PosKey, ["A1 ", 7m, 1m, "x", null]), 2, "2");
        tracker.Delete(new RowData(new RowKey.PrimaryKey(["A1 ", 9m]), ["A1 ", 9m, 1m, "y", null]));
        var added = tracker.AddRow();
        tracker.SetValue(added, 0, "B2");

        var statements = DmlBuilder.Describe(Position, tracker.PendingOperations(), 3);

        Assert.Equal(["SELECT", "DELETE", "SELECT", "UPDATE", "INSERT"], statements.Select(s => s.Sql.Split(' ')[0]));
    }

    [Fact]
    public void Row_reload_after_insert_selects_like_the_grid()
    {
        var query = QueryBuilder.BuildSelectByRowId(Position, "AAAR3s");

        Assert.Contains("WHERE t.ROWID = :p_rowid", query.Sql);
        Assert.True(query.HasRowId);
        Assert.True(OracleSession.IsReadOnlyStatement(query.Sql));
        AssertBindsMatch(new QuerySpec(query.Sql, query.Parameters));
    }

    [Theory]
    [InlineData("SELECT a FROM t WHERE id = :k0 FOR UPDATE WAIT 3")]
    [InlineData("select rowid from t where rowid = :k for update nowait;")]
    public void Lock_guard_accepts_bounded_waits(string sql) => Assert.True(OracleSession.IsLockStatement(sql), sql);

    [Theory]
    [InlineData("SELECT a FROM t FOR UPDATE")] // would wait forever
    [InlineData("SELECT a FROM t")]
    [InlineData("SELECT a FROM t FOR UPDATE WAIT 3; DELETE FROM t")]
    [InlineData("DELETE FROM t")]
    [InlineData("SELECT 'FOR UPDATE WAIT 3' FROM t")]
    [InlineData("SELECT a FROM t FOR UPDATE WAIT 1000")]
    [InlineData("SELECT a FROM t FOR UPDATE WAIT 1.5")]
    [InlineData("SELECT a FROM t -- FOR UPDATE NOWAIT")]
    public void Lock_guard_refuses_everything_else(string sql) => Assert.False(OracleSession.IsLockStatement(sql), sql);
}
