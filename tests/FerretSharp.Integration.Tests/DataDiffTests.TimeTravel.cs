using FerretSharp.Core.Connections;
using FerretSharp.Core.Data;
using FerretSharp.Core.Oracle;
using FerretSharp.Core.Query;
using Oracle.ManagedDataAccess.Client;

namespace FerretSharp.Integration.Tests;

/// <summary>Points in the past against Oracle (WP-78, ADR 0048): reading AS OF an SCN, a time turned into an SCN, setting rows back.</summary>
public sealed partial class DataDiffTests
{
    [Fact]
    public async Task The_read_path_as_of_an_scn_shows_the_filter_as_it_was_with_whole_lobs_by_key()
    {
        await CreateAsync("TT_ORDERS", "ID NUMBER(10) PRIMARY KEY, STATUS VARCHAR2(20), NOTE CLOB",
            "INSERT INTO TT_ORDERS VALUES (1, 'open', 'first note')", "INSERT INTO TT_ORDERS VALUES (2, 'open', NULL)");
        var table = await DetailsAsync("TT_ORDERS");
        var scn = await StartAfterAsync("TT_ORDERS");
        await RunAsync("UPDATE TT_ORDERS SET STATUS = 'done', NOTE = 'changed' WHERE ID = 1", "DELETE FROM TT_ORDERS WHERE ID = 2");
        var open = new FilterCondition("STATUS", FilterOperator.Equals, ["open"]);
        var then = _data.AsOf(scn);

        var page = await then.ReadPageAsync(table, [open], [], new PageSpec(0, 10), Ct);

        Assert.Equal([1m, 2m], page.Rows.Select(r => r.Values[0]));
        Assert.Null(page.DataAsOf); // the rows are of the SCN, not of a snapshot
        Assert.Equal(2L, await then.CountAsync(table, [open], Ct));
        Assert.Equal(0L, await _data.CountAsync(table, [open], Ct));
        var row = Assert.Single(await then.ReadByKeysAsync(table, [0], [[1m]], wholeLobs: true, maxRows: null, Ct));
        Assert.Equal("first note", row.Values[2]);
        Assert.Equal("first note", (await then.ReadLobAsync(table, row.Key, 2, Ct)).Value);

        // The read session of a read-only profile (Prod) runs in read-only transactions: AS OF works there too.
        var (profile, password) = oracle.RequireProfile();
        await using var locked = await OracleSession.OpenAsync(
            OracleConnectionStringFactory.Create(profile, password), new SessionContext("FerretSharp", "Time travel locked"), Ct);
        await locked.UseReadOnlySnapshotsAsync(Ct);
        var lockedPage = await new OracleDataAccess(locked).AsOf(scn).ReadPageAsync(table, [open], [], new PageSpec(0, 10), Ct);
        Assert.Equal([1m, 2m], lockedPage.Rows.Select(r => r.Values[0]));
    }

    [Fact]
    public async Task A_time_before_now_becomes_an_scn_of_the_same_database_and_the_rights_decide_how_exact_now_is()
    {
        var now = await _data.CurrentPointAsync(exact: false, Ct);
        var back = await _data.PointBackAsync(TimeSpan.FromMinutes(1), Ct);

        Assert.True(back.Scn <= now.Scn);
        Assert.Equal(now.Database, back.Database);
        Assert.Matches("^[^/]+/[^/]+$", now.Database);
        TestContext.Current.SendDiagnosticMessage($"Database {now.Database}, SCN now {now.Scn}, a minute ago {back.Scn}");

        // The test user has no SELECT_CATALOG_ROLE: V$DATABASE is refused – the app maps the time then.
        try
        {
            var exact = await _data.CurrentPointAsync(exact: true, Ct);
            Assert.Equal(now.Database, exact.Database);
            Assert.True(exact.Scn >= now.Scn);
        }
        catch (DatabaseException ex)
        {
            Assert.True(ex.IsAny(OracleErrorCodes.MissingRights), ex.Message);
        }

        TestContext.Current.SendDiagnosticMessage($"Undo reaches about {await _data.UndoReachAsync(Ct)}");
        await using var connection = new OracleConnection(oracle.RequireConnectionString());
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT SYS_CONTEXT('USERENV', 'CON_DBID') FROM dual";
        TestContext.Current.SendDiagnosticMessage($"CON_DBID without rights: {await command.ExecuteScalarAsync(Ct)}");

        var error = await Assert.ThrowsAsync<DatabaseException>(() => _data.PointBackAsync(TimeSpan.FromDays(3650), Ct));
        Assert.NotNull(DataDiffErrors.ExplainPoint(error, DateTimeOffset.Now.AddYears(-10)));
    }

    [Fact]
    public async Task Rows_set_back_as_pending_changes_bring_the_table_back_to_its_start_point_once_written()
    {
        await CreateAsync("TT_RESTORE", "ID NUMBER(10) PRIMARY KEY, STATUS VARCHAR2(20), NOTE CLOB",
            "INSERT INTO TT_RESTORE VALUES (1, 'open', 'kept text')",
            "INSERT INTO TT_RESTORE VALUES (2, 'open', TO_CLOB(RPAD('x', 4000, 'x')) || RPAD('y', 1000, 'y'))"); // beyond a VARCHAR2 in SQL
        var table = await DetailsAsync("TT_RESTORE");
        var scn = await StartAfterAsync("TT_RESTORE");
        await RunAsync("UPDATE TT_RESTORE SET STATUS = 'done', NOTE = NULL WHERE ID = 1", "DELETE FROM TT_RESTORE WHERE ID = 2",
            "INSERT INTO TT_RESTORE VALUES (3, 'new', NULL)");
        var diff = DataDiff.Pair(table, [], await _data.ReadDiffAsync(table, [], scn, Ct));
        Assert.Equal(3, diff.Rows.Count);

        var (profile, password) = oracle.RequireProfile();
        await using var workspace = await new OracleDatabaseConnector().OpenAsync(profile, password, "Restore", Ct);
        var read = await DataRestore.ReadAsync(_data.AsOf(scn), workspace.Data, table, diff.Rows, Ct);
        var tracker = new ChangeTracker(table);
        Assert.Equal(3, tracker.Restore(read.Rows).Restored);
        var operations = tracker.PendingOperations();
        await workspace.Editor.FlushAsync(table, operations, new FlushOptions(), Ct);
        await workspace.Editor.CommitAsync(Ct);

        var now = DataDiff.Pair(table, [], await _data.ReadDiffAsync(table, [], scn, Ct));
        Assert.Empty(now.Rows); // as it was – the 5000 characters of the CLOB too, not its preview
        var note = await _data.ReadLobAsync(table, new RowKey.PrimaryKey([2m]), 2, Ct);
        Assert.True(note.Value is string { Length: 5000 } text && text == new string('x', 4000) + new string('y', 1000), "The CLOB came back cut.");
    }
}
