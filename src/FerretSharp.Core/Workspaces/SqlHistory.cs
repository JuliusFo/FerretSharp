using FerretSharp.Core.IO;
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
public sealed class SqlHistoryStore(string directory)
{
    public const int MaxEntries = 500;
    private const int FileVersion = 1;

    private readonly SemaphoreSlim _gate = new(1, 1);

    public string Directory { get; } = directory;

    /// <summary>Newest first; empty if there is none or the file cannot be read.</summary>
    public async Task<IReadOnlyList<SqlHistoryEntry>> LoadAsync(Guid connectionId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return (await ReadAsync(connectionId, cancellationToken)).Entries;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <returns>
    /// The history with the new entry. A file that could not be read for the moment (locked) or comes from a newer
    /// FerretSharp is not overwritten – the entry is then only returned, not stored. A broken one is kept as a copy
    /// (<c>….json.bad-…</c>) before a new history replaces it (R5b: it was replaced without a copy).
    /// </returns>
    public async Task<IReadOnlyList<SqlHistoryEntry>> AddAsync(Guid connectionId, SqlHistoryEntry entry, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var (stored, write) = await ReadAsync(connectionId, cancellationToken);
            var canWrite = write switch
            {
                Write.Replace => true,
                Write.AfterCopy => TryKeepCopy(FileOf(connectionId)),
                _ => false,
            };
            var entries = stored.ToList();
            if (entries.Count > 0 && Same(entries[0].Sql, entry.Sql))
            {
                entries.RemoveAt(0);
            }

            entries.Insert(0, entry);
            if (entries.Count > MaxEntries)
            {
                entries.RemoveRange(MaxEntries, entries.Count - MaxEntries);
            }

            if (!canWrite)
            {
                return entries;
            }

            await AtomicJsonFile.WriteAsync(FileOf(connectionId), new HistoryFile(FileVersion, entries), JsonFiles.Options, cancellationToken);
            return entries;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Removes a connection's history (the connection was deleted).</summary>
    public void Delete(Guid connectionId)
    {
        try
        {
            File.Delete(FileOf(connectionId));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Whether a new history may replace the file.</summary>
    private enum Write
    {
        Replace,

        /// <summary>A broken history is no reason to stop working: kept as a copy, then a new file.</summary>
        AfterCopy,

        /// <summary>Locked for the moment (virus scanner, backup) – the next entry tries again – or a newer FerretSharp's history.</summary>
        No,
    }

    private async Task<(IReadOnlyList<SqlHistoryEntry> Entries, Write Write)> ReadAsync(Guid connectionId, CancellationToken cancellationToken)
    {
        var read = await VersionedJsonFile.ReadAsync<HistoryFile>(FileOf(connectionId), FileVersion, JsonFiles.Options, cancellationToken);
        return read.State switch
        {
            JsonFileState.Missing => ([], Write.Replace),
            JsonFileState.Ok when read.Version == FileVersion => ((read.Document!.Entries ?? []).OfType<SqlHistoryEntry>().ToList(), Write.Replace), // null by hand (A6)
            JsonFileState.Ok or JsonFileState.Empty or JsonFileState.Broken => ([], Write.AfterCopy), // no version or an unknown one, null, no JSON
            _ => ([], Write.No),
        };
    }

    private static bool TryKeepCopy(string path)
    {
        try
        {
            VersionedJsonFile.KeepCopy(path, DateTimeOffset.Now);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false; // without a copy the file stays; the entry is only shown
        }
    }

    private static bool Same(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.Ordinal);

    private string FileOf(Guid connectionId) => Path.Combine(Directory, connectionId.ToString("D") + ".json");

    private sealed record HistoryFile(int Version, IReadOnlyList<SqlHistoryEntry?>? Entries);
}
