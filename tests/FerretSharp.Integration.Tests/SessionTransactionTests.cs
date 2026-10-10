using FerretSharp.Core.Connections;
using FerretSharp.Core.Data;
using FerretSharp.Core.Oracle;
using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;
using Oracle.ManagedDataAccess.Client;

namespace FerretSharp.Integration.Tests;

/// <summary>Transactions of <see cref="OracleSession"/> (WP-08): locked read-only snapshots and writing transactions.</summary>
public sealed class SessionTransactionTests(OracleContainerFixture oracle) : IAsyncLifetime
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static bool _created;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await Gate.WaitAsync(Ct);
        try
        {
            if (!_created)
            {
                await using var connection = await OpenRawAsync();
                await ExecuteAsync(connection, "CREATE TABLE TXS (ID NUMBER PRIMARY KEY, TXT VARCHAR2(20)) SEGMENT CREATION IMMEDIATE");
                await ExecuteAsync(connection, "INSERT INTO TXS VALUES (10, 'zehn')");
                await ExecuteAsync(connection, "INSERT INTO TXS VALUES (20, 'zwanzig')");
                await Task.Delay(3000, Ct); // a snapshot right after CREATE TABLE fails with ORA-01466
                _created = true;
            }
        }
        finally
        {
            Gate.Release();
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async Task<OracleSession> OpenAsync()
    {
        var (profile, password) = oracle.RequireProfile();
        return await OracleSession.OpenAsync(OracleConnectionStringFactory.Create(profile, password), new SessionContext("FerretSharp", "Tx tests"), Ct);
    }

    private async Task<OracleConnection> OpenRawAsync()
    {
        var connection = new OracleConnection(oracle.RequireConnectionString());
        await connection.OpenAsync(Ct);
        return connection;
    }

    private static async Task ExecuteAsync(OracleConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(Ct);
    }

    private static Task<long> CountAsync(OracleSession session, string table, string where) =>
        session.ExecuteReaderAsync($"SELECT COUNT(*) FROM {table} WHERE {where}", [], async (reader, ct) =>
        {
            await reader.ReadAsync(ct);
            return Convert.ToInt64(reader.GetValue(0));
        }, Ct);

    private async Task<long> CountRawAsync(string where)
    {
        await using var connection = await OpenRawAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM TXS WHERE " + where;
        return Convert.ToInt64(await command.ExecuteScalarAsync(Ct));
    }

    /// <summary>WP-08 is done when this holds: on a locked (Prod) session Oracle itself refuses DML.</summary>
    [Fact]
    public async Task Oracle_rejects_dml_in_a_locked_session()
    {
        await using var session = await OpenAsync();
        await session.UseReadOnlySnapshotsAsync(Ct);

        var error = await Assert.ThrowsAsync<DatabaseException>(
            () => session.ExecuteNonQueryAsync("INSERT INTO TXS VALUES (99, 'verboten')", [], Ct));

        Assert.Equal("ORA-01456", error.ErrorCode); // may not perform insert/delete/update operation inside a READ ONLY transaction
        Assert.Equal(TransactionMode.ReadOnly, session.Transaction.Mode);
        Assert.Equal(0, await CountRawAsync("ID = 99"));
    }

    [Fact]
    public async Task Locked_session_reads_a_snapshot_until_it_is_refreshed()
    {
        await using var session = await OpenAsync();
        await session.UseReadOnlySnapshotsAsync(Ct);
        var first = session.Transaction.StartedAt;
        Assert.Equal(0, await CountAsync(session, "TXS", "ID = 30"));

        await using (var writer = await OpenRawAsync())
        {
            await ExecuteAsync(writer, "INSERT INTO TXS VALUES (30, 'neu')");
        }

        Assert.Equal(0, await CountAsync(session, "TXS", "ID = 30"));
        await Task.Delay(20, Ct);
        await session.RefreshSnapshotAsync(Ct);
        Assert.Equal(1, await CountAsync(session, "TXS", "ID = 30"));
        Assert.True(session.Transaction.StartedAt > first);

        await using var cleanup = await OpenRawAsync();
        await ExecuteAsync(cleanup, "DELETE FROM TXS WHERE ID = 30");
    }

    [Fact]
    public async Task First_page_starts_a_new_snapshot_later_pages_stay_in_it()
    {
        await using var session = await OpenAsync();
        await session.UseReadOnlySnapshotsAsync(Ct);
        var (profile, _) = oracle.RequireProfile();
        var table = new TableDetails(
            new TableSummary(profile.EffectiveSchema, "TXS", TableKind.Table),
            [new ColumnInfo("ID", "NUMBER", null, false, null, null, false, false, null, 1), new ColumnInfo("TXT", "VARCHAR2", 20, false, null, null, true, false, null, 2)],
            ["ID"], [], false);
        IDataAccess data = new OracleDataAccess(session);
        var sorts = new[] { new SortSpec("ID", false) };
        var ids = (RowPage page) => page.Rows.Select(r => Convert.ToInt32(r.Values[0])).ToList();

        var first = await data.ReadPageAsync(table, [], sorts, new PageSpec(0, 10), Ct);
        Assert.Equal([10, 20], ids(first));
        Assert.NotNull(first.DataAsOf);

        await using (var writer = await OpenRawAsync())
        {
            await ExecuteAsync(writer, "INSERT INTO TXS VALUES (15, 'dazwischen')");
        }

        var second = await data.ReadPageAsync(table, [], sorts, new PageSpec(1, 10), Ct); // next page: same snapshot
        Assert.Equal([20], ids(second));
        Assert.Equal(first.DataAsOf, second.DataAsOf);

        await Task.Delay(20, Ct);
        var again = await data.ReadPageAsync(table, [], sorts, new PageSpec(0, 10), Ct); // new query: new snapshot
        Assert.Equal([10, 15, 20], ids(again));
        Assert.True(again.DataAsOf > first.DataAsOf);

        await using var cleanup = await OpenRawAsync();
        await ExecuteAsync(cleanup, "DELETE FROM TXS WHERE ID = 15");
    }

    /// <summary>
    /// The first row of a table with deferred segment creation, inserted while the snapshot is open, makes the
    /// snapshot fail (ORA-08176; ORA-01466 right after CREATE TABLE) – the session retries in a new snapshot.
    /// </summary>
    [Fact]
    public async Task Snapshot_that_cannot_read_a_new_segment_is_restarted()
    {
        var name = "TXS_NEU_" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        await using var writer = await OpenRawAsync();
        await ExecuteAsync(writer, $"CREATE TABLE {name} (ID NUMBER)");
        try
        {
            await using var session = await OpenAsync();
            await session.UseReadOnlySnapshotsAsync(Ct);
            await ExecuteAsync(writer, $"INSERT INTO {name} VALUES (1)");

            var count = await CountAsync(session, name, "1 = 1"); // must not throw

            Assert.InRange(count, 0, 1);
            Assert.Equal(TransactionMode.ReadOnly, session.Transaction.Mode);
        }
        finally
        {
            await ExecuteAsync(writer, $"DROP TABLE {name} PURGE");
        }
    }

    [Fact]
    public async Task Writing_transaction_with_savepoint_commit_and_rollback()
    {
        await using var session = await OpenAsync();
        await session.BeginTransactionAsync(Ct);
        Assert.Equal(TransactionMode.ReadWrite, session.Transaction.Mode);

        await session.ExecuteNonQueryAsync("INSERT INTO TXS VALUES (:id, 'eins')", [new QueryParameter("id", 41m, OracleTypeHint.Number)], Ct);
        await session.SavepointAsync("NACH_EINS", Ct);
        await session.ExecuteNonQueryAsync("INSERT INTO TXS VALUES (42, 'zwei')", [], Ct);
        Assert.Equal(0, await CountRawAsync("ID IN (41, 42)")); // not committed: invisible to others

        await session.RollbackToSavepointAsync("NACH_EINS", Ct);
        await session.CommitAsync(Ct);

        Assert.Equal(TransactionMode.None, session.Transaction.Mode);
        Assert.Equal(1, await CountRawAsync("ID = 41"));
        Assert.Equal(0, await CountRawAsync("ID = 42"));

        await session.BeginTransactionAsync(Ct);
        await session.ExecuteNonQueryAsync("DELETE FROM TXS WHERE ID = 41", [], Ct);
        await session.RollbackAsync(Ct);
        Assert.Equal(1, await CountRawAsync("ID = 41"));

        await using var cleanup = await OpenRawAsync();
        await ExecuteAsync(cleanup, "DELETE FROM TXS WHERE ID = 41");
    }

    /// <summary>
    /// A deferred constraint fails only at commit (ORA-02091) – Oracle has rolled the transaction back by then. The
    /// session and the editor must not claim an open transaction afterwards (undo would end in ORA-01086).
    /// </summary>
    [Fact]
    public async Task Failed_commit_that_oracle_rolled_back_leaves_no_transaction()
    {
        var name = "TXS_DEF_" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        await using var writer = await OpenRawAsync();
        await ExecuteAsync(writer, $"CREATE TABLE {name} (ID NUMBER CONSTRAINT {name}_UQ UNIQUE DEFERRABLE INITIALLY DEFERRED)");
        try
        {
            await using var session = await OpenAsync();
            IDataEditor editor = new OracleDataEditor(session);
            await editor.ExecuteAsync(new QuerySpec($"INSERT INTO {name} VALUES (1)", []), Ct);
            await editor.ExecuteAsync(new QuerySpec($"INSERT INTO {name} VALUES (1)", []), Ct); // fine until commit

            var error = await Assert.ThrowsAsync<DatabaseException>(() => editor.CommitAsync(Ct));

            Assert.Equal("ORA-02091", error.ErrorCode);
            Assert.Equal(TransactionMode.None, session.Transaction.Mode);
            Assert.Equal(TransactionMode.None, editor.Transaction.Mode);
            await editor.ExecuteAsync(new QuerySpec($"INSERT INTO {name} VALUES (2)", []), Ct); // a new transaction starts
            await editor.CommitAsync(Ct);

            await using var check = writer.CreateCommand();
            check.CommandText = $"SELECT COUNT(*) FROM {name}";
            Assert.Equal(1, Convert.ToInt32(await check.ExecuteScalarAsync(Ct)));
        }
        finally
        {
            await ExecuteAsync(writer, $"DROP TABLE {name} PURGE");
        }
    }

    [Fact]
    public async Task Disposing_a_session_rolls_its_open_transaction_back()
    {
        await using (var session = await OpenAsync())
        {
            await session.BeginTransactionAsync(Ct);
            await session.ExecuteNonQueryAsync("INSERT INTO TXS VALUES (50, 'offen')", [], Ct);
        }

        Assert.Equal(0, await CountRawAsync("ID = 50"));
    }

    [Fact]
    public async Task Writing_needs_a_transaction_a_dml_statement_and_an_unlocked_session()
    {
        await using var session = await OpenAsync();

        await Assert.ThrowsAsync<RefusedException>(() => session.ExecuteNonQueryAsync("INSERT INTO TXS VALUES (60, 'auto')", [], Ct));
        await session.BeginTransactionAsync(Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.ExecuteNonQueryAsync("DROP TABLE TXS", [], Ct));
        await session.RollbackAsync(Ct);

        await session.UseReadOnlySnapshotsAsync(Ct);
        await Assert.ThrowsAsync<RefusedException>(() => session.BeginTransactionAsync(Ct));
        await session.RollbackAsync(Ct); // locked: continues in a new snapshot
        Assert.Equal(TransactionMode.ReadOnly, session.Transaction.Mode);
        Assert.Equal(0, await CountRawAsync("ID = 60"));
    }

    /// <summary>WP-22, ADR 0019: the schema path takes a single DDL statement – never inside a transaction, never locked.</summary>
    [Fact]
    public async Task Ddl_needs_no_open_transaction_a_ddl_statement_and_an_unlocked_session()
    {
        await using var session = await OpenAsync();

        await session.ExecuteDdlAsync("CREATE TABLE TXS_DDL (ID NUMBER)", Ct);
        await session.ExecuteDdlAsync("COMMENT ON TABLE TXS_DDL IS 'a;b'", Ct);
        Assert.Equal(TransactionMode.None, session.Transaction.Mode);
        await using (var raw = await OpenRawAsync())
        await using (var command = raw.CreateCommand())
        {
            command.CommandText = "SELECT comments FROM user_tab_comments WHERE table_name = 'TXS_DDL'";
            Assert.Equal("a;b", await command.ExecuteScalarAsync(Ct)); // committed: another session sees it
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => session.ExecuteDdlAsync("TRUNCATE TABLE TXS_DDL", Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.ExecuteDdlAsync("DELETE FROM TXS_DDL", Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.ExecuteDdlAsync("CREATE OR REPLACE PROCEDURE TXS_P AS BEGIN NULL; END;", Ct));

        await session.BeginTransactionAsync(Ct);
        await session.ExecuteNonQueryAsync("INSERT INTO TXS VALUES (70, 'offen')", [], Ct);
        await Assert.ThrowsAsync<RefusedException>(() => session.ExecuteDdlAsync("COMMENT ON TABLE TXS_DDL IS 'nie'", Ct));
        await session.RollbackAsync(Ct);
        Assert.Equal(0, await CountRawAsync("ID = 70")); // the refused DDL did not commit the insert

        await session.ExecuteDdlAsync("DROP TABLE TXS_DDL PURGE", Ct);
        await session.UseReadOnlySnapshotsAsync(Ct);
        await Assert.ThrowsAsync<RefusedException>(() => session.ExecuteDdlAsync("CREATE TABLE TXS_NIE (ID NUMBER)", Ct));
        Assert.Equal(TransactionMode.ReadOnly, session.Transaction.Mode);
    }
}
