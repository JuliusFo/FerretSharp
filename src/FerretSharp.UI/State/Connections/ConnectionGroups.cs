using FerretSharp.Core.ClrModel;
using FerretSharp.Core.Connections;
using FerretSharp.Core.Settings;
using FerretSharp.UI.Resources;

namespace FerretSharp.UI.State;

/// <summary>
/// The connection groups as the switcher and the connections page show them (WP-66): in the user's order, collapsed or
/// not – one state for both – and the actions on a group (rename, merge, move) or on a connection's group. Keeps the keys
/// of deleted connections out of the settings. Not tied to a connection – injected.
/// </summary>
public sealed class ConnectionGroups : IDisposable
{
    private readonly ConnectionManager _connections;
    private readonly ConnectionGroupState _state;
    private readonly AppSettingsService _settings;
    private readonly WorkClones _clones;
    private readonly ShortcutService _shortcuts;

    public ConnectionGroups(
        ConnectionManager connections, ConnectionGroupState state, AppSettingsService settings, WorkClones clones, ShortcutService shortcuts)
    {
        (_connections, _state, _settings, _clones, _shortcuts) = (connections, state, settings, clones, shortcuts);
        _state.Changed += OnStateChanged;
        _connections.Changed += OnConnectionsChanged;
    }

    /// <summary>The order or a collapsed group changed (on the caller's thread).</summary>
    public event Action? Changed;

    public Task LoadAsync(CancellationToken cancellationToken) => _state.LoadAsync(cancellationToken);

    /// <summary>The groups with their connections in the user's order (<see cref="ConnectionSearch.Group"/>).</summary>
    public IReadOnlyList<ConnectionGroup> Of(IEnumerable<ConnectionProfile> profiles, string? query = null) =>
        ConnectionSearch.Group(profiles, query, _state.Order);

    /// <summary>The groups in the order the user gave them (<see cref="ConnectionSearch.Group"/> takes it).</summary>
    public IReadOnlyList<string> Order => _state.Order;

    /// <summary>All saved connections grouped.</summary>
    public IReadOnlyList<ConnectionGroup> All(string? query = null) => Of(_connections.Profiles, query);

    /// <summary>The named groups in the order shown, for chips and moving.</summary>
    public IReadOnlyList<string> Names => NamesIn(_state.Order);

    /// <summary>Whether the group is collapsed; null for connections without a group. A search shows all groups open.</summary>
    public bool IsCollapsed(string? group) => _state.IsCollapsed(group);

    /// <returns>False if the state could not be saved (it applies until quitting).</returns>
    public Task<bool> ToggleAsync(string? group) => _state.ToggleCollapsedAsync(group, CancellationToken.None);

    /// <summary>The group the one moves before with "up"; null if it is first (or no named group).</summary>
    public string? Previous(string group) => Previous(Names, group);

    /// <summary>Whether "down" can move the group: a named group follows it.</summary>
    public bool HasNext(string group) => Names.SkipWhile(n => !ConnectionSearch.SameGroup(n, group)).Skip(1).Any();

    /// <summary>Moves the group before <paramref name="before"/>; null: after all named groups.</summary>
    /// <inheritdoc cref="ToggleAsync" path="/returns"/>
    public Task<bool> MoveAsync(string group, string? before) =>
        _state.SetOrderAsync(order => ConnectionGroupState.Move(NamesIn(order), group, before), CancellationToken.None);

    /// <summary>Worked out on the order when it is its turn, like the other moves (review of WP-66).</summary>
    /// <inheritdoc cref="ToggleAsync" path="/returns"/>
    public Task<bool> MoveUpAsync(string group) =>
        _state.SetOrderAsync(
            order => NamesIn(order) is var names && Previous(names, group) is { } previous ? ConnectionGroupState.Move(names, group, previous) : order,
            CancellationToken.None);

    /// <inheritdoc cref="MoveUpAsync"/>
    public Task<bool> MoveDownAsync(string group) =>
        _state.SetOrderAsync(
            order =>
            {
                var names = NamesIn(order);
                var after = names.SkipWhile(n => !ConnectionSearch.SameGroup(n, group)).Skip(1).ToList();
                return after.Count == 0 ? order : ConnectionGroupState.Move(names, group, after.Skip(1).FirstOrDefault());
            },
            CancellationToken.None);

    private IReadOnlyList<string> NamesIn(IReadOnlyList<string> order) =>
        [.. ConnectionSearch.Group(_connections.Profiles, order: order).Select(g => g.Name).OfType<string>()];

    private static string? Previous(IReadOnlyList<string> names, string group) =>
        names.TakeWhile(n => !ConnectionSearch.SameGroup(n, group)).LastOrDefault();

    /// <summary>What renaming <paramref name="from"/> to <paramref name="to"/> would do; null for a name that is no group name.</summary>
    public ConnectionGroupRename? PlanRename(string from, string to) =>
        ConnectionSearch.NormalizeGroup(from) is null || ConnectionSearch.NormalizeGroup(to) is null
            ? null
            : ConnectionGroupRename.Plan(_connections.Profiles, from, to, _settings.Current.AuditTemplateGroups);

