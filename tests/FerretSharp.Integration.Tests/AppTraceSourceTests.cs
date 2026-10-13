using System.Diagnostics;
using FerretSharp.AppTrace;
using FerretSharp.Core.AppTrace;
using FerretSharp.Core.Connections;
using FerretSharp.Core.Oracle;
using FerretSharp.Core.Query;
using Microsoft.Extensions.Logging.Abstractions;
using Oracle.ManagedDataAccess.Client;

namespace FerretSharp.Integration.Tests;

/// <summary>
/// WP-33: attaching to a running app from outside over EventPipe. The app is tests/FerretSharp.FakeEfApp (EF Core 8 with
/// lazy-loading proxies, no diagnostics code), on SQLite (no Docker needed) or on the Oracle container – there the
/// statements it ran are looked up in V$SQL afterwards.
/// </summary>
public sealed class AppTraceSourceTests(OracleContainerFixture oracle)
{
    private static readonly string Configuration = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}",
        StringComparison.OrdinalIgnoreCase) ? "Release" : "Debug";

    private static string FakeAppPath => Path.Combine(ModelHostTests.RepositoryRoot, "tests", "FerretSharp.FakeEfApp", "bin", Configuration, "net8.0", "FerretSharp.FakeEfApp.dll");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly EventPipeTraceSource _source = new(NullLogger<EventPipeTraceSource>.Instance);

    [Fact]
    public async Task The_statements_of_an_app_are_read_from_outside_by_origin()
    {
        await using var app = await FakeApp.StartAsync(FakeAppPath, "sqlite", null, "-environment=fake-test");

        var listed = Assert.Single(_source.ListProcesses(), p => p.Id == app.Id);
        Assert.Equal(("FerretSharp.FakeEfApp", "fake-test"), (listed.Name, listed.Environment));

        var recording = new StatementRecording();
        await foreach (var e in TraceAsync(app))
        {
            recording.Apply(e);
        }

        var snapshot = recording.Snapshot();
        int Count(StatementOrigin origin) => snapshot.Origins.SingleOrDefault(o => o.Origin == origin)?.Count ?? 0;
        // 3 rounds: 2 LINQ (orders, FromSql with Count on top), 5 lazy loads, 1 FromSql, 1 raw UPDATE, 1 SaveChanges, 1 ExecuteUpdate
        Assert.Equal(6, Count(StatementOrigin.Linq));
        Assert.Equal(15, Count(StatementOrigin.LazyLoading));
        Assert.Equal(3, Count(StatementOrigin.FromSql));
        Assert.Equal(3, Count(StatementOrigin.ExecuteSql));
        Assert.Equal(3, Count(StatementOrigin.SaveChanges));
        Assert.Equal(3, Count(StatementOrigin.ExecuteUpdate));
        var lazy = Assert.Single(snapshot.Statements, s => s.Origin == StatementOrigin.LazyLoading);
        Assert.Equal(("Positionen", 48L), (lazy.Navigation, lazy.Rows)); // 15 + 16 + 17 lines
        Assert.True(lazy.Repeated); // 15 runs within a moment
        Assert.Contains(".db", snapshot.DataSource, StringComparison.Ordinal); // SQLite: the file
    }

    [Fact]
    public async Task Canceling_ends_the_trace_quietly_and_leaves_the_app_running()
    {
        await using var app = await FakeApp.StartAsync(FakeAppPath, "sqlite", null);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        await using var events = _source.AttachAsync(app.Id, cts.Token).GetAsyncEnumerator(cts.Token);
        var first = events.MoveNextAsync(); // attaches

        await app.GoAsync();
        Assert.True(await first);
        await cts.CancelAsync();
        while (await events.MoveNextAsync())
        {
        }

        Assert.False(app.HasExited);
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

        var recording = new StatementRecording();
        await foreach (var e in TraceAsync(app))
        {
            recording.Apply(e);
        }

        var snapshot = recording.Snapshot();
        var lazy = Assert.Single(snapshot.Statements, s => s.Origin == StatementOrigin.LazyLoading);
        Assert.Equal(15, lazy.Count);

        // The app's data source is the container's connect descriptor: it names the profile's host, port and service.
        var match = AppConnectionMatch.Find([profile, profile with { Id = Guid.NewGuid(), Name = "Other", Address = new HostPortAddress("elsewhere", 1521, "X", null) }], null, snapshot.DataSource);
        Assert.Equal((profile.Id, MatchKind.DataSource), (match.Profile?.Id, match.Kind));

        await using var connection = await new OracleDatabaseConnector().OpenAsync(profile, password, "App trace lookup", Ct);
        var cursor = await connection.Schema.GetCursorAsync(lazy.Text, Ct);
        Assert.NotNull(cursor);
        Assert.Equal(15, cursor.Executions);
        Assert.Equal(lazy.Rows, cursor.RowsProcessed);
        Assert.Contains(cursor.Plan.Steps, s => s.ObjectName == "TR_POSITION");
    }

    /// <summary>Attaches, then lets the app run its rounds; ends when the app has ended.</summary>
    private async IAsyncEnumerable<EfEvent> TraceAsync(FakeApp app)
    {
        await using var events = _source.AttachAsync(app.Id, Ct).GetAsyncEnumerator(Ct);
        var next = events.MoveNextAsync(); // attaching happens here, before the first event
        await app.GoAsync();
        var done = app.ExitWhenDoneAsync();
        while (await next)
        {
            yield return events.Current;
            next = events.MoveNextAsync();
        }

        await done;
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

    /// <summary>tests/FerretSharp.FakeEfApp as a process: "ready" once its tables are there, "go" runs it, "exit" ends it.</summary>
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
            await app.ExpectAsync("ready");
            return app;
        }

        public async Task GoAsync()
        {
            await Task.Delay(500, Ct); // the session is enabled in the app right after attaching; give it a moment anyway
            await _process.StandardInput.WriteLineAsync("go");
            await _process.StandardInput.FlushAsync(Ct);
        }

        public async Task ExitWhenDoneAsync()
        {
            await ExpectAsync("done");
            await _process.StandardInput.WriteLineAsync("exit");
            await _process.StandardInput.FlushAsync(Ct);
            await _process.WaitForExitAsync(Ct);
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
                _process.Kill();
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
