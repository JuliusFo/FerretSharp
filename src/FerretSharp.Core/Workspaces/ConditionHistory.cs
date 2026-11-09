using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Workspaces;

/// <summary>A condition applied in a table tab (WP-79), as written: kept with its literals, like the SQL editor's history.</summary>
public sealed record ConditionHistoryEntry(string Owner, string Table, string Text, DateTimeOffset UsedAt)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public TableRef TableRef => new(Owner, Table);
}

/// <summary>
/// The conditions last applied in table tabs, one file per connection (<c>conditions\{id}.json</c>), newest first: at most
/// <see cref="PerTable"/> per table and <see cref="MaxEntries"/> in all. Applying a condition again moves it to the front.
/// </summary>
public sealed class ConditionHistoryStore(string directory) : HistoryFileStore<ConditionHistoryEntry>(directory, FileVersion)
{
    public const int PerTable = 10;
    public const int MaxEntries = 1000;
    private const int FileVersion = 1;

    /// <summary>The conditions of one table in <paramref name="history"/>, newest first.</summary>
    public static IReadOnlyList<string> Of(IReadOnlyList<ConditionHistoryEntry> history, TableRef table) =>
        [.. history.Where(e => e.TableRef == table).Select(e => e.Text)];

    protected override void Add(List<ConditionHistoryEntry> entries, ConditionHistoryEntry entry)
    {
        entries.RemoveAll(e => e.TableRef == entry.TableRef && Same(e.Text, entry.Text));
        entries.Insert(0, entry);
        var ofTable = 0;
        entries.RemoveAll(e => e.TableRef == entry.TableRef && ++ofTable > PerTable);
        if (entries.Count > MaxEntries)
        {
            entries.RemoveRange(MaxEntries, entries.Count - MaxEntries);
        }
    }

    /// <summary>Entries set to null in the file by hand are left out.</summary>
    protected override IReadOnlyList<ConditionHistoryEntry> Normalize(IEnumerable<ConditionHistoryEntry> entries) =>
        [.. entries.Where(e => e is { Owner: not null, Table: not null, Text: not null })];

    private static bool Same(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.Ordinal);
}
