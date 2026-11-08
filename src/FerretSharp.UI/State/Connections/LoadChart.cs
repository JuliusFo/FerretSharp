using System.Globalization;
using FerretSharp.Core.ServerLoad;

namespace FerretSharp.UI.State;

/// <summary>
/// The history of the server load as lines of an SVG (WP-75): x is the time over <see cref="Span"/> up to the latest look,
/// y the value up to <c>max</c> (SVG coordinates, y downwards). A look after a gap starts a new line – the page was closed,
/// nothing is known in between.
/// </summary>
public static class LoadChart
{
    /// <summary>The shortest time the axis shows: a few minutes of looks would otherwise fill the whole width with two points.</summary>
    public static readonly TimeSpan MinSpan = TimeSpan.FromMinutes(10);

    /// <summary>
    /// The time the axis shows: from the first look kept up to the latest, at least <see cref="MinSpan"/>, at most
    /// <see cref="ServerLoadTracker.HistoryLength"/> (E2E: on a one-hour axis the first minutes were a dot at the right edge).
    /// </summary>
    public static TimeSpan Span(IReadOnlyList<LoadSample> history)
    {
        if (history.Count == 0)
        {
            return MinSpan;
        }

        var seen = history[^1].To - history[0].To;
        var minutes = Math.Ceiling(seen.TotalMinutes / 5) * 5; // a round number for the axis
        return TimeSpan.FromMinutes(Math.Clamp(minutes, MinSpan.TotalMinutes, ServerLoadTracker.HistoryLength.TotalMinutes));
    }

    /// <summary>The <c>points</c> of each line, invariant numbers with one decimal.</summary>
    public static IReadOnlyList<string> Lines(
        IReadOnlyList<LoadSample> history, Func<LoadSample, double?> value, double max, double width, double height, TimeSpan span)
    {
        if (history.Count == 0 || max <= 0)
        {
            return [];
        }

        var end = history[^1].To;
        var start = end - span;
        var lines = new List<string>();
        var points = new List<string>();
        foreach (var sample in history.Where(s => s.To >= start))
        {
            // A look after a gap is an average over the gap: not drawn, the line after it is a new one.
            if (sample.AfterGap || value(sample) is not { } shown)
            {
                Flush();
                continue;
            }

            points.Add(Point((sample.To - start) / span * width, height - Math.Clamp(shown / max, 0, 1) * height));
        }

        Flush();
        return lines;

        void Flush()
        {
            if (points.Count > 0)
            {
                lines.Add(string.Join(' ', points));
                points.Clear();
            }
        }
    }

    /// <summary>
    /// The top of the scale for the active sessions: the most active sessions with some room, rounded up to a value the axis
    /// can name (<see cref="RoundUp"/>), at least one. The CPUs are a line only when they lie below it – 32 CPUs on a quiet
    /// server would press the line flat to the ground.
    /// </summary>
    public static double SessionsMax(IReadOnlyList<LoadSample> history) =>
        RoundUp(Math.Max(1, (history.Count == 0 ? 0 : history.Max(s => s.ActiveSessions)) * 1.1));

    /// <summary>Steps of a decade the top of a scale may take: its half is a round number too.</summary>
    private static readonly double[] Steps = [1, 1.5, 2, 2.5, 3, 4, 5, 6, 8, 10];

    /// <summary>The smallest step (1, 1.5, 2, 2.5, 3, 4, 5, 6, 8 times a power of ten) at or above <paramref name="value"/>.</summary>
    public static double RoundUp(double value)
    {
        if (value <= 0 || !double.IsFinite(value))
        {
            return 1;
        }

        var decade = Math.Pow(10, Math.Floor(Math.Log10(value)));
        var step = Steps.First(s => s * decade >= value * (1 - 1e-9)); // 10 always fits; the tolerance keeps 3 at 3, not 4
        return Math.Round(step * decade, 6);
    }

    /// <summary>The height of a horizontal line at <paramref name="value"/>.</summary>
    public static string Y(double value, double max, double height) =>
        (height - Math.Clamp(value / max, 0, 1) * height).ToString("0.0", CultureInfo.InvariantCulture);

    private static string Point(double x, double y) =>
        $"{x.ToString("0.0", CultureInfo.InvariantCulture)},{y.ToString("0.0", CultureInfo.InvariantCulture)}";
}
