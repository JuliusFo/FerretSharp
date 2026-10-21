using FerretSharp.Core.Connections;
using FerretSharp.Core.Settings;

namespace FerretSharp.UI.State;

/// <summary>
/// How a connection is named outside the connection list, where its group heading is not in sight: with the group in front
/// (<c>Kunde A › DEV</c>) while <see cref="AppSettings.ShowConnectionGroup"/> is on. Typing the name to unlock Prod stays
/// the bare name.
/// </summary>
public static class ConnectionLabel
{
    public const string Separator = " › ";

    /// <summary>The group to show in front of the name; null if the setting is off or the connection has none.</summary>
    public static string? GroupOf(ConnectionProfile profile, AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(settings);
        return settings.ShowConnectionGroup && !string.IsNullOrWhiteSpace(profile.Group) ? profile.Group.Trim() : null;
    }

    /// <summary>The name as one text, for dialogs and lists: <c>Kunde A › DEV</c>, or just <c>DEV</c>.</summary>
    public static string Of(ConnectionProfile profile, AppSettings settings) =>
        GroupOf(profile, settings) is { } group ? group + Separator + profile.Name : profile.Name;
}
