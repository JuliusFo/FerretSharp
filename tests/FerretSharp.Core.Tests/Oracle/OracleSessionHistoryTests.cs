using FerretSharp.Core.Oracle;

namespace FerretSharp.Core.Tests.Oracle;

/// <summary>
/// Diagnostics of lost connections (ORA-12570 on the explorer session after the record diagram, 3.47.0): the log entry says
/// how long the session was idle and what its last break was – network or a cancelled command left out of step.
/// </summary>
public sealed class OracleSessionHistoryTests
{
    [Fact]
    public void A_session_without_a_break_says_so()
    {
        Assert.Equal("idle 125.0 s before this command; no break sent since the session opened",
            OracleSession.DescribeHistory(null, idleBefore: 125_000, now: 1_000_000));
    }

    [Fact]
    public void A_break_after_its_command_had_returned_is_named_with_the_commands_since()
    {
        var last = new OracleSession.SentBreak(sentAt: 986_000, intoCommand: 812, afterReturn: true) { Ended = "normally" };
        last.CountCommand();

        Assert.Equal(
            "idle 13.2 s before this command; last break 14.0 s ago, after its command had returned (it ended normally), 1 command(s) since, this one included",
            OracleSession.DescribeHistory(last, idleBefore: 13_200, now: 1_000_000));
    }

    [Fact]
    public void A_break_during_the_failing_command_says_how_far_into_it()
    {
        var last = new OracleSession.SentBreak(sentAt: 999_500, intoCommand: 30_000, afterReturn: false);

        Assert.Equal("idle 0.0 s before this command; last break 0.5 s ago, 30.0 s into its command (it ended not yet), during this command",
            OracleSession.DescribeHistory(last, idleBefore: 0, now: 1_000_000));
    }
}
