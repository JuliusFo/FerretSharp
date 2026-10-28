using FerretSharp.Core.Connections;
using FerretSharp.Core.IO;

namespace FerretSharp.Core.ClrModel;

/// <summary>
/// The active work clone of each connection group and the folders chosen recently (WP-40, ADR 0030): state, not
/// configuration – switched from the status bar or the command palette, never in the connection dialog or the settings.
/// Connections without a group share one entry. Local paths: not exported. A broken or missing file just means "none
/// chosen"; one of a newer FerretSharp is kept as it is, choices of this session stay in memory (like recent.json).
/// </summary>
public sealed class WorkClones(string filePath, TimeProvider? timeProvider = null)
{
    public const int CurrentVersion = 1;

    public const int MaxRecent = 20;


    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyList<Active> _active = [];
    private IReadOnlyList<RecentFolder> _recent = [];
    private bool _keepFile;

    /// <summary>Raised after the active clone of a group changed (on the caller's thread).</summary>
    public event Action? Changed;

    /// <summary>The active work clone of the group (trimmed, case ignored; null = no group); null if none is chosen.</summary>
    public string? ActiveFor(string? group)
    {
        var key = ConnectionSearch.NormalizeGroup(group);
        return _active.FirstOrDefault(a => ConnectionSearch.SameGroupKey(a.Group, key))?.Path;
    }

    /// <summary>Recently chosen folders that still exist, most recent first.</summary>
    public IReadOnlyList<string> Recent => _recent.Select(r => r.Path).Where(Directory.Exists).ToList();

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var read = await VersionedJsonFile.ReadAsync<Document>(filePath, CurrentVersion, JsonFiles.Options, cancellationToken);
            _keepFile = read.State == JsonFileState.Newer;
            // Entries missing, null or twice (edited by hand): what is readable counts, the first entry of a group wins.
            _active = (read.Document?.Active ?? []).OfType<Active>()
                .Where(a => !string.IsNullOrWhiteSpace(a.Path))
                .Select(a => a with { Group = ConnectionSearch.NormalizeGroup(a.Group) })
                .DistinctBy(a => a.Group ?? "", ConnectionSearch.GroupNameComparer)
                .ToList();
            _recent = (read.Document?.Recent ?? []).OfType<RecentFolder>()
                .Where(r => !string.IsNullOrWhiteSpace(r.Path))
                .DistinctBy(r => r.Path, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(r => r.LastUsed)
                .Take(MaxRecent)
                .ToList();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Makes <paramref name="path"/> the group's work clone and puts it first in the recent folders; null = none (the
    /// linked clone is used).
    /// </summary>
    /// <returns>
    /// False if the file could not be written (a backup or sync held it): the choice applies all the same, until quitting –
    /// as <c>recent.json</c> (review 3.44.0, A22: it ended in "unexpected error").
    /// </returns>
    public async Task<bool> SetActiveAsync(string? group, string? path, CancellationToken cancellationToken)
    {
        bool saved;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var key = ConnectionSearch.NormalizeGroup(group);
            var full = path is null ? null : Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            var active = _active
                .Where(a => !ConnectionSearch.SameGroupKey(a.Group, key))
                .ToList();
            if (full is not null)
            {
                active.Add(new Active(key, full));
            }

            var recent = full is null
                ? _recent
                : _recent.Where(r => !string.Equals(r.Path, full, StringComparison.OrdinalIgnoreCase))
                    .Prepend(new RecentFolder(full, _time.GetUtcNow()))
                    .Take(MaxRecent)
                    .ToList();

            saved = await WriteAsync(active, recent, cancellationToken);
            _active = active;
            _recent = recent;
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke();
        return saved;
    }

    /// <summary>
    /// A connection group was renamed (WP-66, <see cref="ConnectionGroupRename"/>): its work clone goes with it. If the
    /// target has one already (a merge), the target's stays.
    /// </summary>
    /// <returns>False if the file could not be written; as <see cref="SetActiveAsync"/>.</returns>
    public async Task<bool> RenameGroupAsync(string from, string to, CancellationToken cancellationToken)
    {
        bool saved;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var source = _active.FirstOrDefault(a => ConnectionSearch.SameGroup(a.Group, from));
            if (source is null)
            {
                return true;
            }

            var target = ConnectionSearch.NormalizeGroup(to);
            var keepTarget = !ConnectionSearch.SameGroup(from, to) && _active.Any(a => ConnectionSearch.SameGroup(a.Group, target));
            var active = _active.Where(a => a != source).ToList();
            if (!keepTarget)
            {
                active.Add(source with { Group = target });
            }

            saved = await WriteAsync(active, _recent, cancellationToken);
            _active = active;
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke();
        return saved;
    }

    private async Task<bool> WriteAsync(IReadOnlyList<Active> active, IReadOnlyList<RecentFolder> recent, CancellationToken cancellationToken)
    {
        if (_keepFile)
        {
            return true;
        }

        try
        {
            await AtomicJsonFile.WriteAsync(filePath, new Document(CurrentVersion, active, recent), JsonFiles.Options, cancellationToken);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <param name="Group">The connection group as the lists show it (trimmed); null for connections without one.</param>
    private sealed record Active(string? Group, string Path);

    private sealed record RecentFolder(string Path, DateTimeOffset LastUsed);

    private sealed record Document(int Version, IReadOnlyList<Active?>? Active, IReadOnlyList<RecentFolder?>? Recent);
}
