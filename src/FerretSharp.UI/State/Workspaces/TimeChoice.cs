using System.Globalization;

namespace FerretSharp.UI.State;

/// <summary>
/// The time of the time dialog (WP-78): typed as local date and time (<c>datetime-local</c> with seconds), or one of the
/// quick choices before now; with the hint how far the undo reaches about.
/// </summary>
public static class TimeChoice
{
    /// <summary>The quick choices in minutes before now (decision of the user, 2026-11-09).</summary>
    public static readonly IReadOnlyList<int> PresetMinutes = [5, 15, 60];

    /// <summary>The format of a <c>datetime-local</c> input with seconds.</summary>
    private const string InputFormat = "yyyy-MM-dd'T'HH:mm:ss";

    /// <summary>The time as the input shows it (local, to the second).</summary>
    public static string ToInput(DateTimeOffset time) => time.ToLocalTime().ToString(InputFormat, CultureInfo.InvariantCulture);

    /// <summary>
    /// The time typed into the input, as local time; null for an empty or broken one. Chromium leaves out the seconds when they
    /// are 0 (<c>2026-11-09T09:30</c>).
    /// </summary>
    public static DateTimeOffset? FromInput(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)
            || !DateTime.TryParseExact(text.Trim(), [InputFormat, "yyyy-MM-dd'T'HH:mm", "yyyy-MM-dd'T'HH:mm:ss.FFF"], CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal, out var local))
        {
            return null;
        }

        return new DateTimeOffset(local);
    }

    /// <summary><paramref name="minutes"/> before <paramref name="now"/>, to the second.</summary>
    public static DateTimeOffset Preset(int minutes, DateTimeOffset now)
    {
        var at = now.AddMinutes(-minutes);
        return at.AddTicks(-(at.Ticks % TimeSpan.TicksPerSecond));
    }

    /// <summary>The earliest time the undo reaches about, <paramref name="reach"/> before <paramref name="now"/>; null without a hint.</summary>
    public static DateTimeOffset? Earliest(TimeSpan? reach, DateTimeOffset now) => reach is { } r && r > TimeSpan.Zero ? now - r : null;
}
