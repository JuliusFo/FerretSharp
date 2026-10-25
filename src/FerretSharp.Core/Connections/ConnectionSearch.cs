using FerretSharp.Core.Query;

namespace FerretSharp.Core.Connections;

/// <param name="Name">Group name; null for ungrouped connections (always listed last).</param>
public sealed record ConnectionGroup(string? Name, IReadOnlyList<ConnectionProfile> Profiles);

/// <summary>Filtering and grouping for the connection switcher and the connections page.</summary>
public static class ConnectionSearch
{
    private static readonly StringComparer GroupComparer = StringComparer.CurrentCultureIgnoreCase;

    /// <summary>Every word must occur in the name, group, address, user or kind (<see cref="TextSearch"/>).</summary>
    public static bool Matches(ConnectionProfile profile, string? query) =>
        new TextSearch(query).Matches(profile.Name, profile.Group, profile.Address.Display, profile.User, profile.Kind.ToString());

    /// <summary>
    /// Groups alphabetically (ungrouped last); within a group by kind (Dev, Test, Prod, Other), then name.
    /// Groups that differ only in case are merged.
    /// </summary>
    public static IReadOnlyList<ConnectionGroup> Group(IEnumerable<ConnectionProfile> profiles, string? query = null) =>
        profiles
            .Where(p => Matches(p, query))
            .GroupBy(p => NormalizeGroup(p.Group), GroupComparer!)
            .OrderBy(g => g.Key is null)
            .ThenBy(g => g.Key, GroupComparer)
            .Select(g => new ConnectionGroup(
                g.Key,
                g.OrderBy(p => p.Kind).ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList()))
            .ToList();

    /// <summary>All connections in the order of <see cref="Group"/>, for flat lists such as a native select.</summary>
    public static IReadOnlyList<ConnectionProfile> InListOrder(IEnumerable<ConnectionProfile> profiles) =>
        Group(profiles).SelectMany(g => g.Profiles).ToList();

    /// <summary>Distinct existing group names, for suggestions in the edit dialog.</summary>
    public static IReadOnlyList<string> GroupNames(IEnumerable<ConnectionProfile> profiles) =>
        profiles
            .Select(p => NormalizeGroup(p.Group))
            .OfType<string>()
            .Distinct(GroupComparer)
            .Order(GroupComparer)
            .ToList();

    /// <summary>The same group as the lists show it: trimmed, case ignored; no group is never the same as another.</summary>
    public static bool SameGroup(string? a, string? b) =>
        NormalizeGroup(a) is { } left && NormalizeGroup(b) is { } right && GroupComparer.Equals(left, right);

    /// <summary>
    /// The same entry of something kept per group, where connections without a group share one (the work clone, WP-40) –
    /// unlike <see cref="SameGroup"/>, for which no group is never the same (review 3.44.0, B8: the rule stood inline twice).
    /// </summary>
    public static bool SameGroupKey(string? a, string? b) =>
        (NormalizeGroup(a), NormalizeGroup(b)) switch
        {
            (null, null) => true,
            ({ } left, { } right) => GroupComparer.Equals(left, right),
            _ => false,
        };

    /// <summary>How group names compare: case-insensitive in the user's culture.</summary>
    public static StringComparer GroupNameComparer => GroupComparer;

    /// <summary>The group name trimmed; null for none.</summary>
    public static string? NormalizeGroup(string? group) => string.IsNullOrWhiteSpace(group) ? null : group.Trim();
}
