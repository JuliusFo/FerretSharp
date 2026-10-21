using FerretSharp.Core.Connections;

namespace FerretSharp.Core.Tests.Connections;

public sealed class ReconnectPolicyTests
{
    [Fact]
    public void Pauses_grow_and_then_stay_at_a_minute()
    {
        var pauses = Enumerable.Range(1, 5).Select(attempt => ReconnectPolicy.PauseAfter(attempt, TimeSpan.Zero)).ToList();

        Assert.Equal([TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1)], pauses);
    }

    [Fact]
    public void No_attempt_starts_later_than_ten_minutes_after_the_loss()
    {
        Assert.Equal(TimeSpan.FromMinutes(1), ReconnectPolicy.PauseAfter(9, TimeSpan.FromMinutes(9)));
        Assert.Null(ReconnectPolicy.PauseAfter(10, TimeSpan.FromMinutes(9) + TimeSpan.FromSeconds(1)));
    }

    [Theory]
    [InlineData("ORA-12170")] // connect timeout
    [InlineData("ORA-12541")] // no listener
    [InlineData("ORA-12543")] // destination host unreachable
    [InlineData("ORA-12514")] // service not registered yet (instance starting)
    [InlineData("ORA-03113")] // lost while logging in
    [InlineData("ORA-01033")] // initialization in progress
    [InlineData("ORA-50000")] // ODP.NET: connection request timed out
    public void Tries_again_while_network_or_database_are_away(string code) =>
        Assert.True(ReconnectPolicy.IsWorthRetrying(new ConnectionError("away", code)));

    [Fact]
    public void Tries_again_after_an_open_that_hit_its_limit() =>
        Assert.True(ReconnectPolicy.IsWorthRetrying(new ConnectionError("did not open", null) { TimedOut = true }));

    /// <summary>Another wrong password counts towards FAILED_LOGIN_ATTEMPTS and can lock the account.</summary>
    [Theory]
    [InlineData("ORA-01017")] // invalid username/password
    [InlineData("ORA-28000")] // account locked
    [InlineData("ORA-28001")] // password expired
    [InlineData("ORA-01045")] // no CREATE SESSION
    [InlineData("ORA-12899")] // not TNS, whatever it is
    [InlineData(null)] // no password saved, bad configuration, anything else
    public void Stops_on_everything_else(string? code) =>
        Assert.False(ReconnectPolicy.IsWorthRetrying(new ConnectionError("refused", code)));
}
