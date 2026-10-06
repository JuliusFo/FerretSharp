using FerretSharp.Core.Connections;
using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace FerretSharp.Core.Tests.Query;

/// <summary>What the SQL editor checks before it runs anything (R2: out of the view into the core).</summary>
public sealed class SqlScriptPlanTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static IReadOnlyList<SqlStatement> Script(string text) => SqlScript.Split(text);

    [Fact]
    public void A_script_is_numbered_and_bound()
    {
        var plan = SqlScriptPlan.Prepare(Script("SELECT * FROM t WHERE id = :id;\nUPDATE t SET a = 1 WHERE id = :id"),
            [new SqlVariable("id", SqlVariableType.Number, "4711")], writable: true, single: false);

        Assert.Null(plan.Problem);
        Assert.Equal([1, 2], plan.Statements.Select(s => s.Number));
        Assert.All(plan.Statements, s => Assert.Equal(4711m, Assert.Single(s.Query.Parameters).Value));
    }

    [Fact]
    public void A_single_statement_has_number_0_and_its_problem_without_prefix()
    {
        var single = SqlScriptPlan.Prepare(Script("DROP TABLE t"), [], writable: true, single: true);
        var script = SqlScriptPlan.Prepare(Script("SELECT 1 FROM dual;\nDROP TABLE t"), [], writable: true, single: false);

        Assert.StartsWith("DROP (DDL) führt der SQL-Editor nicht aus", single.Problem!.Message);
        Assert.StartsWith("Statement 2 – nichts ausgeführt: DROP (DDL)", script.Problem!.Message);
        Assert.Equal("DROP TABLE t", script.Problem.Statement.Text);
        Assert.Empty(script.Statements); // nothing runs, also not the SELECT before
    }

    [Fact]
    public void Dml_on_a_read_only_workspace_asks_for_unlocking()
    {
        var plan = SqlScriptPlan.Prepare(Script("SELECT 1 FROM dual;\nDELETE FROM t WHERE id = 1"), [], writable: false, single: false);

        Assert.True(plan.Problem!.NeedsUnlock);
        Assert.Contains("DELETE ändert Daten – der Workspace ist schreibgeschützt.", plan.Problem.Message);
    }

    [Fact]
    public void A_missing_value_stops_the_script()
    {
        var plan = SqlScriptPlan.Prepare(Script("SELECT * FROM t WHERE id = :id"), [new SqlVariable("id", SqlVariableType.Number, "")], writable: true, single: true);

        Assert.Equal(":id: Zahl fehlt (für NULL den Typ NULL wählen).", plan.Problem!.Message);
        Assert.False(plan.Problem.NeedsUnlock);
    }

    [Theory]
    [InlineData("SELECT * FROM t", false, false)]
    [InlineData("UPDATE t SET a = 1 WHERE id = 2", false, false)]
    [InlineData("UPDATE t SET a = 1 WHERE id = 2", true, true)] // DML on Prod: always
    [InlineData("DELETE FROM t", false, true)] // without WHERE: everywhere
    [InlineData("SELECT * FROM t", true, false)]
    public void Confirmation(string sql, bool prod, bool expected) =>
        Assert.Equal(expected, SqlScriptPlan.Prepare(Script(sql), [], writable: true, single: true).NeedsConfirmation(prod));

    [Theory]
    [InlineData("SELECT * FROM gibt_es_nicht", "ORA-00942", "Tabelle oder View \"FERRET\".\"GIBT_ES_NICHT\" ist nicht vorhanden", 14, 13)]
    [InlineData("SELECT k.nam FROM kunden k", "ORA-00904", "\"K\".\"NAM\": ungültiger Bezeichner", 9, 3)]
    [InlineData("SELECT * FROM kunden", "ORA-00001", "\"X\"", null, null)] // other errors: nothing to mark
    [InlineData("SELECT * FROM kunden", "ORA-00942", "\"ANDERS\"", null, null)] // name not in the statement
    public void Error_location(string statement, string code, string message, int? start, int? length) =>
        Assert.Equal(start is null ? null : (start.Value, length!.Value), SqlScriptPlan.LocateError(statement, code, message));

    [Fact]
    public async Task Bind_type_comes_from_the_column_it_is_compared_with()
    {
        var kunden = new TableSummary("APP", "KUNDEN", TableKind.Table);
        var reader = Substitute.For<ISchemaReader>();
        reader.GetTablesAsync("APP", Arg.Any<CancellationToken>()).Returns((IReadOnlyList<TableSummary>)[kunden]);
        reader.GetSynonymTargetsAsync("APP", Arg.Any<CancellationToken>()).Returns((IReadOnlyList<TableSummary>)[]);
        reader.GetForeignKeysAsync("APP", Arg.Any<CancellationToken>()).Returns((IReadOnlyList<ForeignKeyInfo>)[]);
        reader.GetDetailsAsync(kunden, Arg.Any<CancellationToken>()).Returns(new TableDetails(kunden,
            [
                new ColumnInfo("KUNDE_ID", "NUMBER", null, false, 10, 0, false, false, null, 1),
                new ColumnInfo("ERSTELLT_AM", "DATE", null, false, null, null, false, false, null, 2),
                new ColumnInfo("KUERZEL", "CHAR", 3, false, null, null, true, false, null, 3),
            ], ["KUNDE_ID"], [], false));
        var schema = new SchemaCache(reader, "APP");
        await schema.LoadAsync(Ct);
        var info = SqlScript.Analyze("SELECT * FROM kunden k WHERE k.kunde_id = :id AND erstellt_am > :von AND kuerzel = :k AND 1 = :x");

        Assert.Equal(SqlVariableType.Number, await SqlBinds.SuggestTypeAsync(info, "id", schema, Ct));
        Assert.Equal(SqlVariableType.Date, await SqlBinds.SuggestTypeAsync(info, "von", schema, Ct));
        Assert.Equal(SqlVariableType.Char, await SqlBinds.SuggestTypeAsync(info, "k", schema, Ct));
        Assert.Null(await SqlBinds.SuggestTypeAsync(info, "x", schema, Ct)); // not compared with a column

        reader.GetDetailsAsync(kunden, Arg.Any<CancellationToken>()).ThrowsAsync(new DatabaseException("weg"));
        var other = new SchemaCache(reader, "APP");
        await other.LoadAsync(Ct);
        Assert.Null(await SqlBinds.SuggestTypeAsync(info, "id", other, Ct)); // only a suggestion: no error
    }
}
