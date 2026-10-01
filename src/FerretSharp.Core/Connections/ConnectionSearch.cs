namespace FerretSharp.Core.Connections;

/// <param name="Name">Group name; null for ungrouped connections (always listed last).</param>
public sealed record ConnectionGroup(string? Name, IReadOnlyList<ConnectionProfile> Profiles);

/// <summary>Filtering and grouping for the connection switcher and the connections page.</summary>
public static class ConnectionSearch
{
    private static readonly StringComparer GroupComparer = StringComparer.CurrentCultureIgnoreCase;

    /// <summary>
    /// Every whitespace-separated term must occur (case-insensitive) in the name, group, address, user or kind.
    /// </summary>
    public static bool Matches(ConnectionProfile profile, string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return true;
        }

        string[] haystack = [profile.Name, profile.Group ?? "", profile.Address.Display, profile.User, profile.Kind.ToString()];
        return query
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .All(term => haystack.Any(h => h.Contains(term, StringComparison.CurrentCultureIgnoreCase)));
    }

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

    /// <summary>Distinct existing group names, for suggestions in the edit dialog.</summary>
    public static IReadOnlyList<string> GroupNames(IEnumerable<ConnectionProfile> profiles) =>
        profiles
            .Select(p => NormalizeGroup(p.Group))
            .OfType<string>()
            .Distinct(GroupComparer)
            .Order(GroupComparer)
            .ToList();

    private static string? NormalizeGroup(string? group) => string.IsNullOrWhiteSpace(group) ? null : group.Trim();
}
