using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Workspaces;

/// <summary>
/// A table, view or PL/SQL unit whose tab was active (WP-91): the real object (<see cref="Owner"/>, <see cref="Name"/>), as
/// the schema lists it, so it is found again after its tab was closed.
/// </summary>
public sealed record RecentObject(SchemaObjectKind Kind, string Owner, string Name, DateTimeOffset OpenedAt)
{
    public static RecentObject Of(TableSummary table, DateTimeOffset at) => new(SchemaObjectKind.Table, table.Owner, table.Name, at);

    public static RecentObject Of(PlSqlObjectSummary unit, DateTimeOffset at) => new(SchemaObjects.KindOf(unit.Kind), unit.Owner, unit.Name, at);

    /// <summary>The same object, whenever it was opened.</summary>
    public bool Same(RecentObject other) => other.Kind == Kind && other.Owner == Owner && other.Name == Name;
}

/// <summary>
/// The objects last opened, one file per connection (<c>recent-objects\{id}.json</c>, WP-91), newest first, each once, at
/// most <see cref="MaxEntries"/>. Only names – no filter, no value. The file handling is the SQL history's.
/// </summary>
public sealed class RecentObjectStore(string directory) : HistoryFileStore<RecentObject>(directory, FileVersion)
{
    public const int MaxEntries = 30;
    private const int FileVersion = 1;

    protected override void Add(List<RecentObject> entries, RecentObject entry)
    {
        entries.RemoveAll(entry.Same);
        entries.Insert(0, entry);
        if (entries.Count > MaxEntries)
        {
            entries.RemoveRange(MaxEntries, entries.Count - MaxEntries);
        }
    }

    /// <summary>Entries edited by hand: without owner or name, of an unknown kind or twice are left out.</summary>
    protected override IReadOnlyList<RecentObject> Normalize(IEnumerable<RecentObject> entries)
    {
        var result = new List<RecentObject>();
        foreach (var entry in entries)
        {
            if (entry is { Owner: not null, Name: not null } && Enum.IsDefined(entry.Kind) && !result.Exists(entry.Same) && result.Count < MaxEntries)
            {
                result.Add(entry);
            }
        }

        return result;
    }
}
