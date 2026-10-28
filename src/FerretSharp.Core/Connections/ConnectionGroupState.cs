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

        // The lists rendered with the connections before this was read (E2E of WP-66: after a restart all groups were
        // open and alphabetical until something else rendered them again).
        Changed?.Invoke();
    }

    /// <returns>False if the file could not be written: the change applies all the same, until quitting (as work clones).</returns>
    public Task<bool> SetCollapsedAsync(string? group, bool collapsed, CancellationToken cancellationToken) =>
        UpdateAsync(state => state with { Collapsed = Collapse(state.Collapsed, group, collapsed) }, cancellationToken);

    /// <summary>Collapses an open group or opens a collapsed one, decided on the state when it is its turn.</summary>
    /// <inheritdoc cref="SetCollapsedAsync" path="/returns"/>
    public Task<bool> ToggleCollapsedAsync(string? group, CancellationToken cancellationToken) =>
        UpdateAsync(
            state => state with { Collapsed = Collapse(state.Collapsed, group, !state.Collapsed.Any(c => SameKey(c, Key(group)))) },
            cancellationToken);

    /// <summary>
    /// The groups in this order from now on – all groups the list shows, so a group moved behind one without a place
    /// stays there. Groups not in it lose their place.
    /// </summary>
    /// <inheritdoc cref="SetCollapsedAsync" path="/returns"/>
    public Task<bool> SetOrderAsync(IReadOnlyList<string> order, CancellationToken cancellationToken) =>
        SetOrderAsync(_ => order, cancellationToken);

    /// <summary>
    /// The order <paramref name="reorder"/> makes of the current one, worked out when it is its turn: a move right after
    /// another starts from where that one put the groups (review of WP-66).
    /// </summary>
    /// <inheritdoc cref="SetCollapsedAsync" path="/returns"/>
    public Task<bool> SetOrderAsync(Func<IReadOnlyList<string>, IReadOnlyList<string>> reorder, CancellationToken cancellationToken) =>
        UpdateAsync(
            state => state with
            {
                Order = [.. reorder(state.Order).Select(ConnectionSearch.NormalizeGroup).OfType<string>().Distinct(ConnectionSearch.GroupNameComparer)],
            },
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
    /// Merged into a group that exists, the target keeps its place and is expanded – the moved connections are seen. What
    /// a group of the new name without connections left behind is dropped (review of WP-66).
    /// </summary>
    /// <inheritdoc cref="SetCollapsedAsync" path="/returns"/>
    public Task<bool> RenameAsync(string from, string to, bool merges, CancellationToken cancellationToken)
    {
        var target = ConnectionSearch.NormalizeGroup(to) ?? throw new ArgumentException("A group needs a name.", nameof(to));
        if (merges)
        {
            return UpdateAsync(
                state => new GroupState(
                    [.. state.Order.Where(o => !ConnectionSearch.SameGroup(o, from))],
                    [.. state.Collapsed.Where(c => !ConnectionSearch.SameGroup(c, from) && !ConnectionSearch.SameGroup(c, target))]),
                cancellationToken);
        }

        var renamesOnly = ConnectionSearch.SameGroup(from, target); // a new spelling: its own entries are the target's
        return UpdateAsync(
            state => new GroupState(
                [.. state.Order.Where(o => renamesOnly || !ConnectionSearch.SameGroup(o, target)).Select(o => ConnectionSearch.SameGroup(o, from) ? target : o)],
                [.. state.Collapsed.Where(c => renamesOnly || !ConnectionSearch.SameGroup(c, target)).Select(c => ConnectionSearch.SameGroup(c, from) ? target : c)]),
            cancellationToken);
    }

    /// <summary>
    /// A group starts out (a connection moved into a name no connection had, review of WP-66): a place or a collapsed
    /// entry left by an earlier group of that name is dropped, else the moved connection disappeared into a collapsed group.
    /// </summary>
    /// <inheritdoc cref="SetCollapsedAsync" path="/returns"/>
    public Task<bool> ForgetAsync(string group, CancellationToken cancellationToken) =>
        _order.Any(o => ConnectionSearch.SameGroup(o, group)) || _collapsed.Any(c => ConnectionSearch.SameGroup(c, group))
            ? UpdateAsync(
                state => new GroupState(
                    [.. state.Order.Where(o => !ConnectionSearch.SameGroup(o, group))],
                    [.. state.Collapsed.Where(c => !ConnectionSearch.SameGroup(c, group))]),
                cancellationToken)
            : Task.FromResult(true);

    private static IReadOnlyList<string> Collapse(IReadOnlyList<string> collapsed, string? group, bool collapse) =>
        collapse
            ? [.. collapsed.Where(c => !SameKey(c, Key(group))), Key(group)]
            : [.. collapsed.Where(c => !SameKey(c, Key(group)))];

    /// <summary>
    /// Works out the new state from the current one inside the gate and keeps it before writing (review of WP-66: two
    /// quick clicks each started from the old state, and the second undid the first).
    /// </summary>
    private async Task<bool> UpdateAsync(Func<GroupState, GroupState> change, CancellationToken cancellationToken)
    {
        bool saved;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var next = change(new GroupState(_order, _collapsed));
            (_order, _collapsed) = (next.Order, next.Collapsed);
            saved = true;
            if (!_keepFile)
            {
                try
                {
                    await AtomicJsonFile.WriteAsync(filePath, new Document(CurrentVersion, next.Order, next.Collapsed), JsonFiles.Options, cancellationToken);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    saved = false;
                }
            }
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke();
        return saved;
    }

    private sealed record GroupState(IReadOnlyList<string> Order, IReadOnlyList<string> Collapsed);

    /// <summary>The entry of a group in <see cref="Document.Collapsed"/>: its name as the lists show it, "" for none.</summary>
    private static string Key(string? group) => ConnectionSearch.NormalizeGroup(group) ?? "";

    private static bool SameKey(string a, string b) => ConnectionSearch.GroupNameComparer.Equals(a, b);

    private sealed record Document(int Version, IReadOnlyList<string?>? Order, IReadOnlyList<string?>? Collapsed);
}
