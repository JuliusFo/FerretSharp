using FerretSharp.Core.Audit;
using FerretSharp.Core.Settings;

namespace FerretSharp.Core.Connections;

/// <summary>
/// What renaming a connection group changes (WP-66): every connection of the group in one write, and what is kept per
/// group by name – the audit template (<see cref="AppSettings.AuditTemplateGroups"/>) and the report's exceptions file
/// (<see cref="AppSettings.ReportExceptionsGroups"/>, WP-72). The work clone and the group
/// state follow by name (<c>WorkClones.RenameGroupAsync</c>, <see cref="ConnectionGroupState.RenameAsync"/>).
/// A name another group has merges both: the target keeps its spelling and, if both have one, its audit template and
/// exceptions file.
/// </summary>
/// <param name="From">The group as the lists show it.</param>
/// <param name="To">The name the connections get; in a merge the spelling of the existing group.</param>
/// <param name="Profiles">The connections of the group with the new name.</param>
/// <param name="Merges">Another group has the name already.</param>
/// <param name="AuditTemplateGroups">The audit templates of the groups afterwards.</param>
/// <param name="DroppedAuditTemplate">The template of <paramref name="From"/> the target's replaces; null if none is lost.</param>
public sealed record ConnectionGroupRename(
    string From,
    string To,
    IReadOnlyList<ConnectionProfile> Profiles,
    bool Merges,
    SettingsMap AuditTemplateGroups,
    string? DroppedAuditTemplate)
{
    /// <summary>The exceptions files of the groups afterwards (WP-72).</summary>
    public SettingsMap ReportExceptionsGroups { get; init; } = SettingsMap.Empty;

    /// <summary>The exceptions file of <see cref="From"/> the target's replaces; null if none is lost.</summary>
    public string? DroppedReportExceptions { get; init; }

    /// <summary>Nothing would change: the same name, also in spelling.</summary>
    public bool IsUnchanged => string.Equals(From, To, StringComparison.Ordinal);

    /// <exception cref="ArgumentException"><paramref name="from"/> or <paramref name="to"/> is no group name.</exception>
    public static ConnectionGroupRename Plan(
        IEnumerable<ConnectionProfile> profiles, string from, string to, SettingsMap auditTemplateGroups, SettingsMap? reportExceptionsGroups = null)
    {
        var all = profiles.ToList();
        var source = ConnectionSearch.NormalizeGroup(from) ?? throw new ArgumentException("A group needs a name.", nameof(from));
        var typed = ConnectionSearch.NormalizeGroup(to) ?? throw new ArgumentException("A group needs a name.", nameof(to));

        var existing = ConnectionSearch.SameGroup(source, typed)
            ? null
            : all.Select(p => ConnectionSearch.NormalizeGroup(p.Group)).FirstOrDefault(g => ConnectionSearch.SameGroup(g, typed));
        var target = existing ?? typed;
        var merges = existing is not null;

        var moved = all.Where(p => ConnectionSearch.SameGroup(p.Group, source)).Select(p => p with { Group = target }).ToList();

        var (templates, dropped) = Move(auditTemplateGroups, source, target);
        var (exceptions, droppedExceptions) = Move(reportExceptionsGroups ?? SettingsMap.Empty, source, target);

        return new ConnectionGroupRename(source, target, moved, merges, templates, dropped)
        {
            ReportExceptionsGroups = exceptions,
            DroppedReportExceptions = droppedExceptions,
        };
    }

    /// <summary>The file of <paramref name="source"/> under <paramref name="target"/>, unless the target has one already.</summary>
    private static (SettingsMap Entries, string? Dropped) Move(SettingsMap entries, string source, string target)
    {
        var sourceFile = AuditTemplateSource.GroupEntry(entries, source)?.Value;
        // Also an entry of a group without connections (left after an earlier rename): it was set for this name.
        var targetFile = ConnectionSearch.SameGroup(source, target) ? null : AuditTemplateSource.GroupEntry(entries, target)?.Value;
        var result = SettingsMap.From(entries.Where(e => !ConnectionSearch.SameGroup(e.Key, source)));
        string? dropped = null;
        if (sourceFile is not null)
        {
            if (targetFile is null)
            {
                result = result.With(target, sourceFile);
            }
            else if (!string.Equals(sourceFile, targetFile, StringComparison.OrdinalIgnoreCase))
            {
                dropped = sourceFile;
            }
        }

        return (result, dropped);
    }
}
