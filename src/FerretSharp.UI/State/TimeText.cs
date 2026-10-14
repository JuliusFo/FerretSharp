using System.Globalization;

namespace FerretSharp.UI.State;

/// <summary>
/// Times as the footers, the LINQ console and the model page show them (R5b, review 3.31.0 B12: five ways to write a
/// duration, two for the time of a snapshot). Plan steps keep their own finer form, the run clock its <c>m:ss</c>.
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

    /// <summary>A time of day in the user's culture and time zone ("14:30:05"), e.g. of a read-only snapshot.</summary>
    public static string TimeOfDay(DateTimeOffset at) => at.ToLocalTime().ToString("T", CultureInfo.CurrentCulture);
}
