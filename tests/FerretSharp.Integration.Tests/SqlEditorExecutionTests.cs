using FerretSharp.Core.Connections;
using FerretSharp.Core.Data;
using FerretSharp.Core.Oracle;
using FerretSharp.Core.Query;
using Oracle.ManagedDataAccess.Client;

namespace FerretSharp.Integration.Tests;

/// <summary>
/// The free SQL editor (WP-17, ADR 0014) against Oracle: statements as the user writes them – split from a script, binds
/// by name (also twice and in another letter case), literals with semicolons – read through the read path, DML and MERGE
/// only in the workspace's transaction, nothing in a locked session.
/// </summary>
public sealed class SqlEditorExecutionTests(OracleContainerFixture oracle) : IAsyncLifetime
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static bool _created;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await Gate.WaitAsync(Ct);
        try
        {
            if (_created)
            {
                return;
            }

            await using var connection = new OracleConnection(oracle.RequireConnectionString());
            await connection.OpenAsync(Ct);
            foreach (var sql in new[]
            {
                "CREATE TABLE SX_KUNDE (ID NUMBER(10) PRIMARY KEY, NAME VARCHAR2(50 CHAR) NOT NULL, KUERZEL CHAR(3), ANGELEGT DATE, STATUS VARCHAR2(20))",
                "INSERT INTO SX_KUNDE SELECT LEVEL, 'Kunde ' || LEVEL, 'K' || LEVEL, DATE '2026-10-01' + LEVEL, 'AKTIV' FROM DUAL CONNECT BY LEVEL <= 9",
                "CREATE TABLE SX_NEU (ID NUMBER(10) PRIMARY KEY, NAME VARCHAR2(50 CHAR))",
                "INSERT INTO SX_NEU VALUES (1, 'Erste; neu')",
                "INSERT INTO SX_NEU VALUES (42, 'Zweiundvierzig')",
                "COMMIT",
            })
            {
                await using var command = connection.CreateCommand();
                command.CommandText = sql;
                await command.ExecuteNonQueryAsync(Ct);
            }

            _created = true;
        }
        finally
        {
            Gate.Release();
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async Task<IDatabaseConnection> OpenAsync(string action)
    {
        var (profile, password) = oracle.RequireProfile();
        return await new OracleDatabaseConnector().OpenAsync(profile, password, action, Ct);
    }

    /// <summary>The statement at the cursor, bound as the editor does it.</summary>
    private static QuerySpec Prepare(string script, string at, params SqlVariable[] variables)
    {
        var cursor = script.IndexOf(at, StringComparison.Ordinal);
        var statement = SqlScript.StatementAt(script, cursor, cursor)!;
        var info = SqlScript.Analyze(statement.Text);
        Assert.Null(info.Rejection);
        return SqlBinds.Bind(statement.Text, info.Binds, variables);
    }

    private const string Script = """
        -- Kunden ab einem Tag, mit Kürzel
        SELECT id, name, kuerzel FROM sx_kunde
         WHERE angelegt >= :ab AND (kuerzel = :k OR :K IS NULL) AND name <> 'x;y'
         ORDER BY id;

        select q'[a;b]' as text, :Id * 2 as doppelt from dual where :id > 0
        /
        MERGE INTO sx_kunde k USING sx_neu n ON (k.id = n.id)
         WHEN MATCHED THEN UPDATE SET k.name = n.name
         WHEN NOT MATCHED THEN INSERT (id, name, status) VALUES (n.id, n.name, 'NEU');
        """;

    [Fact]
    public async Task Statements_of_a_script_run_with_their_binds()
    {
        await using var connection = await OpenAsync("SQL read");

        var customers = await connection.Data.ReadSqlAsync(Prepare(Script, "Kunden ab",
            new SqlVariable("ab", SqlVariableType.Date, "08.10.2026"), new SqlVariable("k", SqlVariableType.Null, "")), 0, 100, Ct);
        var dual = await connection.Data.ReadSqlAsync(Prepare(Script, "q'[", new SqlVariable("id", SqlVariableType.Number, "21")), 0, 10, Ct);

        Assert.Equal([7m, 8m, 9m], customers.Rows.Select(r => r[0]));
        Assert.Equal(["a;b", 42m], dual.Rows.Single());
    }

    [Fact]
    public async Task Char_binds_find_blank_padded_values_text_binds_do_not()
    {
        await using var connection = await OpenAsync("SQL char");
        const string sql = "SELECT id FROM sx_kunde WHERE kuerzel = :k";

        var asChar = await connection.Data.ReadSqlAsync(SqlBinds.Bind(sql, ["k"], [new SqlVariable("k", SqlVariableType.Char, "K3")]), 0, 10, Ct);
        var asText = await connection.Data.ReadSqlAsync(SqlBinds.Bind(sql, ["k"], [new SqlVariable("k", SqlVariableType.Text, "K3")]), 0, 10, Ct);

        Assert.Equal([3m], asChar.Rows.Select(r => r[0]));
        Assert.Empty(asText.Rows);
    }

    [Fact]
    public async Task Merge_runs_in_the_workspace_transaction_until_rolled_back()
    {
        await using var connection = await OpenAsync("SQL merge");
        var count = new QuerySpec("SELECT COUNT(*) FROM sx_kunde WHERE status = 'NEU' OR name = 'Erste; neu'", []);

        var rows = (await connection.Editor.ExecuteAsync(Prepare(Script, "MERGE"), Ct)).Rows;

        Assert.Equal(2, rows); // id 1 updated, id 42 inserted
        Assert.Equal(TransactionMode.ReadWrite, connection.Editor.Transaction.Mode);
        Assert.Equal(2m, (await connection.Data.ReadSqlAsync(count, 0, 1, Ct)).Rows[0][0]);
        await connection.Editor.RollbackAsync(Ct);
        Assert.Equal(0m, (await connection.Data.ReadSqlAsync(count, 0, 1, Ct)).Rows[0][0]);
    }

    [Fact]
    public async Task A_locked_session_reads_in_a_snapshot_and_refuses_merge()
    {
        await using var connection = await OpenAsync("SQL locked");
        await connection.UseReadOnlySnapshotsAsync(Ct);

        var page = await connection.Data.ReadSqlAsync(new QuerySpec("SELECT COUNT(*) FROM sx_kunde", []), 0, 1, Ct);
        var error = await Assert.ThrowsAsync<RefusedException>(() => connection.Editor.ExecuteAsync(Prepare(Script, "MERGE"), Ct));

        Assert.NotNull(page.DataAsOf);
        Assert.Contains("schreibgeschützt", error.Message);
    }

    [Theory]
    [InlineData("TRUNCATE TABLE sx_neu")]
    [InlineData("CREATE OR REPLACE PROCEDURE sx_p AS BEGIN DELETE FROM sx_neu; END;")]
    [InlineData("ALTER TABLE sx_neu MODIFY name DEFAULT :n")]
    [InlineData("BEGIN DELETE FROM sx_neu; END;")]
    [InlineData("COMMIT")]
    public async Task What_the_editor_rejects_the_session_refuses_too(string sql)
    {
        await using var connection = await OpenAsync("SQL guard");

        Assert.NotNull(SqlScript.Analyze(sql).Rejection);
        await Assert.ThrowsAsync<InvalidOperationException>(() => connection.Data.ReadSqlAsync(new QuerySpec(sql, []), 0, 1, Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => connection.Editor.ExecuteDdlAsync(sql, Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => connection.Editor.ExecuteAsync(new QuerySpec(sql, []), Ct));
        await connection.Editor.RollbackAsync(Ct); // ExecuteAsync began a transaction before the guard refused
    }

    /// <summary>WP-22: DDL from the editor runs outside of any transaction – committed at once, visible to every session.</summary>
    [Fact]
    public async Task Ddl_runs_outside_of_a_transaction_and_is_committed_at_once()
    {
        await using var connection = await OpenAsync("SQL ddl");
        await using var other = await OpenAsync("SQL ddl other");
        var script = SqlScriptPlan.Prepare(SqlScript.Split("""
            CREATE TABLE sx_ddl (id NUMBER(10) PRIMARY KEY, name VARCHAR2(20 CHAR));
            COMMENT ON TABLE sx_ddl IS 'Kunde; neu';
            INSERT INTO sx_ddl VALUES (1, 'eins')
            """), [], writable: true, single: false);
        Assert.Null(script.Problem);

        foreach (var statement in script.Statements)
        {
            if (statement.Info.IsDdl)
            {
                await connection.Editor.ExecuteDdlAsync(statement.Query.Sql, Ct);
                Assert.Equal(TransactionMode.None, connection.Editor.Transaction.Mode);
            }
            else
            {
                await connection.Editor.ExecuteAsync(statement.Query, Ct);
            }
        }

        var comment = await other.Data.ReadSqlAsync(new QuerySpec("SELECT comments FROM user_tab_comments WHERE table_name = 'SX_DDL'", []), 0, 1, Ct);
        var rows = await other.Data.ReadSqlAsync(new QuerySpec("SELECT COUNT(*) FROM sx_ddl", []), 0, 1, Ct);
        Assert.Equal("Kunde; neu", comment.Rows.Single()[0]);
        Assert.Equal(0m, rows.Rows.Single()[0]); // the INSERT after the DDL waits in the transaction for the user's commit
        Assert.Equal(TransactionMode.ReadWrite, connection.Editor.Transaction.Mode);

        await connection.Editor.RollbackAsync(Ct);
        await connection.Editor.ExecuteDdlAsync("DROP TABLE sx_ddl PURGE", Ct);
    }

    [Fact]
    public async Task Ddl_is_refused_while_the_workspace_has_written_and_in_a_locked_session()
    {
        await using var connection = await OpenAsync("SQL ddl tx");
        await connection.Editor.ExecuteAsync(new QuerySpec("UPDATE sx_kunde SET status = status WHERE id = 9", []), Ct);

        var open = await Assert.ThrowsAsync<RefusedException>(() => connection.Editor.ExecuteDdlAsync("CREATE TABLE sx_nie (id NUMBER)", Ct));

        Assert.Contains("ohne offene Transaktion", open.Message);
        Assert.Equal(TransactionMode.ReadWrite, connection.Editor.Transaction.Mode); // nothing was committed
        Assert.Single(connection.Editor.Actions);
        await connection.Editor.RollbackAsync(Ct);

        await connection.UseReadOnlySnapshotsAsync(Ct);
        var locked = await Assert.ThrowsAsync<RefusedException>(() => connection.Editor.ExecuteDdlAsync("CREATE TABLE sx_nie (id NUMBER)", Ct));
        Assert.Contains("schreibgeschützt", locked.Message);
    }

    /// <summary>
    /// A table another workspace has written to and not committed: DROP does not wait for its locks (DDL_LOCK_TIMEOUT 0) and
    /// fails at once with ORA-00054 – the message says what to do. ALTER TABLE … ADD however waits for that transaction
    /// (Oracle 23: "enq: TX - row lock contention", found when this test hung) – and neither <c>OracleCommand.Cancel</c>,
    /// a command timeout nor closing the session stops it (tried in WP-22): it runs once the other transaction ends.
    /// </summary>
    [Fact]
    public async Task Ddl_on_a_table_another_session_has_written_to_fails_or_waits_for_that_transaction()
    {
        await using var writer = await OpenAsync("SQL busy writer");
        await using var ddl = await OpenAsync("SQL busy ddl");
        await ddl.Editor.ExecuteDdlAsync("CREATE TABLE sx_busy (id NUMBER(10))", Ct);
        await writer.Editor.ExecuteAsync(new QuerySpec("INSERT INTO sx_busy VALUES (1)", []), Ct);

        var error = await Assert.ThrowsAsync<DatabaseException>(() => ddl.Editor.ExecuteDdlAsync("DROP TABLE sx_busy PURGE", Ct));
        Assert.Equal("ORA-00054", error.ErrorCode);
        Assert.Contains("anderer Workspace", error.Message);

        var alter = ddl.Editor.ExecuteDdlAsync("ALTER TABLE sx_busy ADD (name VARCHAR2(10))", Ct);
        await Task.Delay(TimeSpan.FromSeconds(2), Ct);
        Assert.False(alter.IsCompleted); // waits for the writer's transaction

        await writer.Editor.RollbackAsync(Ct);
        await alter.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        var columns = await writer.Data.ReadSqlAsync(new QuerySpec("SELECT COUNT(*) FROM user_tab_columns WHERE table_name = 'SX_BUSY'", []), 0, 1, Ct);
        Assert.Equal(2m, columns.Rows.Single()[0]);
        await ddl.Editor.ExecuteDdlAsync("DROP TABLE sx_busy PURGE", Ct);
    }
}
