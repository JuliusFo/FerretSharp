using FerretSharp.Core.Connections;
using FerretSharp.Core.Oracle;
using FerretSharp.Testing;
using NSubstitute;

namespace FerretSharp.Core.Tests.Oracle;

public class OracleConnectionTesterTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("ORA-01017: invalid credential or not authorized; logon denied", "invalid credential or not authorized; logon denied")]
    [InlineData("ORA-12514: Cannot connect\nhttps://docs.oracle.com/error-help", "Cannot connect")]
    [InlineData("Plain message", "Plain message")]
    public void Clean_message_strips_code_prefix_and_extra_lines(string raw, string expected)
    {
        Assert.Equal(expected, OracleConnectionTester.CleanMessage(raw));
    }

    /// <summary>
    /// fix/robustness: the test opened with a single attempt – when opening hung over the VPN it waited about 20 s, while
    /// connecting tried again after 5 s and opened at once. Now it opens like the explorer (<see cref="SessionOpening"/>).
    /// </summary>
    [Fact]
    public async Task A_hanging_open_is_tried_again_past_its_limit()
    {
        var time = new CountingTimeProvider();
        var hanging = new TaskCompletionSource<IDatabaseConnection>();
        var opened = FakeDatabase.Connection(serverVersion: "19.0.0.0.0");
        var calls = 0;
        var connector = Substitute.For<IDatabaseConnector>();
        connector.OpenAsync(default!, default!, default!, Ct).ReturnsForAnyArgs(_ => Interlocked.Increment(ref calls) == 1 ? hanging.Task : Task.FromResult(opened));
        var profile = new ConnectionProfile(Guid.NewGuid(), "Test", ConnectionKind.Test, new HostPortAddress("h", 1521, "S", null), "u", null, ReadOnly: false);

        var testing = new OracleConnectionTester(connector, time).TestAsync(profile, "secret", Ct);
        await Wait.UntilAsync(() => Volatile.Read(ref calls) == 1 && time.Timers == 1);
        time.Advance(SessionOpening.AttemptLimits[0]);
        var result = await testing.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        Assert.True(result.Success, result.Message);
        Assert.Equal(("19.0.0.0.0", 2), (result.ServerVersion, calls));
        await opened.Received(1).DisposeAsync();
    }
}
