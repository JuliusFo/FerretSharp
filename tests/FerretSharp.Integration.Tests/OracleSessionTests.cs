using System.Diagnostics;
using FerretSharp.Core.Oracle;

namespace FerretSharp.Integration.Tests;

public class OracleSessionTests(OracleContainerFixture oracle)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<OracleSession> OpenAsync(SessionContext? context = null)
    {
        var (profile, password) = oracle.RequireProfile();
        var connectionString = OracleConnectionStringFactory.Create(profile, password);
        return await OracleSession.OpenAsync(connectionString, context ?? new SessionContext("FerretSharp", "Tests"), Ct);
    }

    [Fact]
    public async Task Ping_round_trips()
    {
        await using var session = await OpenAsync();

        var elapsed = await session.PingAsync(Ct);

        Assert.True(elapsed > TimeSpan.Zero);
        Assert.False(string.IsNullOrEmpty(session.ServerVersion));
    }

    [Fact]
    public async Task Session_context_is_visible_to_the_server()
    {
        await using var session = await OpenAsync(new SessionContext("FerretSharp", "Workspace Bug 3711", "integration-test"));

        var values = await session.ExecuteReaderAsync(
            "SELECT SYS_CONTEXT('USERENV', 'MODULE'), SYS_CONTEXT('USERENV', 'ACTION'), SYS_CONTEXT('USERENV', 'CLIENT_INFO') FROM DUAL",
            [],
            async (reader, ct) =>
            {
                await reader.ReadAsync(ct);
                return (reader.GetString(0), reader.GetString(1), reader.GetString(2));
            },
            Ct);

        Assert.Equal(("FerretSharp", "Workspace Bug 3711", "integration-test"), values);
    }

    [Fact]
    public async Task Set_action_takes_effect_on_the_next_round_trip()
    {
        await using var session = await OpenAsync(new SessionContext("FerretSharp", "Workspace 1"));

        await session.SetActionAsync("Bug 3711", Ct);

        Assert.Equal("Bug 3711", await ActionAsync(session));
    }

    /// <summary>Raw non-ASCII values make ODP.NET 23.26 lose the session (ORA-12537), so they are transliterated.</summary>
    [Fact]
    public async Task Non_ascii_session_attributes_keep_the_session_alive()
    {
        await using var session = await OpenAsync(new SessionContext("FerretSharp", "Prüfung – Änderung", "Grüße"));

        await session.SetActionAsync("Straße", Ct);

        Assert.Equal("Strasse", await ActionAsync(session));
        Assert.True(await session.PingAsync(Ct) > TimeSpan.Zero);
    }

    private static Task<string> ActionAsync(OracleSession session) => session.ExecuteReaderAsync(
        "SELECT SYS_CONTEXT('USERENV', 'ACTION') FROM DUAL",
        [],
        async (reader, ct) =>
        {
            await reader.ReadAsync(ct);
            return reader.GetString(0);
        },
        Ct);

    [Fact]
    public async Task Sorting_and_comparison_are_binary_regardless_of_the_windows_locale()
    {
        await using var session = await OpenAsync();

        var values = await session.ExecuteReaderAsync(
            "SELECT SYS_CONTEXT('USERENV', 'NLS_SORT'), (SELECT value FROM nls_session_parameters WHERE parameter = 'NLS_COMP') FROM DUAL",
            [],
            async (reader, ct) =>
            {
                await reader.ReadAsync(ct);
                return (reader.GetString(0), reader.GetString(1));
            },
            Ct);

        Assert.Equal(("BINARY", "BINARY"), values);
    }

    /// <summary>The session refuses DML before it reaches the server; the row stays.</summary>
    [Fact]
    public async Task Writing_statements_are_refused_and_change_nothing()
    {
        await SampleSchema.EnsureCreatedAsync(oracle.RequireConnectionString(), Ct);
        await using var session = await OpenAsync();
        const string count = "SELECT COUNT(*) FROM GRID_TEST WHERE ID = 1";
        Task<decimal> CountAsync() => session.ExecuteReaderAsync(count, [], async (reader, ct) =>
        {
            await reader.ReadAsync(ct);
            return reader.GetDecimal(0);
        }, Ct);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => session.ExecuteReaderAsync("DELETE FROM GRID_TEST WHERE ID = 1", [], (reader, ct) => reader.ReadAsync(ct), Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => session.ExecuteReaderAsync("SELECT * FROM GRID_TEST WHERE ID = 1 FOR UPDATE", [], (reader, ct) => reader.ReadAsync(ct), Ct));

        Assert.Equal(1m, await CountAsync());
    }

    [Fact]
    public async Task Bind_variables_are_bound_by_name()
    {
        await using var session = await OpenAsync();

        var result = await session.ExecuteReaderAsync(
            "SELECT :b || :a FROM DUAL",
            [new("a", "world"), new("b", "hello ")],
            async (reader, ct) =>
            {
                await reader.ReadAsync(ct);
                return reader.GetString(0);
            },
            Ct);

        Assert.Equal("hello world", result);
    }

    [Fact]
    public async Task Cancelling_a_long_query_stops_it_and_keeps_the_session_usable()
    {
        await using var session = await OpenAsync();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cts.CancelAfter(TimeSpan.FromSeconds(1));
        var stopwatch = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.ExecuteReaderAsync(
            "SELECT COUNT(*) FROM all_objects a CROSS JOIN all_objects b CROSS JOIN all_objects c",
            [],
            (reader, ct) => reader.ReadAsync(ct),
            cts.Token));

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(15), $"Cancellation took {stopwatch.Elapsed}.");
        Assert.True(await session.PingAsync(Ct) > TimeSpan.Zero);
    }
}
