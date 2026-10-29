using System.Globalization;
using FerretSharp.Core.Resources;
using FerretSharp.UI.Resources;

namespace FerretSharp.UI.State;

/// <summary>When a connection was last used, as the connections page and the connection's menu (WP-66) say it.</summary>
public static class ConnectionLastUsed
{
    /// <summary>The text for connection <paramref name="id"/> in <paramref name="lastUsed"/> (review 3.54.0, B5: both places looked it up themselves).</summary>
    public static string Text(IReadOnlyDictionary<Guid, DateTimeOffset> lastUsed, Guid id, DateTime today) =>
        Text(lastUsed.TryGetValue(id, out var used) ? used : null, today);

    /// <summary>"today at 09:12", "yesterday at …", "3 days ago", else the date; empty if never.</summary>
    public static string Text(DateTimeOffset? lastUsed, DateTime today)
    {
        if (lastUsed is not { } used)
        {
            return "";
        }

        var local = used.ToLocalTime();
        var days = (today.Date - local.Date).Days;
        return days switch
        {
            0 => TextFormat.Format(ConnectionViewText.Connections_TodayAt, local),
            1 => TextFormat.Format(ConnectionViewText.Connections_YesterdayAt, local),
            < 7 => TextFormat.Format(ConnectionViewText.Connections_DaysAgo, days),
            _ => local.ToString("dd.MM.yyyy", CultureInfo.CurrentCulture),
        };
    }
}
