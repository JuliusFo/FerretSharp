using FerretSharp.Core.Audit;
using FerretSharp.Core.Connections;
using FerretSharp.Core.Settings;

namespace FerretSharp.UI.State;

/// <summary>A connection group in the audit section of the settings (WP-51).</summary>
/// <param name="Group">The group as the connections name it; for an orphaned entry as the settings name it.</param>
/// <param name="Path">Its template file; null for none (the global template applies).</param>
/// <param name="Orphaned">No connection has the group any more (renamed or deleted): the entry stays until removed (decision of the user, 2026-10-17).</param>
public sealed record AuditGroupTemplate(string Group, string? Path, bool Orphaned);

/// <summary>The audit templates of connection groups as the settings page lists and changes them (WP-51).</summary>
public static class AuditGroupTemplates
{
    /// <summary>
    /// Every group the connections have, with its template if it has one, and the entries of groups no connection has any
    /// more – ordered by name as the connection list orders groups.
    /// </summary>
    public static IReadOnlyList<AuditGroupTemplate> Of(IEnumerable<ConnectionProfile> profiles, IReadOnlyDictionary<string, string> entries)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(entries);
        var groups = ConnectionSearch.GroupNames(profiles);
        var rows = groups.Select(g => new AuditGroupTemplate(g, AuditTemplateSource.GroupEntry(entries, g)?.Value, Orphaned: false)).ToList();
        rows.AddRange(entries
            .Where(e => !string.IsNullOrWhiteSpace(e.Value) && !groups.Any(g => ConnectionSearch.SameGroup(g, e.Key)))
            .Select(e => new AuditGroupTemplate(e.Key, e.Value, Orphaned: true)));
        return rows.OrderBy(r => r.Group, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>
    /// <paramref name="entries"/> with the template of <paramref name="group"/> set to <paramref name="path"/> – empty removes
    /// it. Entries of the same group in another spelling go, so a group has one entry.
    /// </summary>
    public static SettingsMap With(SettingsMap entries, string group, string? path)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var name = ConnectionSearch.NormalizeGroup(group) ?? throw new ArgumentException("A group has a name.", nameof(group));
        var result = entries;
        foreach (var key in entries.Keys.Where(k => ConnectionSearch.SameGroup(k, name)))
        {
            result = result.Without(key);
        }

        return string.IsNullOrWhiteSpace(path) ? result : result.With(name, path.Trim().Trim('"'));
    }
}
