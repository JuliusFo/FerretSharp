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
    public void A_gap_starts_a_new_line_and_older_than_an_hour_and_values_beyond_the_scale_are_cut()
    {
        LoadSample[] history =
        [
            Sample(70, 1), // older than the hour: left out
            Sample(60, 2),
            Sample(30, 8), // above the scale: at the top
            Sample(15, 3, afterGap: true), // the average over a gap: not drawn, a new line begins
            Sample(0, 0),
        ];

        var lines = LoadChart.Lines(history, s => s.ActiveSessions, 4, 600, 100);

        Assert.Equal(["0.0,50.0 300.0,0.0", "600.0,100.0"], lines);
    }

    [Fact]
    public void The_scale_of_the_sessions_is_the_cpus_or_the_most_active_sessions_with_room() =>
        Assert.Equal((4.4, 11.0), (LoadChart.SessionsMax([Sample(0, 3)], 4), LoadChart.SessionsMax([Sample(0, 10)], 4)), new ToleranceComparer());

    private sealed class ToleranceComparer : IEqualityComparer<(double, double)>
    {
        public bool Equals((double, double) x, (double, double) y) => Math.Abs(x.Item1 - y.Item1) < 1e-9 && Math.Abs(x.Item2 - y.Item2) < 1e-9;

        public int GetHashCode((double, double) obj) => 0;
    }
}
