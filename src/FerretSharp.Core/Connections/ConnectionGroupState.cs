using FerretSharp.Core.IO;

namespace FerretSharp.Core.Connections;

/// <summary>
/// The order of the connection groups and which are collapsed (WP-66), shared by the switcher and the connections page:
/// state, not configuration – not exported. Groups are named as the lists show them (<see cref="ConnectionSearch.NormalizeGroup"/>,
/// case ignored); the collapsed entry of connections without a group is the empty name. A broken or missing file just
/// means "all expanded, alphabetical"; one of a newer FerretSharp is kept as it is, changes of this session stay in
/// memory (like recent.json).
/// </summary>
public sealed class ConnectionGroupState(string filePath)
{
    public const int CurrentVersion = 1;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyList<string> _order = [];
    private IReadOnlyList<string> _collapsed = [];
    private bool _keepFile;

    /// <summary>Raised after the order or a collapsed group changed (on the caller's thread).</summary>
    public event Action? Changed;

    /// <summary>The groups in the order the user gave them; groups not in it come alphabetically after them.</summary>
    public IReadOnlyList<string> Order => _order;

    /// <summary>Whether the group is collapsed; null for connections without a group.</summary>
    public bool IsCollapsed(string? group) => _collapsed.Any(c => SameKey(c, Key(group)));

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var read = await VersionedJsonFile.ReadAsync<Document>(filePath, CurrentVersion, JsonFiles.Options, cancellationToken);
            _keepFile = read.State == JsonFileState.Newer;
            // Entries missing, null or twice (edited by hand): what is readable counts, the first place of a group wins.
            _order = (read.Document?.Order ?? []).Select(ConnectionSearch.NormalizeGroup).OfType<string>()
                .Distinct(ConnectionSearch.GroupNameComparer)
                .ToList();
            _collapsed = (read.Document?.Collapsed ?? []).OfType<string>().Select(Key)
                .Distinct(ConnectionSearch.GroupNameComparer)
                .ToList();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <returns>False if the file could not be written: the change applies all the same, until quitting (as work clones).</returns>
    public Task<bool> SetCollapsedAsync(string? group, bool collapsed, CancellationToken cancellationToken) =>
        UpdateAsync(
            _order,
            collapsed
                ? [.. _collapsed.Where(c => !SameKey(c, Key(group))), Key(group)]
                : [.. _collapsed.Where(c => !SameKey(c, Key(group)))],
            cancellationToken);

    /// <summary>
    /// The groups in this order from now on – all groups the list shows, so a group moved behind one without a place
    /// stays there. Groups not in it lose their place.
    /// </summary>
    /// <inheritdoc cref="SetCollapsedAsync" path="/returns"/>
    public Task<bool> SetOrderAsync(IReadOnlyList<string> order, CancellationToken cancellationToken) =>
        UpdateAsync(
            [.. order.Select(ConnectionSearch.NormalizeGroup).OfType<string>().Distinct(ConnectionSearch.GroupNameComparer)],
            _collapsed,
            cancellationToken);

    /// <summary>
    /// <paramref name="shown"/> (the named groups as listed) with <paramref name="group"/> moved before
    /// <paramref name="before"/>; null moves it to the end. Unknown names leave the list as it is.
    /// </summary>
    public static IReadOnlyList<string> Move(IReadOnlyList<string> shown, string group, string? before)
    {
        if (!shown.Any(s => ConnectionSearch.SameGroup(s, group)) || ConnectionSearch.SameGroup(group, before))
        {
            return shown;
        }

        var moved = shown.First(s => ConnectionSearch.SameGroup(s, group));
        var rest = shown.Where(s => !ConnectionSearch.SameGroup(s, group)).ToList();
        var index = before is null ? rest.Count : rest.FindIndex(s => ConnectionSearch.SameGroup(s, before));
        if (index < 0)
        {
            return shown;
        }

        rest.Insert(index, moved);
        return rest;
    }

    /// <summary>
    /// A group was renamed (<see cref="ConnectionGroupRename"/>): the new name takes its place and whether it is collapsed.
    /// Merged into a group that exists, the target keeps its place and is expanded – the moved connections are seen.
    /// </summary>
    /// <inheritdoc cref="SetCollapsedAsync" path="/returns"/>
    public Task<bool> RenameAsync(string from, string to, bool merges, CancellationToken cancellationToken)
    {
        var target = ConnectionSearch.NormalizeGroup(to) ?? throw new ArgumentException("A group needs a name.", nameof(to));
        if (merges)
        {
            return UpdateAsync(
                [.. _order.Where(o => !ConnectionSearch.SameGroup(o, from))],
                [.. _collapsed.Where(c => !ConnectionSearch.SameGroup(c, from) && !ConnectionSearch.SameGroup(c, target))],
                cancellationToken);
        }

        return UpdateAsync(
            [.. _order.Select(o => ConnectionSearch.SameGroup(o, from) ? target : o)],
            [.. _collapsed.Select(c => ConnectionSearch.SameGroup(c, from) ? target : c)],
            cancellationToken);
    }

    private async Task<bool> UpdateAsync(IReadOnlyList<string> order, IReadOnlyList<string> collapsed, CancellationToken cancellationToken)
    {
        bool saved;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            saved = true;
            if (!_keepFile)
            {
                try
                {
                    await AtomicJsonFile.WriteAsync(filePath, new Document(CurrentVersion, order, collapsed), JsonFiles.Options, cancellationToken);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    saved = false;
                }
            }

            _order = order;
            _collapsed = collapsed;
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke();
        return saved;
    }

    /// <summary>The entry of a group in <see cref="Document.Collapsed"/>: its name as the lists show it, "" for none.</summary>
    private static string Key(string? group) => ConnectionSearch.NormalizeGroup(group) ?? "";

    private static bool SameKey(string a, string b) => ConnectionSearch.GroupNameComparer.Equals(a, b);

    private sealed record Document(int Version, IReadOnlyList<string?>? Order, IReadOnlyList<string?>? Collapsed);
}
