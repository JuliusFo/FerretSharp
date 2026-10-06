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

        var rows = await connection.Editor.ExecuteAsync(Prepare(Script, "MERGE"), Ct);

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
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => connection.Editor.ExecuteAsync(Prepare(Script, "MERGE"), Ct));

        Assert.NotNull(page.DataAsOf);
        Assert.Contains("schreibgeschützt", error.Message);
    }

    [Theory]
    [InlineData("DROP TABLE sx_neu")]
    [InlineData("BEGIN DELETE FROM sx_neu; END;")]
    [InlineData("COMMIT")]
    public async Task What_the_editor_rejects_the_session_refuses_too(string sql)
    {
        await using var connection = await OpenAsync("SQL guard");

        Assert.NotNull(SqlScript.Analyze(sql).Rejection);
        await Assert.ThrowsAsync<InvalidOperationException>(() => connection.Data.ReadSqlAsync(new QuerySpec(sql, []), 0, 1, Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => connection.Editor.ExecuteAsync(new QuerySpec(sql, []), Ct));
        await connection.Editor.RollbackAsync(Ct); // ExecuteAsync began a transaction before the guard refused
    }
}
