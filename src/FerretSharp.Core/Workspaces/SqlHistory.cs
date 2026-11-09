using FerretSharp.Core.Query;

namespace FerretSharp.Core.Workspaces;

/// <summary>A statement run in the SQL editor (WP-17).</summary>
/// <param name="Rows">Rows read (queries: the rows loaded first) or changed (DML); null after an error.</param>
/// <param name="MoreRows">A query had more rows than were loaded.</param>
/// <param name="Changed">DML: <see cref="Rows"/> were changed.</param>
/// <param name="Error">The error (ORA code and message) if it failed.</param>
/// <param name="Variables">Bind variables as used; on Prod connections without values.</param>
public sealed record SqlHistoryEntry(
    string Sql,
    DateTimeOffset ExecutedAt,
    TimeSpan Elapsed,
    int? Rows,
    bool MoreRows,
    bool Changed,
    string? Error,
    IReadOnlyList<SqlVariable> Variables)
{
    /// <summary>DDL that ran (WP-22): it changed the schema and was committed at once. Absent in older files: false.</summary>
    public bool Ddl { get; init; }

    /// <summary>The entry without bind values (Prod: values are not kept, as in logs and error dialogs).</summary>
    public SqlHistoryEntry WithoutValues() => this with { Variables = Variables.Select(v => v with { Value = "" }).ToList() };
}

/// <summary>
/// The SQL editor's history, one file per connection (<c>history\{id}.json</c>), newest first, at most
/// <see cref="MaxEntries"/>. Running the same statement again replaces its last entry instead of adding one.
/// </summary>
public sealed class SqlHistoryStore(string directory) : HistoryFileStore<SqlHistoryEntry>(directory, FileVersion)
{
    public const int MaxEntries = 500;
    private const int FileVersion = 1;

    protected override void Add(List<SqlHistoryEntry> entries, SqlHistoryEntry entry)
    {
        if (entries.Count > 0 && Same(entries[0].Sql, entry.Sql))
        {
            entries.RemoveAt(0);
        }

        entries.Insert(0, entry);
        if (entries.Count > MaxEntries)
        {
            entries.RemoveRange(MaxEntries, entries.Count - MaxEntries);
        }
    }

    private static bool Same(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.Ordinal);
}
