using FerretSharp.Core.ServerLoad;
using FerretSharp.UI.State;

namespace FerretSharp.UI.Tests.State;

public sealed class LoadChartTests
{
    private static readonly DateTime End = new(2026, 11, 7, 11, 0, 0);

    private static LoadSample Sample(double minutesBeforeEnd, double sessions, bool afterGap = false) =>
        new(End.AddMinutes(-minutesBeforeEnd - 0.5), End.AddMinutes(-minutesBeforeEnd), 4, null, sessions, 0, 0, 0, 0,
            new Dictionary<string, double>(), afterGap);

    [Fact]
    public void A_gap_starts_a_new_line_and_older_than_the_span_and_values_beyond_the_scale_are_cut()
    {
        LoadSample[] history =
        [
            Sample(70, 1), // older than the span: left out
            Sample(60, 2),
            Sample(30, 8), // above the scale: at the top
            Sample(15, 3, afterGap: true), // the average over a gap: not drawn, a new line begins
            Sample(0, 0),
        ];

        var lines = LoadChart.Lines(history, s => s.ActiveSessions, 4, 600, 100, TimeSpan.FromHours(1));

        Assert.Equal(["0.0,50.0 300.0,0.0", "600.0,100.0"], lines);
    }

    [Theory]
    [InlineData(2, 10)] // a few minutes: still ten on the axis, the points do not fill the width
    [InlineData(23, 25)] // rounded up to five minutes
    [InlineData(150, 60)] // never more than the history keeps
    public void The_axis_spans_the_looks_seen_between_ten_and_sixty_minutes(double seenMinutes, double expectedMinutes) =>
        Assert.Equal(TimeSpan.FromMinutes(expectedMinutes), LoadChart.Span([Sample(seenMinutes, 1), Sample(0, 1)]));

    [Fact]
    public void The_scale_of_the_sessions_follows_the_most_active_with_room_and_is_at_least_one() =>
        Assert.Equal([1.0, 12.5], [LoadChart.SessionsMax([Sample(0, 0.3)]), LoadChart.SessionsMax([Sample(1, 10), Sample(0, 2)])]);
}
