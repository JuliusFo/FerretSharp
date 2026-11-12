using FerretSharp.Core.Connections;
using FerretSharp.Core.IO;

namespace FerretSharp.Core.Scripts;

/// <summary>
/// Which scripts were last started from a cell (WP-84, decision of the user 2026-11-11): the cell menu shows them first.
/// The file holds only folder kind, group, path and time – never a value. The values typed into the dialog for a script's
/// other parameters stay in memory until FerretSharp ends (decision of the user: no values of Prod in a file).
/// A broken or missing file means "none used"; one of a newer FerretSharp is kept as it is, uses of this session stay in
/// memory (like <c>work-clones.json</c>).
/// </summary>
public sealed class ScriptUsage(string filePath, TimeProvider? timeProvider = null)
{
    public const int CurrentVersion = 1;

    /// <summary>At most this many scripts are remembered; the oldest go first.</summary>
    public const int MaxEntries = 200;

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _valuesLock = new();
    private readonly Dictionary<string, Dictionary<string, string>> _values = new(StringComparer.Ordinal);
    private IReadOnlyList<Use> _uses = [];
    private bool _keepFile;

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var read = await VersionedJsonFile.ReadAsync<Document>(filePath, CurrentVersion, JsonFiles.Options, cancellationToken);
            _keepFile = read.State == JsonFileState.Newer;
            // Entries missing, null or twice (edited by hand): what is readable counts, the latest use of a script wins.
            _uses = (read.Document?.Uses ?? []).OfType<Use>()
                .Where(u => !string.IsNullOrWhiteSpace(u.Path) && Enum.IsDefined(u.Root))
                .OrderByDescending(u => u.LastUsed)
                .DistinctBy(u => Key(u.Root, u.Group, u.Path), StringComparer.Ordinal)
                .Take(MaxEntries)
                .ToList();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// <paramref name="matches"/> with the scripts used from a cell first, the latest first; the others keep their order.
    /// </summary>
    public IReadOnlyList<ScriptMatch> Ordered(IReadOnlyList<ScriptMatch> matches)
    {
        ArgumentNullException.ThrowIfNull(matches);
        var uses = _uses;
        var last = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        foreach (var use in uses)
        {
            last.TryAdd(Key(use.Root, use.Group, use.Path), use.LastUsed);
        }

        return
        [
            .. matches.Select((m, i) => (Match: m, Index: i, Used: last.TryGetValue(Key(m.Script), out var t) ? t : (DateTimeOffset?)null))
                .OrderBy(x => x.Used is null)
                .ThenByDescending(x => x.Used)
                .ThenBy(x => x.Index)
                .Select(x => x.Match),
        ];
    }

    /// <summary>Remembers that <paramref name="script"/> was started now.</summary>
    /// <returns>False if the file could not be written: the use counts all the same, until quitting.</returns>
    public async Task<bool> UsedAsync(ScriptEntry script, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(script);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var key = Key(script);
            var uses = _uses.Where(u => Key(u.Root, u.Group, u.Path) != key)
                .Prepend(new Use(script.Root.Kind, script.Root.Group, script.Path, _time.GetUtcNow()))
                .Take(MaxEntries)
                .ToList();
            _uses = uses;
            if (_keepFile)
            {
                return true;
            }

            try
            {
                await AtomicJsonFile.WriteAsync(filePath, new Document(CurrentVersion, uses), JsonFiles.Options, cancellationToken);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The values typed for the parameters of <paramref name="script"/> this session (by name, case ignored).</summary>
    public IReadOnlyDictionary<string, string> ValuesOf(ScriptEntry script)
    {
        ArgumentNullException.ThrowIfNull(script);
        lock (_valuesLock)
        {
            return _values.TryGetValue(Key(script), out var values)
                ? new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>Keeps <paramref name="values"/> (parameter name → text) for the next start of the script; only in memory.</summary>
    public void Remember(ScriptEntry script, IEnumerable<KeyValuePair<string, string>> values)
    {
        ArgumentNullException.ThrowIfNull(script);
        ArgumentNullException.ThrowIfNull(values);
        lock (_valuesLock)
        {
            if (!_values.TryGetValue(Key(script), out var kept))
            {
                kept = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                _values[Key(script)] = kept;
            }

            foreach (var (name, value) in values)
            {
                kept[name] = value;
            }
        }
    }

    private static string Key(ScriptEntry script) => Key(script.Root.Kind, script.Root.Group, script.Path);

    /// <summary>A script by folder kind, group (case ignored, as the settings compare groups) and path (case ignored, as Windows).</summary>
    private static string Key(ScriptRootKind root, string? group, string path) =>
        string.Join('\u0001', root.ToString(), root == ScriptRootKind.Group ? ConnectionSearch.NormalizeGroup(group)?.ToUpperInvariant() ?? "" : "", path.ToUpperInvariant());

    /// <param name="Group">The group of a <see cref="ScriptRootKind.Group"/> folder as the settings name it.</param>
    private sealed record Use(ScriptRootKind Root, string? Group, string Path, DateTimeOffset LastUsed);

    private sealed record Document(int Version, IReadOnlyList<Use?>? Uses);
}
