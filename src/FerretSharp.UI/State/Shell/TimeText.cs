using System.Globalization;

namespace FerretSharp.UI.State;

/// <summary>
/// Times as the footers, the LINQ console and the model page show them (R5b, review 3.31.0 B12: five ways to write a
/// duration, two for the time of a snapshot), and the run clock (<see cref="Clock"/>). Plan steps keep their own finer form.
/// </summary>
public static class TimeText
{
    /// <summary>"12 ms" below a second, "1,4 s" below a minute, "2 min 5 s" above.</summary>
    public static string Duration(TimeSpan elapsed) => elapsed.TotalMilliseconds switch
    {
        < 1000 => $"{elapsed.TotalMilliseconds.ToString("0", CultureInfo.CurrentCulture)} ms",
        < 60_000 => $"{elapsed.TotalSeconds.ToString("0.0", CultureInfo.CurrentCulture)} s",
        _ => $"{(int)elapsed.TotalMinutes} min {elapsed.Seconds} s",
    };

    /// <summary>"0:07", "12:45", "1:02:03": the run clock (<c>RunProgress</c>) and how long a statement waits (WP-52).</summary>
    public static string Clock(TimeSpan elapsed)
    {
        var seconds = Math.Max(0, (long)elapsed.TotalSeconds);
        return seconds >= 3600
            ? $"{seconds / 3600}:{seconds / 60 % 60:00}:{seconds % 60:00}"
            : $"{seconds / 60}:{seconds % 60:00}";
    }

    /// <summary>A time of day in the user's culture and time zone ("14:30:05"), e.g. of a read-only snapshot.</summary>
    public static string TimeOfDay(DateTimeOffset at) => at.ToLocalTime().ToString("T", CultureInfo.CurrentCulture);
}
