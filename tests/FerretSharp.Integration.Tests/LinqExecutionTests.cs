using FerretSharp.Core.Connections;
using FerretSharp.Core.Data;
using FerretSharp.Core.Oracle;
using FerretSharp.Core.Query;
using Oracle.ManagedDataAccess.Client;

namespace FerretSharp.Integration.Tests;

/// <summary>
/// SQL from the LINQ console (ADR 0011) in a workspace session: run as EF wrote it – joins with duplicate column names,
/// named binds – read page by page, written only in the workspace's transaction, never in a locked one.
/// </summary>
public sealed class LinqExecutionTests(OracleContainerFixture oracle) : IAsyncLifetime
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static bool _created;

    // Like EF Core with the Oracle provider: quoted names, aliases, ":name_0" binds, the key twice in a join.
    private const string JoinSql = """
        SELECT "k"."ID", "k"."NAME", "k"."ANGELEGT", "k"."NOTIZ", "a"."ID", "a"."BETRAG"
        FROM "LX_KUNDE" "k"
        INNER JOIN "LX_AUFTRAG" "a" ON "k"."ID" = "a"."KUNDE_ID"
        WHERE "a"."BETRAG" >= :betrag_0
        ORDER BY "a"."ID"
        """;

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
                "CREATE TABLE LX_KUNDE (ID NUMBER(10) PRIMARY KEY, NAME VARCHAR2(50 CHAR) NOT NULL, ANGELEGT DATE, NOTIZ CLOB)",
                "CREATE TABLE LX_AUFTRAG (ID NUMBER(10) PRIMARY KEY, KUNDE_ID NUMBER(10), BETRAG NUMBER(12,2), STATUS VARCHAR2(20))",
                "INSERT INTO LX_KUNDE VALUES (1, 'Erika', DATE '2026-10-05', TO_CLOB(RPAD('x', 4000, 'x')) || TO_CLOB(RPAD('x', 1000, 'x')))",
                "INSERT INTO LX_KUNDE VALUES (2, 'Max', NULL, NULL)",
                "INSERT INTO LX_AUFTRAG SELECT LEVEL, MOD(LEVEL, 2) + 1, LEVEL * 10.5, 'OFFEN' FROM DUAL CONNECT BY LEVEL <= 5",
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

    [Fact]
    public async Task A_join_with_duplicate_column_names_reads_page_by_page_with_its_types()
    {
        await using var connection = await OpenAsync("LINQ read");
        var query = new QuerySpec(JoinSql, [new QueryParameter("betrag_0", 20m, OracleTypeHint.Number)]);

        var first = await connection.Data.ReadSqlAsync(query, 0, 2, Ct);
        var second = await connection.Data.ReadSqlAsync(query, 2, 2, Ct);
        var beyond = await connection.Data.ReadSqlAsync(query, 4, 2, Ct);

        Assert.Equal(["ID", "NAME", "ANGELEGT", "NOTIZ", "ID", "BETRAG"], first.Columns.Select(c => c.Name));
        Assert.Equal(["NUMBER(10)", "VARCHAR2(50)", "DATE", "CLOB", "NUMBER(10)", "NUMBER(12,2)"], first.Columns.Select(c => c.Column.DisplayType));
        Assert.Equal([2m, 3m], first.Rows.Select(r => r[4]));
        Assert.False(first.IsLastPage);
        Assert.Equal([4m, 5m], second.Rows.Select(r => r[4]));
        Assert.True(second.IsLastPage);
        Assert.Empty(beyond.Rows);
        Assert.True(beyond.IsLastPage);
        // The CLOB arrives as in the table grid: a preview and its length (WP-17).
        var clob = Assert.IsType<LobValue>(first.Rows.Single(r => (decimal)r[0]! == 1m)[3]);
        Assert.Equal((5000L, QueryBuilder.ClobPreviewLength), (clob.Length, clob.Preview!.Length));
    }

    [Fact]
    public async Task Only_plain_queries_pass_the_read_path()
    {
        await using var connection = await OpenAsync("LINQ tripwire");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            connection.Data.ReadSqlAsync(new QuerySpec("DELETE FROM \"LX_AUFTRAG\"", []), 0, 10, Ct));
    }

    [Fact]
    public async Task An_update_runs_in_the_workspace_transaction_visible_there_until_rolled_back()
    {
        await using var connection = await OpenAsync("LINQ write");
        var update = new QuerySpec("""UPDATE "LX_AUFTRAG" "a" SET "a"."STATUS" = N'STORNIERT' WHERE "a"."KUNDE_ID" = :kunde_0""",
            [new QueryParameter("kunde_0", 2, OracleTypeHint.Number)]);
        var status = new QuerySpec("SELECT COUNT(*) FROM \"LX_AUFTRAG\" WHERE \"STATUS\" = 'STORNIERT'", []);

        var rows = await connection.Editor.ExecuteAsync(update, Ct);

        Assert.Equal(3, rows); // orders 1, 3 and 5
        Assert.Equal(TransactionMode.ReadWrite, connection.Editor.Transaction.Mode);
        Assert.Equal(3m, (await connection.Data.ReadSqlAsync(status, 0, 1, Ct)).Rows[0][0]);
        await connection.Editor.RollbackAsync(Ct);
        Assert.Equal(0m, (await connection.Data.ReadSqlAsync(status, 0, 1, Ct)).Rows[0][0]);
    }

    [Fact]
    public async Task A_locked_session_refuses_the_update()
    {
        await using var connection = await OpenAsync("LINQ locked");
        await connection.UseReadOnlySnapshotsAsync(Ct);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => connection.Editor.ExecuteAsync(
            new QuerySpec("""UPDATE "LX_AUFTRAG" "a" SET "a"."STATUS" = N'STORNIERT'""", []), Ct));

        Assert.Contains("schreibgeschützt", error.Message);
        Assert.Equal(TransactionMode.ReadOnly, connection.Editor.Transaction.Mode);
    }
}
