using System.Diagnostics;
using FerretSharp.Core.Connections;
using FerretSharp.Core.Data;
using FerretSharp.Core.Oracle;
using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;
using Oracle.ManagedDataAccess.Client;

namespace FerretSharp.Integration.Tests;

/// <summary>
/// The data diff (WP-44) against Oracle: the start SCN from the time, the flashback query with both MINUS, and the errors the
/// user can act on. Each test has a table of its own; the changes are made by another session and committed, as an app would.
/// </summary>
public sealed partial class DataDiffTests(OracleContainerFixture oracle) : IAsyncLifetime
{
    private OracleSession? _session;
    private OracleDataAccess _data = null!;
    private OracleSchemaReader _schema = null!;
    private string _owner = "";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        var (profile, password) = oracle.RequireProfile();
        _owner = profile.EffectiveSchema;
        _session = await OracleSession.OpenAsync(
            OracleConnectionStringFactory.Create(profile, password), new SessionContext("FerretSharp", "Data diff tests"), Ct);
        _data = new OracleDataAccess(_session);
        _schema = new OracleSchemaReader(_session);
    }

    public async ValueTask DisposeAsync()
    {
        if (_session is not null)
        {
            await _session.DisposeAsync();
        }
    }

    /// <summary>Runs statements in a session of their own and commits – another app changing data.</summary>
    private async Task RunAsync(params string[] statements)
    {
        await using var connection = new OracleConnection(oracle.RequireConnectionString());
        await connection.OpenAsync(Ct);
        foreach (var sql in statements.Append("COMMIT"))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync(Ct);
        }
    }

    private Task CreateAsync(string table, string columns, params string[] rows) =>
        RunAsync([$"BEGIN EXECUTE IMMEDIATE 'DROP TABLE {table} PURGE'; EXCEPTION WHEN OTHERS THEN NULL; END;", $"CREATE TABLE {table} ({columns})", .. rows]);

    private Task<TableDetails> DetailsAsync(string table) => _schema.GetDetailsAsync(new TableSummary(_owner, table, TableKind.Table), Ct);

    /// <summary>
    /// The start SCN once it is past everything committed so far: mapped from the time, it may lie a few seconds back (measured
    /// here; the user is told in the plan of WP-44). The highest commit SCN of the table's rows is the mark – DDL on the table
    /// therefore goes before its last rows, or the start may lie before it (ORA-01466). Past the commit is not enough either:
    /// Oracle still refuses a point within about a second of the CREATE TABLE (ORA-01466, CI after the dependency update of
    /// 2026-11-09, when TIMESTAMP_TO_SCN was already past the commit at once) – the start is taken once the table reads AS OF it.
    /// </summary>
    private async Task<long> StartAfterAsync(string table)
    {
        await using var connection = new OracleConnection(oracle.RequireConnectionString());
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT MAX(ORA_ROWSCN) FROM {table}";
        var committed = Convert.ToInt64(await command.ExecuteScalarAsync(Ct), System.Globalization.CultureInfo.InvariantCulture);
        var watch = Stopwatch.StartNew();
        while (true)
        {
            var scn = (await _data.CurrentPointAsync(exact: false, Ct)).Scn;
            if (scn >= committed && await ReadsAsOfAsync(connection, table, scn))
            {
                TestContext.Current.SendDiagnosticMessage($"Start point readable after {watch.ElapsedMilliseconds} ms");
                return scn;
            }

            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), "No start point after the last commit was readable within 20 s.");
            await Task.Delay(250, Ct);
        }
    }

    private static async Task<bool> ReadsAsOfAsync(OracleConnection connection, string table, long scn)
    {
        await using var probe = connection.CreateCommand();
        probe.BindByName = true;
        probe.CommandText = $"SELECT COUNT(*) FROM {table} AS OF SCN :scn";
        probe.Parameters.Add("scn", OracleDbType.Int64, scn, System.Data.ParameterDirection.Input);
        try
        {
            await probe.ExecuteScalarAsync(Ct);
            return true;
        }
        catch (OracleException ex) when (ex.Number == 1466)
        {
            return false;
        }
    }

    private static decimal Id(DiffRow row) => (decimal)((RowKey.PrimaryKey)row.Key).Values[0]!;

    [Fact]
    public async Task A_diff_finds_new_changed_and_deleted_rows_and_with_a_filter_rows_that_moved_in_or_out()
    {
        await CreateAsync("DIFF_ORDERS", "ID NUMBER(10) PRIMARY KEY, STATUS VARCHAR2(20), NOTE CLOB, LEGACY LONG",
            "INSERT INTO DIFF_ORDERS (ID, STATUS, NOTE) VALUES (1, 'open', 'abc')",
            "INSERT INTO DIFF_ORDERS (ID, STATUS, NOTE) VALUES (2, 'open', 'abc')",
            "INSERT INTO DIFF_ORDERS (ID, STATUS, NOTE) VALUES (3, 'open', NULL)",
            "INSERT INTO DIFF_ORDERS (ID, STATUS, NOTE) VALUES (4, 'done', NULL)");
        var table = await DetailsAsync("DIFF_ORDERS");
        var scn = await StartAfterAsync("DIFF_ORDERS");

        await RunAsync(
            "UPDATE DIFF_ORDERS SET STATUS = 'done' WHERE ID = 1",
            "UPDATE DIFF_ORDERS SET NOTE = 'abcd' WHERE ID = 2",
            "DELETE FROM DIFF_ORDERS WHERE ID = 3",
            "INSERT INTO DIFF_ORDERS (ID, STATUS) VALUES (5, 'open')",
            "UPDATE DIFF_ORDERS SET LEGACY = 'not compared' WHERE ID = 4");

        var all = DataDiff.Pair(table, [], await _data.ReadDiffAsync(table, [], scn, Ct));

        Assert.Equal([(1m, DiffStatus.Changed), (2m, DiffStatus.Changed), (3m, DiffStatus.Deleted), (5m, DiffStatus.Added)],
            all.Rows.Select(r => (Id(r), r.Status)));
        Assert.Equal([table.IndexOf("STATUS")], all.Rows[0].Changed);
        Assert.Equal([table.IndexOf("NOTE")], all.Rows[1].Changed); // the CLOB by its length
        Assert.Equal([table.IndexOf("LEGACY")], all.Omitted); // LONG: left out, so row 4 is no change
        Assert.False(all.Truncated);

        var open = DataDiff.Pair(table, [new FilterCondition("STATUS", FilterOperator.Equals, ["open"])],
            await _data.ReadDiffAsync(table, [new FilterCondition("STATUS", FilterOperator.Equals, ["open"])], scn, Ct));

        // Row 1 left the filter (deleted from its view), row 2 stays open and changed, row 5 came in.
        Assert.Equal([(1m, DiffStatus.Deleted), (2m, DiffStatus.Changed), (3m, DiffStatus.Deleted), (5m, DiffStatus.Added)],
            open.Rows.Select(r => (Id(r), r.Status)));
    }

    [Fact]
    public async Task A_table_without_primary_key_is_compared_by_rowid_and_reports_row_movement()
    {
        // Row movement before the rows: the start point waits for their commit only, DDL after it could lie past the start
        // (ORA-01466; flaky under the load of the whole suite).
        await CreateAsync("DIFF_LOG", "MSG VARCHAR2(40)",
            "ALTER TABLE DIFF_LOG ENABLE ROW MOVEMENT", "INSERT INTO DIFF_LOG VALUES ('one')", "INSERT INTO DIFF_LOG VALUES ('two')");
        var table = await DetailsAsync("DIFF_LOG");
        var scn = await StartAfterAsync("DIFF_LOG");

        await RunAsync("UPDATE DIFF_LOG SET MSG = 'zwei' WHERE MSG = 'two'", "INSERT INTO DIFF_LOG VALUES ('three')");

        var read = await _data.ReadDiffAsync(table, [], scn, Ct);
        var diff = DataDiff.Pair(table, [], read);

        Assert.Equal(RowKeyKind.RowId, diff.RowKey);
        Assert.True(diff.RowMovement);
        Assert.Equal([DiffStatus.Added, DiffStatus.Changed], diff.Rows.Select(r => r.Status).Order());
        Assert.Equal("two", diff.Rows.Single(r => r.Status == DiffStatus.Changed).Then!.Values[0]);
    }

    /// <summary>
    /// A column added since the start point does not break the comparison (Oracle 23: it reads as NULL then, the rows show as
    /// changed where it has a value); DDL that rewrites the table does – explained to the user.
    /// </summary>
    [Fact]
    public async Task An_added_column_is_compared_and_ddl_that_rewrites_the_table_is_explained()
    {
        await CreateAsync("DIFF_DDL", "ID NUMBER(10) PRIMARY KEY, CODE VARCHAR2(10)", "INSERT INTO DIFF_DDL VALUES (1, 'a')", "INSERT INTO DIFF_DDL VALUES (2, 'b')");
        var scn = await StartAfterAsync("DIFF_DDL");
        await RunAsync("ALTER TABLE DIFF_DDL ADD (NAME VARCHAR2(10))", "UPDATE DIFF_DDL SET NAME = 'x' WHERE ID = 2");
        var table = await DetailsAsync("DIFF_DDL");

        var diff = DataDiff.Pair(table, [], await _data.ReadDiffAsync(table, [], scn, Ct));
        Assert.Equal([(2m, DiffStatus.Changed)], diff.Rows.Select(r => (Id(r), r.Status)));

        await RunAsync("TRUNCATE TABLE DIFF_DDL");
        var error = await Assert.ThrowsAsync<DatabaseException>(() => _data.ReadDiffAsync(table, [], scn, Ct));

        TestContext.Current.SendDiagnosticMessage($"After TRUNCATE: {error.ErrorCode}");
        Assert.NotNull(DataDiffErrors.Explain(error, table.Table.Ref, DateTimeOffset.Now));
    }

    /// <summary>
    /// Review of WP-44: in a locked session (Prod) a failed flashback query counted as an unusable snapshot and ran up to four
    /// times, with pauses of 1 + 2 + 3 s after ORA-01466 – a new snapshot never helps the side as of then.
    /// </summary>
    [Fact]
    public async Task In_a_locked_session_a_failed_diff_is_not_repeated_in_new_snapshots()
    {
        await CreateAsync("DIFF_LOCKED", "ID NUMBER(10) PRIMARY KEY", "INSERT INTO DIFF_LOCKED VALUES (1)");
        var scn = await StartAfterAsync("DIFF_LOCKED");
        var table = await DetailsAsync("DIFF_LOCKED");
        await RunAsync("TRUNCATE TABLE DIFF_LOCKED");
        var (profile, password) = oracle.RequireProfile();
        await using var locked = await OracleSession.OpenAsync(
            OracleConnectionStringFactory.Create(profile, password), new SessionContext("FerretSharp", "Data diff locked"), Ct);
        await locked.UseReadOnlySnapshotsAsync(Ct);

        var watch = Stopwatch.StartNew();
        await Assert.ThrowsAsync<DatabaseException>(() => new OracleDataAccess(locked).ReadDiffAsync(table, [], scn, Ct));

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), $"The failed diff took {watch.Elapsed.TotalSeconds:0.0} s – repeated in new snapshots?");
    }
}
