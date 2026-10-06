using System.Text.Json;
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
    /// FerretSharp is not overwritten – the entry is then only returned, not stored.
    /// </returns>
    public async Task<IReadOnlyList<SqlHistoryEntry>> AddAsync(Guid connectionId, SqlHistoryEntry entry, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var (stored, canWrite) = await ReadAsync(connectionId, cancellationToken);
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

            await AtomicJsonFile.WriteAsync(FileOf(connectionId), new HistoryFile(FileVersion, entries), Options, cancellationToken);
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

    /// <returns>The entries, and whether the file may be replaced by a new one.</returns>
    private async Task<(IReadOnlyList<SqlHistoryEntry> Entries, bool CanWrite)> ReadAsync(Guid connectionId, CancellationToken cancellationToken)
    {
        var path = FileOf(connectionId);
        if (!File.Exists(path))
        {
            return ([], true);
        }

        try
        {
            await using var stream = File.OpenRead(path);
            var file = await JsonSerializer.DeserializeAsync<HistoryFile>(stream, Options, cancellationToken);
            return file switch
            {
                { Version: FileVersion } => (file.Entries, true),
                { Version: > FileVersion } => ([], false), // a newer FerretSharp's history stays as it is
                _ => ([], true),
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ([], false); // locked for the moment (virus scanner, backup): try again with the next entry
        }
        catch (JsonException)
        {
            return ([], true); // a broken history is no reason to stop working; the next entry writes a new file
        }
    }

    private static bool Same(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.Ordinal);

    private string FileOf(Guid connectionId) => Path.Combine(Directory, connectionId.ToString("D") + ".json");

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private sealed record HistoryFile(int Version, List<SqlHistoryEntry> Entries);
}
