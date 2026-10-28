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
    public IReadOnlyList<string> Names => [.. All().Select(g => g.Name).OfType<string>()];

    /// <summary>Whether the group is collapsed; null for connections without a group. A search shows all groups open.</summary>
    public bool IsCollapsed(string? group) => _state.IsCollapsed(group);

    /// <returns>False if the state could not be saved (it applies until quitting).</returns>
    public Task<bool> ToggleAsync(string? group) => _state.SetCollapsedAsync(group, !_state.IsCollapsed(group), CancellationToken.None);

    /// <summary>The group the one moves before with "up"; null if it is first (or no named group).</summary>
    public string? Previous(string group) => Names.TakeWhile(n => !ConnectionSearch.SameGroup(n, group)).LastOrDefault();

    /// <summary>Whether "down" can move the group: a named group follows it.</summary>
    public bool HasNext(string group) => Names.SkipWhile(n => !ConnectionSearch.SameGroup(n, group)).Skip(1).Any();

    /// <summary>Moves the group before <paramref name="before"/>; null: after all named groups.</summary>
    /// <inheritdoc cref="ToggleAsync" path="/returns"/>
    public Task<bool> MoveAsync(string group, string? before) =>
        _state.SetOrderAsync(ConnectionGroupState.Move(Names, group, before), CancellationToken.None);

    /// <inheritdoc cref="ToggleAsync" path="/returns"/>
    public Task<bool> MoveUpAsync(string group) =>
        Previous(group) is { } previous ? MoveAsync(group, previous) : Task.FromResult(true);

    /// <inheritdoc cref="ToggleAsync" path="/returns"/>
    public Task<bool> MoveDownAsync(string group)
    {
        var after = Names.SkipWhile(n => !ConnectionSearch.SameGroup(n, group)).Skip(2).FirstOrDefault();
        return HasNext(group) ? MoveAsync(group, after) : Task.FromResult(true);
    }

    /// <summary>What renaming <paramref name="from"/> to <paramref name="to"/> would do; null for a name that is no group name.</summary>
    public ConnectionGroupRename? PlanRename(string from, string to) =>
        ConnectionSearch.NormalizeGroup(from) is null || ConnectionSearch.NormalizeGroup(to) is null
            ? null
            : ConnectionGroupRename.Plan(_connections.Profiles, from, to, _settings.Current.AuditTemplateGroups);

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

    /// <summary>Puts the connection into the group (null or blank: none); nothing is written if it is there already.</summary>
    /// <exception cref="IOException">The connections could not be saved.</exception>
    public Task MoveConnectionAsync(ConnectionProfile profile, string? group)
    {
        var target = ConnectionSearch.NormalizeGroup(group);
        return profile.Group == target
            ? Task.CompletedTask
            : _connections.SaveAsync(profile with { Group = target }, null, CancellationToken.None);
    }

    private void OnStateChanged() => Changed?.Invoke();

    /// <summary>A deleted connection's key would stay in the settings: dropped when the list no longer has it.</summary>
    private void OnConnectionsChanged()
    {
        if (_connections.LoadError is not null)
        {
            return; // an unreadable file lists no connections – that does not mean all were deleted
        }

        _ = ForgetDeletedKeysAsync(_connections.Profiles.Select(p => p.Id).ToHashSet());
    }

    private async Task ForgetDeletedKeysAsync(IReadOnlySet<Guid> existing)
    {
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
