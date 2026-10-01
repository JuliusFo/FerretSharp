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
