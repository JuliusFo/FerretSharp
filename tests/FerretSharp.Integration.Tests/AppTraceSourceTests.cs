using System.Diagnostics;
using System.Runtime.InteropServices;
using FerretSharp.AppTrace;
using FerretSharp.Core.AppTrace;
using FerretSharp.Core.Connections;
using FerretSharp.Core.Oracle;
using Microsoft.Extensions.Logging.Abstractions;
using Oracle.ManagedDataAccess.Client;

namespace FerretSharp.Integration.Tests;

/// <summary>
/// WP-33: attaching to a running app from outside over EventPipe. The app is tests/FerretSharp.FakeEfApp (EF Core 8 with
/// lazy-loading proxies, no diagnostics code), on SQLite (no Docker needed) or on the Oracle container – there the
/// statements it ran are looked up in V$SQL afterwards.
/// </summary>
public sealed partial class AppTraceSourceTests(OracleContainerFixture oracle)
{
    private static readonly string Configuration = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}",
        StringComparison.OrdinalIgnoreCase) ? "Release" : "Debug";

    private static string FakeAppPath => Net8.Require(Path.Combine(ModelHostTests.RepositoryRoot, "tests", "FerretSharp.FakeEfApp", "bin", Configuration, "net8.0", "FerretSharp.FakeEfApp.dll"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly EventPipeTraceSource _source = new(NullLogger<EventPipeTraceSource>.Instance);

    [Fact]
    public async Task The_statements_of_an_app_are_read_from_outside_by_origin()
    {
        await using var app = await FakeApp.StartAsync(FakeAppPath, "sqlite", null, "-environment=fake-test");

        var listed = Assert.Single(_source.ListProcesses(), p => p.Id == app.Id);
        Assert.Equal(("FerretSharp.FakeEfApp", "fake-test"), (listed.Name, listed.Environment));

        var snapshot = await RecordAsync(app);
        int Count(StatementOrigin origin) => snapshot.Origins.SingleOrDefault(o => o.Origin == origin)?.Count ?? 0;
        // 3 rounds: 2 LINQ (orders, FromSql with Count on top), 12 lazy loads, 1 FromSql, 1 raw UPDATE, 1 SaveChanges, 1 ExecuteUpdate
        Assert.Equal(6, Count(StatementOrigin.Linq));
        Assert.Equal(36, Count(StatementOrigin.LazyLoading));
        Assert.Equal(3, Count(StatementOrigin.FromSql));
        Assert.Equal(3, Count(StatementOrigin.ExecuteSql));
        Assert.Equal(3, Count(StatementOrigin.SaveChanges));
        Assert.Equal(3, Count(StatementOrigin.ExecuteUpdate));
        var lazy = Assert.Single(snapshot.Statements, s => s.Origin == StatementOrigin.LazyLoading);
        Assert.Equal(("Positionen", 111L), (lazy.Navigation, lazy.Rows)); // 36 + 37 + 38 lines
        Assert.True(lazy.Repeated); // 12 runs in one context within a moment
        Assert.Contains(".db", snapshot.DataSource, StringComparison.Ordinal); // SQLite: the file
    }

    [Fact]
    public async Task Rows_are_right_for_every_query_shape_and_a_lazy_load_without_a_query_changes_no_origin()
    {
        await using var app = await FakeApp.StartAsync(FakeAppPath, "sqlite", null, "--scenario", "shapes");

        var snapshot = await RecordAsync(app);

        long Rows(string start, StatementOrigin origin = StatementOrigin.Linq) =>
            Assert.Single(snapshot.Statements, s => s.Origin == origin && s.Text.StartsWith(start, StringComparison.Ordinal)).Rows;
        // EF reads to the end also for First, Single and Count (measured, review of WP-33)
        Assert.Equal(1, Rows("SELECT \"t\".\"ID\", \"t\".\"NUMMER\"\nFROM \"TR_AUFTRAG\" AS \"t\"\nORDER BY \"t\".\"ID\"\nLIMIT 1".ReplaceLineEndings()));
        Assert.Equal(5, Rows("SELECT \"t\".\"ID\", \"t\".\"NUMMER\"\nFROM \"TR_AUFTRAG\" AS \"t\"\nORDER BY \"t\".\"ID\"\nLIMIT @__p_0".ReplaceLineEndings()));
        Assert.Equal(1, Rows("SELECT COUNT(*)"));
        var lazy = Assert.Single(snapshot.Statements, s => s.Origin == StatementOrigin.LazyLoading);
        Assert.Equal(("Auftrag", 1L), (lazy.Navigation, lazy.Rows)); // a reference
        // "Ersatz" has a NULL foreign key: the event came, no query – the SaveChanges after it stays SaveChanges.
        var save = Assert.Single(snapshot.Statements, s => s.Origin == StatementOrigin.SaveChanges);
        Assert.Equal(1, save.Rows);
    }

    [Fact]
    public async Task Canceling_ends_the_trace_quietly_and_leaves_the_app_running()
    {
        await using var app = await FakeApp.StartAsync(FakeAppPath, "sqlite", null);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        await using var events = _source.AttachAsync(app.Id, cts.Token).GetAsyncEnumerator(cts.Token);
        var first = events.MoveNextAsync(); // attaches

        await app.SendAsync("warm");
        Assert.True(await first);
        await cts.CancelAsync();
        while (await events.MoveNextAsync())
        {
        }

        Assert.False(app.HasExited);
    }

    [Fact]
    public async Task Detaching_from_a_halted_app_does_not_hang()
    {
        // At a breakpoint the app does not answer the stop: the trace closes its stream itself (review of WP-33).
        await using var app = await FakeApp.StartAsync(FakeAppPath, "sqlite", null);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        await using var events = _source.AttachAsync(app.Id, cts.Token).GetAsyncEnumerator(cts.Token);
        var first = events.MoveNextAsync();
        await app.SendAsync("warm");
        Assert.True(await first);

        app.Suspend();
        var watch = Stopwatch.StartNew();
        await cts.CancelAsync();
        while (await events.MoveNextAsync())
        {
        }

        Assert.True(watch.Elapsed < EventPipeTraceSource.StopTimeout * 3, $"Detaching took {watch.Elapsed}");
    }

    /// <summary>
    /// Review 3.31.0, A24: attaching to an app halted at a breakpoint waited for its diagnostics server – a detach or a switch
    /// of the page waited with it until the app ran again.
    /// </summary>
    [Fact]
    public async Task Attaching_to_a_halted_app_can_be_cancelled_at_once()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Halts the app with NtSuspendProcess.");
        await using var app = await FakeApp.StartAsync(FakeAppPath, "sqlite", null);
        app.Suspend();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cts.CancelAfter(TimeSpan.FromMilliseconds(500));
        var watch = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in _source.AttachAsync(app.Id, cts.Token))
            {
            }
        });

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), $"Cancelling took {watch.Elapsed}");
    }

    [Fact]
    public async Task An_app_that_is_killed_ends_the_trace_quietly()
    {
        // Stopping the debugger kills the app: the stream breaks off in the middle of a block.
        await using var app = await FakeApp.StartAsync(FakeAppPath, "sqlite", null);
        await using var events = _source.AttachAsync(app.Id, Ct).GetAsyncEnumerator(Ct);
        var first = events.MoveNextAsync();
        await app.SendAsync("warm");
        Assert.True(await first);

        await app.DisposeAsync(); // kills it

        // Whatever was buffered, then the end – without an exception.
        while (await events.MoveNextAsync())
        {
        }
    }

    [Fact]
    public async Task A_process_that_ended_is_refused()
    {
        await using var app = await FakeApp.StartAsync(FakeAppPath, "sqlite", null);
        var id = app.Id;
        await app.DisposeAsync();

        var error = await Assert.ThrowsAsync<RefusedException>(async () =>
        {
            await foreach (var _ in _source.AttachAsync(id, Ct))
            {
            }
        });

        Assert.Contains(id.ToString(System.Globalization.CultureInfo.InvariantCulture), error.Message);
    }

    [Fact]
    public async Task On_oracle_the_statements_are_found_in_v_sql_and_the_app_matches_its_connection()
    {
        var (profile, password) = oracle.RequireProfile();
        var connectionString = oracle.RequireConnectionString();
        await GrantCatalogAsync(profile.User);
        await using var app = await FakeApp.StartAsync(FakeAppPath, "oracle", connectionString, "-environment=fake-oracle");

        var snapshot = await RecordAsync(app);
        var lazy = Assert.Single(snapshot.Statements, s => s.Origin == StatementOrigin.LazyLoading);
        Assert.Equal(36, lazy.Count);

        // The app's data source is the container's connect descriptor: it names the profile's host, port and service.
        var match = AppConnectionMatch.Find([profile, profile with { Id = Guid.NewGuid(), Name = "Other", Address = new HostPortAddress("elsewhere", 1521, "X", null) }], null, snapshot.DataSource);
        Assert.Equal((profile.Id, MatchKind.DataSource), (match.Profile?.Id, match.Kind));

        await using var connection = await new OracleDatabaseConnector().OpenAsync(profile, password, "App trace lookup", Ct);
        var cursor = await connection.Schema.GetCursorAsync(lazy.Text, Ct);
        Assert.NotNull(cursor);
        Assert.Equal(36, cursor.Executions);
        Assert.Equal(lazy.Rows, cursor.RowsProcessed);
        Assert.Contains(cursor.Plan.Steps, s => s.ObjectName == "TR_POSITION");
    }

    /// <summary>Attaches, waits until the app's warm-up query shows up (the session is live), runs the app to its end.</summary>
    private async Task<RecordingSnapshot> RecordAsync(FakeApp app)
    {
        var recording = new StatementRecording();
        await using var events = _source.AttachAsync(app.Id, Ct).GetAsyncEnumerator(Ct);
        var next = events.MoveNextAsync(); // attaching happens here, before the first event
        await app.SendAsync("warm");
        Task? done = null;
        while (await next)
        {
            var e = events.Current;
            next = events.MoveNextAsync();
            if (e.Field(EfEvents.Text)?.Contains("warm-up", StringComparison.Ordinal) == true)
            {
                done ??= app.GoAndExitWhenDoneAsync();
                continue;
            }

            recording.Apply(e);
        }

        Assert.NotNull(done);
        await done;
        return recording.Snapshot();
    }

    private async Task GrantCatalogAsync(string user)
    {
        var system = new OracleConnectionStringBuilder(oracle.RequireConnectionString()) { UserID = "system" }.ConnectionString;
        await using var connection = new OracleConnection(system);
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = $"GRANT SELECT_CATALOG_ROLE TO {OracleIdentifier.Quote(OracleIdentifier.Normalize(user))}";
        await command.ExecuteNonQueryAsync(Ct);
    }

    [LibraryImport("ntdll.dll")]
    private static partial int NtSuspendProcess(IntPtr process);

    /// <summary>
    /// tests/FerretSharp.FakeEfApp as a process: "ready" once its tables are there, "warm" runs a marker query until "go",
    /// "go" runs it, "done" when through, "exit" ends it.
    /// </summary>
    private sealed class FakeApp : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly string? _db;
        private bool _disposed;

        private FakeApp(Process process, string? db)
        {
            _process = process;
            _db = db;
        }

        public int Id => _process.Id;

        public bool HasExited => _process.HasExited;

        public static async Task<FakeApp> StartAsync(string path, string provider, string? connectionString, params string[] args)
        {
            Assert.True(File.Exists(path), $"The fake app is not built: {path}");
            var db = provider == "sqlite" ? Path.Combine(Path.GetTempPath(), $"ferret-fake-ef-{Guid.NewGuid():N}.db") : null;
            var info = new ProcessStartInfo("dotnet")
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var arg in (string[])["exec", path, "--provider", provider, .. db is null ? [] : new[] { "--db", db }, .. args])
            {
                info.ArgumentList.Add(arg);
            }

            if (connectionString is not null)
            {
                info.Environment["FAKE_EF_CONNECTION"] = connectionString; // never on the command line
            }

            var app = new FakeApp(Process.Start(info)!, db);
            try
            {
                await app.ExpectAsync("ready");
            }
            catch
            {
                await app.DisposeAsync(); // never leave a process behind
                throw;
            }

            return app;
        }

        public async Task SendAsync(string line)
        {
            await _process.StandardInput.WriteLineAsync(line);
            await _process.StandardInput.FlushAsync(Ct);
        }

        public async Task GoAndExitWhenDoneAsync()
        {
            await SendAsync("go");
            await ExpectAsync("done");
            await SendAsync("exit");
            await _process.WaitForExitAsync(Ct);
        }

        /// <summary>Halts every thread of the app, as a debugger does at a breakpoint (more: the diagnostics server too).</summary>
        public void Suspend()
        {
            if (OperatingSystem.IsWindows())
            {
                Assert.Equal(0, NtSuspendProcess(_process.Handle));
            }
            else
            {
                using var kill = Process.Start("kill", ["-STOP", Id.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
                kill.WaitForExit();
            }
        }

        private async Task ExpectAsync(string line)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(2));
            while (await _process.StandardOutput.ReadLineAsync(timeout.Token) is { } read)
            {
                if (read == line)
                {
                    return;
                }
            }

            Assert.Fail($"The fake app ended before \"{line}\": {await _process.StandardError.ReadToEndAsync(Ct)}");
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (!_process.HasExited)
            {
                _process.Kill(); // also a halted one
                await _process.WaitForExitAsync(CancellationToken.None);
            }

            _process.Dispose();
            if (_db is not null)
            {
                try
                {
                    File.Delete(_db);
                }
                catch (IOException)
                {
                    // Windows may hold the file of a killed process a moment longer; it is in the temp folder
                }
            }
        }
    }
}
