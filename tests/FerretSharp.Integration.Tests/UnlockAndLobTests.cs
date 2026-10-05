using FerretSharp.Core.Connections;
using FerretSharp.Core.Data;
using FerretSharp.Core.Oracle;
using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;
using Oracle.ManagedDataAccess.Client;

namespace FerretSharp.Integration.Tests;

/// <summary>WP-10: unlocking a locked session for writing and locking it again; whole LOB values read and written.</summary>
public sealed class UnlockAndLobTests(OracleContainerFixture oracle) : IAsyncLifetime
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static bool _created;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Owner => oracle.RequireProfile().Profile.EffectiveSchema;

    public async ValueTask InitializeAsync()
    {
        await Gate.WaitAsync(Ct);
        try
        {
            if (_created)
            {
                return;
            }

            await ExecuteAsync("CREATE TABLE UL_ROWS (ID NUMBER(10) PRIMARY KEY, TXT VARCHAR2(20)) SEGMENT CREATION IMMEDIATE");
            await ExecuteAsync("CREATE TABLE UL_LOBS (ID NUMBER(10) PRIMARY KEY, NOTIZ CLOB, NNOTIZ NCLOB, BILD BLOB) SEGMENT CREATION IMMEDIATE");
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

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new OracleConnection(oracle.RequireConnectionString());
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(Ct);
    }

    private Task<TableDetails> DetailsAsync(IDatabaseConnection connection, string table) =>
        connection.Schema.GetDetailsAsync(new TableSummary(Owner, table, TableKind.Table), Ct);

    private static int I(TableDetails table, string column) => table.Columns.ToList().FindIndex(c => c.Name == column);

    private static async Task FlushAsync(IDatabaseConnection connection, ChangeTracker tracker)
    {
        var operations = tracker.PendingOperations();
        var result = await connection.Editor.FlushAsync(tracker.Table, operations, new FlushOptions(), Ct);
        tracker.MarkFlushed(operations, result.NewKeys);
    }

    private static async Task<RowData> RowAsync(IDatabaseConnection connection, TableDetails table, int id)
    {
        var page = await connection.Data.ReadPageAsync(table, [FilterCondition.Of("ID", FilterOperator.Equals, id.ToString())], [], new PageSpec(0, 10), Ct);
        return Assert.Single(page.Rows);
    }

    [Fact]
    public async Task An_unlocked_session_writes_and_is_read_only_again_after_locking()
    {
        await using var session = await OpenAsync("Unlock");
        await using var check = await OpenAsync("Unlock check");
        var table = await DetailsAsync(session, "UL_ROWS");
        await session.UseReadOnlySnapshotsAsync(Ct);

        var tracker = new ChangeTracker(table);
        var row = tracker.AddRow();
        tracker.SetValue(row, I(table, "ID"), "1");
        tracker.SetValue(row, I(table, "TXT"), "freigeschaltet");
        await Assert.ThrowsAsync<InvalidOperationException>(() => FlushAsync(session, tracker)); // locked: no writing transaction

        await session.StopReadOnlySnapshotsAsync(Ct);
        Assert.False(session.UsesReadOnlySnapshots);
        Assert.Equal(TransactionMode.None, session.Transaction.Mode);
        await FlushAsync(session, tracker);
        await session.Editor.CommitAsync(Ct);

        Assert.Equal("freigeschaltet", (await RowAsync(check, table, 1)).Values[I(table, "TXT")]);

        await session.UseReadOnlySnapshotsAsync(Ct);
        Assert.True(session.UsesReadOnlySnapshots);
        Assert.Equal(TransactionMode.ReadOnly, session.Transaction.Mode);
        var again = new ChangeTracker(table);
        again.SetValue(await RowAsync(session, table, 1), I(table, "TXT"), "gesperrt");
        await Assert.ThrowsAsync<InvalidOperationException>(() => FlushAsync(session, again));
    }

    [Fact]
    public async Task Whole_lob_values_round_trip_and_set_to_null()
    {
        await using var session = await OpenAsync("LOB");
        await using var check = await OpenAsync("LOB check");
        var table = await DetailsAsync(session, "UL_LOBS");
        var text = string.Concat(Enumerable.Repeat("GrÃ¼ÃŸe ðŸ˜€ â€“ Zeile\n", 7000)); // > 100.000 characters, beyond 32 KB binds
        var national = "Î©Î¼Î­Î³Î± âœ“ ï¬";
        var bytes = new byte[300_000];
        new Random(42).NextBytes(bytes);

        var tracker = new ChangeTracker(table);
        var added = tracker.AddRow();
        tracker.SetValue(added, I(table, "ID"), "1");
        Assert.True(tracker.SetContent(added, I(table, "NOTIZ"), text).IsValid);
        Assert.True(tracker.SetContent(added, I(table, "NNOTIZ"), national).IsValid);
        Assert.True(tracker.SetContent(added, I(table, "BILD"), bytes).IsValid);
        await FlushAsync(session, tracker);
        await session.Editor.CommitAsync(Ct);

        var row = await RowAsync(check, table, 1);
        Assert.Equal(new LobValue(text[..200], text.Length), row.Values[I(table, "NOTIZ")]);
        Assert.Equal(text, (await check.Data.ReadLobAsync(table, row.Key, I(table, "NOTIZ"), Ct)).Value);
        Assert.Equal(national, (await check.Data.ReadLobAsync(table, row.Key, I(table, "NNOTIZ"), Ct)).Value);
        Assert.Equal(bytes, (byte[])(await check.Data.ReadLobAsync(table, row.Key, I(table, "BILD"), Ct)).Value!);

        var update = new ChangeTracker(table);
        var loaded = (await session.Data.ReadLobAsync(table, row.Key, I(table, "NOTIZ"), Ct)).Value;
        Assert.True(update.SetContent(row, I(table, "NOTIZ"), loaded, "kurz").IsValid);
        Assert.True(update.SetContent(row, I(table, "BILD"), bytes, null).IsValid);
        await FlushAsync(session, update);
        await session.Editor.CommitAsync(Ct);

        Assert.Equal(new LobRead(true, "kurz"), await check.Data.ReadLobAsync(table, row.Key, I(table, "NOTIZ"), Ct));
        Assert.Equal(new LobRead(true, null), await check.Data.ReadLobAsync(table, row.Key, I(table, "BILD"), Ct));
        Assert.False((await check.Data.ReadLobAsync(table, new RowKey.PrimaryKey([999m]), I(table, "NOTIZ"), Ct)).Found);
    }

    [Fact]
    public async Task A_lob_changed_by_someone_else_since_loading_is_a_conflict()
    {
        await using var session = await OpenAsync("LOB conflict");
        var table = await DetailsAsync(session, "UL_LOBS");
        await ExecuteAsync("INSERT INTO UL_LOBS (ID, NOTIZ) VALUES (2, 'Version 1')");
        var row = await RowAsync(session, table, 2);
        var loaded = (await session.Data.ReadLobAsync(table, row.Key, I(table, "NOTIZ"), Ct)).Value;

        await ExecuteAsync("UPDATE UL_LOBS SET NOTIZ = 'Version 2' WHERE ID = 2"); // same length, different text
        var tracker = new ChangeTracker(table);
        tracker.SetContent(row, I(table, "NOTIZ"), loaded, "meine Version");

        var conflict = await Assert.ThrowsAsync<ConcurrencyConflictException>(() => FlushAsync(session, tracker));

        var difference = Assert.Single(conflict.Differences);
        Assert.Equal("Version 1", difference.Expected);
        Assert.Equal("Version 2", difference.Actual);
        await session.Editor.RollbackAsync(Ct);
    }
}