    /// <summary>
    /// The work clone of the renamed group when the target name has its own – a group merged into, or one left by an earlier
    /// group of that name, which <see cref="WorkClones.RenameGroupAsync"/> keeps like the audit template (review of WP-66: it
    /// was dropped without a word); null if none is lost. The folder stays among the recent ones.
    /// </summary>
    public string? DroppedWorkClone(ConnectionGroupRename rename) =>
        !rename.IsUnchanged && !ConnectionSearch.SameGroup(rename.From, rename.To) && _clones.ActiveFor(rename.From) is { } source && _clones.ActiveFor(rename.To) is { } target
                      && !string.Equals(source, target, StringComparison.OrdinalIgnoreCase)
            ? source
            : null;

    /// <summary>
    /// Renames (or merges) the group: the connections in one write first – that is the rename –, then what is kept per group
    /// by name. Those that could not be saved apply until quitting and are named in the result.
    /// </summary>
    /// <returns>What could not be saved; empty if all was.</returns>
    /// <exception cref="IOException">The connections could not be saved: nothing changed.</exception>
    public async Task<IReadOnlyList<string>> RenameAsync(ConnectionGroupRename rename)
    {
        await _connections.SaveManyAsync(rename.Profiles, CancellationToken.None);

        List<string> notSaved = [];
        if (!rename.AuditTemplateGroups.Equals(_settings.Current.AuditTemplateGroups))
        {
            try
            {
                await _settings.UpdateAsync(s => s with { AuditTemplateGroups = rename.AuditTemplateGroups }, CancellationToken.None);
            }
            catch (IOException ex)
            {
                notSaved.Add(ex.Message);
            }
        }

        if (!await _clones.RenameGroupAsync(rename.From, rename.To, CancellationToken.None))
        {
            notSaved.Add(ConnectionViewText.Group_WorkCloneNotSaved);
        }

        if (!await _state.RenameAsync(rename.From, rename.To, rename.Merges, CancellationToken.None))
        {
            notSaved.Add(ConnectionViewText.Group_StateNotSaved);
        }

        return notSaved;
    }

    /// <summary>
    /// Puts the connection into the group (null or blank: none); nothing is written if it is there already or the connection
    /// is gone. The saved profile is read again by id: the one of an open menu may be older than a save in the background
    /// (review of WP-66). A group no connection had starts expanded and without a place an earlier group of its name left.
    /// </summary>
    /// <exception cref="IOException">The connections could not be saved.</exception>
    public async Task MoveConnectionAsync(ConnectionProfile profile, string? group)
    {
        var target = ConnectionSearch.NormalizeGroup(group);
        if (_connections.Profiles.FirstOrDefault(p => p.Id == profile.Id) is not { } saved || saved.Group == target)
        {
            return;
        }

        await _connections.SaveAsync(saved with { Group = target }, null, CancellationToken.None);
        await Cleanup; // a new group starts clean (OnConnectionsChanged)
    }

    /// <summary>
    /// What the last change of the list set off in the background – keys of deleted connections, leftovers of new groups;
    /// tests wait for it instead of a fixed time (review of WP-66).
    /// </summary>
    internal Task Cleanup { get; private set; } = Task.CompletedTask;

    /// <summary>The named groups the list had at the last change; null before the first one.</summary>
    private HashSet<string>? _known;

    private void OnStateChanged() => Changed?.Invoke();

    /// <summary>
    /// After every change of the list: a group that appears – from the connection dialog, an import, a chip or dragging –
    /// starts expanded and without a place an earlier group of its name left (review of WP-66: the new connection
    /// disappeared into a collapsed group), and a deleted connection's key leaves the settings.
    /// </summary>
    private void OnConnectionsChanged()
    {
        // An unreadable file lists no connections – that does not mean all were deleted. Neither does a missing one (moved
        // away by a sync for a moment, review of WP-66): with no connection left, keys and known groups stay until there is
        // one again.
        if (_connections.LoadError is not null || _connections.Profiles.Count == 0)
        {
            return;
        }

        var groups = _connections.Profiles.Select(p => ConnectionSearch.NormalizeGroup(p.Group)).OfType<string>()
            .ToHashSet(ConnectionSearch.GroupNameComparer);
        // The first list (loading at start) only tells which groups there are: nothing of them is new.
        var started = _known is null ? [] : groups.Where(g => !_known.Contains(g)).ToList();
        _known = groups;
        Cleanup = CleanUpAsync(_connections.Profiles.Select(p => p.Id).ToHashSet(), started);
    }

    private async Task CleanUpAsync(IReadOnlySet<Guid> existing, IReadOnlyList<string> started)
    {
        foreach (var group in started)
        {
            await _state.ForgetAsync(group, CancellationToken.None); // a file it cannot write: in effect for the session
        }

        try
        {
            await _shortcuts.ForgetConnectionsExceptAsync(existing);
        }
        catch (IOException)
        {
            // In effect for this session; tried again with the next change of the list.
        }
    }

    public void Dispose()
    {
        _state.Changed -= OnStateChanged;
        _connections.Changed -= OnConnectionsChanged;
    }
}
