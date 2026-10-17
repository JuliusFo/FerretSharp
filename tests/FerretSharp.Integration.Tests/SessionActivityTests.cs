using System.Diagnostics;
using FerretSharp.Core.Connections;
using FerretSharp.Core.Data;
using FerretSharp.Core.Oracle;
using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;
using Oracle.ManagedDataAccess.Client;

namespace FerretSharp.Integration.Tests;

/// <summary>
/// Review 3.40.0, C3: what a running statement shows (WP-52, ADR 0027) against a real database – the progress of a long
/// operation of the statement running now, and the way without rights on the V$ views, down to the schema cache the run
/// progress asks (<c>SessionWatch.For</c> in the UI goes through <see cref="SchemaCache.GetSessionActivityAsync"/>).
/// </summary>
public sealed class SessionActivityTests(OracleContainerFixture oracle) : IAsyncLifetime
{
    private const string NoRightsUser = "FS_ACTIVITY_NORIGHTS";
    private const string NoRightsPassword = "Activity2026x";
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

            // A long operation as Oracle reports one (V$SESSION_LONGOPS), set by the statement that runs, which then waits.
            await ExecuteAsync(oracle.RequireConnectionString(), """
                CREATE OR REPLACE FUNCTION SA_LONG_OPERATION RETURN NUMBER IS
                  rindex BINARY_INTEGER := DBMS_APPLICATION_INFO.SET_SESSION_LONGOPS_NOHINT;
                  slno   BINARY_INTEGER;
                BEGIN
                  DBMS_APPLICATION_INFO.SET_SESSION_LONGOPS(rindex, slno, 'Table Scan', 0, 0, 43, 100, 'APP.KUNDEN', 'Blocks');
                  DBMS_SESSION.SLEEP(6);
                  RETURN 1;
                END;
                """, """
                CREATE OR REPLACE FUNCTION SA_WAIT RETURN NUMBER IS
                BEGIN
                  DBMS_SESSION.SLEEP(3);
                  RETURN 1;
                END;
                """);
            await ExecuteAsync(System(),
                $"GRANT SELECT_CATALOG_ROLE TO {OracleIdentifier.Quote(Owner)}",
                $"CREATE USER {NoRightsUser} IDENTIFIED BY {NoRightsPassword}",
                $"GRANT CREATE SESSION TO {NoRightsUser}");
            _created = true;
        }
        finally
        {
            Gate.Release();
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private string System() => new OracleConnectionStringBuilder(oracle.RequireConnectionString()) { UserID = "system" }.ConnectionString;

    private static async Task ExecuteAsync(string connectionString, params string[] statements)
    {
        await using var connection = new OracleConnection(connectionString);
        await connection.OpenAsync(Ct);
        foreach (var sql in statements)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync(Ct);
        }
    }

    private async Task<IDatabaseConnection> OpenAsync(string action, ConnectionProfile? profile = null, string? password = null)
    {
        var (own, ownPassword) = oracle.RequireProfile();
        return await new OracleDatabaseConnector().OpenAsync(profile ?? own, password ?? ownPassword, action, Ct);
    }

    [Fact]
    public async Task The_long_operation_of_the_running_statement_shows_as_progress()
    {
        await using var workspace = await OpenAsync("Long operation");
        await using var explorer = await OpenAsync("Explorer");
        var running = workspace.Data.ReadSqlAsync(new QuerySpec("SELECT SA_LONG_OPERATION FROM DUAL", []), 0, 1, Ct);

        LongOperation? progress = null;
        var watch = Stopwatch.StartNew();
        while (progress is null && watch.Elapsed < TimeSpan.FromSeconds(5))
        {
            progress = (await explorer.Schema.GetSessionActivityAsync(workspace.SessionTag!, Ct))?.Progress;
            if (progress is null)
            {
                await Task.Delay(200, Ct);
            }
        }

        Assert.NotNull(progress);
        // TARGET is the object a real scan works on; one set by SET_SESSION_LONGOPS has none (its target is object number 0).
        Assert.Equal(("Table Scan", null, 43L, 100L), (progress.Operation, progress.Target, progress.Done, progress.Total));
        Assert.Null((await explorer.Schema.GetSessionActivityAsync(workspace.SessionTag!, Ct))?.Wait); // no lock to wait for
        await running;

        // The long operation stays in V$SESSION_LONGOPS, unfinished – the next statement of the session does not show it (A11).
        var next = workspace.Data.ReadSqlAsync(new QuerySpec("SELECT SA_WAIT FROM DUAL", []), 0, 1, Ct);
        await Task.Delay(1000, Ct); // the statement runs now (it waits 3 s)
        Assert.False(next.IsCompleted);
        Assert.Null((await explorer.Schema.GetSessionActivityAsync(workspace.SessionTag!, Ct))?.Progress);
        await next;
    }

    [Fact]
    public async Task Without_rights_on_the_v_views_the_activity_is_unknown_and_not_asked_again()
    {
        var (profile, _) = oracle.RequireProfile();
        var noRights = profile with { Id = Guid.NewGuid(), User = NoRightsUser };
        await using var explorer = await OpenAsync("No rights", noRights, NoRightsPassword);
        await using var workspace = await OpenAsync("Watched");
        var schema = new SchemaCache(explorer.Schema, NoRightsUser);

        Assert.Null(await explorer.Schema.GetSessionActivityAsync(workspace.SessionTag!, Ct));
        Assert.Null(await explorer.Schema.GetLockHoldersAsync(new TableRef(Owner, "DUAL"), Ct));

        Assert.Null(await schema.GetSessionActivityAsync(workspace.SessionTag!, Ct));
        Assert.True(schema.SessionActivityUnavailable); // the run progress then names the workspaces with open changes instead
    }
}
