using FerretSharp.Core.Connections;
using FerretSharp.Core.Connections.Transfer;

namespace FerretSharp.UI.State;

/// <summary>
/// Import and export of connections (WP-35) for the dialogs: where other tools are looked for, reading, saving the import
/// in one step.
/// </summary>
/// <param name="appData">
/// The roaming app data folder DBeaver and SQL Developer are looked for in (<c>%APPDATA%</c>; tests point it elsewhere with
/// <c>--import-appdata=</c>).
/// </param>
public sealed class ConnectionTransfer(ConnectionManager connections, string appData)
{
    public IReadOnlyList<ConnectionProfile> Profiles => connections.Profiles;

    /// <summary>Only looks which folders exist; reads nothing.</summary>
    public IReadOnlyList<FoundInstallation> FindInstallations() => ConnectionInstallations.Find(appData);

    /// <exception cref="ConnectionImportException">Unreadable or of an unknown format.</exception>
    public Task<ImportFile> ReadAsync(FoundInstallation installation, CancellationToken cancellationToken) =>
        Task.Run(() => ConnectionInstallations.ReadAsync(installation, cancellationToken), cancellationToken);

    /// <exception cref="ConnectionImportException">Unreadable, too large or of an unknown format.</exception>
    public Task<ImportFile> ReadFileAsync(string path, CancellationToken cancellationToken) =>
        Task.Run(() => ConnectionImportReader.ReadFileAsync(path, cancellationToken), cancellationToken);

    public ConnectionImportPlan Plan(ImportFile file) => new(file, connections.Profiles);

    /// <returns>How many connections were saved.</returns>
    /// <exception cref="InvalidOperationException">The plan has a <see cref="ConnectionImportPlan.Problem"/>.</exception>
    public async Task<int> ImportAsync(ConnectionImportPlan plan, CancellationToken cancellationToken)
    {
        var profiles = plan.Build();
        await connections.SaveManyAsync(profiles, cancellationToken);
        return profiles.Count;
    }
}

/// <summary>
/// The selection of the export dialog (WP-35): connections in their groups like on the connections page, a check box per
/// group and per connection. Hidden by the search, a connection keeps its selection.
/// </summary>
public sealed class ConnectionExportSelection
{
    private readonly IReadOnlyList<ConnectionProfile> _profiles;
    private readonly HashSet<Guid> _selected;
    private readonly IReadOnlyList<string>? _order;

    /// <param name="request">All connections start selected, or only those of the requested group (or connection).</param>
    /// <param name="order">The user's order of the groups (WP-66), as the connection list shows them.</param>
    public ConnectionExportSelection(IReadOnlyList<ConnectionProfile> profiles, ConnectionExportRequest request, IReadOnlyList<string>? order = null)
    {
        _profiles = profiles;
        _order = order;
        _selected = profiles
            .Where(p => request.Connection is { } only ? p.Id == only : !request.OnlyGroup || SameGroup(p.Group, request.Group))
            .Select(p => p.Id)
            .ToHashSet();
    }

    public string Query { get; set; } = "";

    /// <summary>Keep the paths only valid on this computer (C# project, script folder).</summary>
    public bool IncludeLocalPaths { get; set; }

    public IReadOnlyList<ConnectionGroup> Groups => ConnectionSearch.Group(_profiles, Query, _order);

    public int SelectedCount => _selected.Count;

    /// <summary>In the order of the connections page.</summary>
    public IReadOnlyList<ConnectionProfile> Selected =>
        ConnectionSearch.Group(_profiles, order: _order).SelectMany(g => g.Profiles).Where(p => _selected.Contains(p.Id)).ToList();

    public bool IsSelected(ConnectionProfile profile) => _selected.Contains(profile.Id);

    public void Set(ConnectionProfile profile, bool selected)
    {
        if (selected)
        {
            _selected.Add(profile.Id);
        }
        else
        {
            _selected.Remove(profile.Id);
        }
    }

    /// <summary>How many of the group's connections (all of them, not only the shown ones) are selected.</summary>
    public int SelectedIn(ConnectionGroup group) => AllOf(group).Count(p => _selected.Contains(p.Id));

    public int CountOf(ConnectionGroup group) => AllOf(group).Count();

    /// <summary>The group's check box: selects all of its connections unless all are selected already.</summary>
    public void ToggleGroup(ConnectionGroup group)
    {
        var select = SelectedIn(group) < CountOf(group);
        foreach (var profile in AllOf(group))
        {
            Set(profile, select);
        }
    }

    /// <summary>"All"/"None" act on the shown connections.</summary>
    public void SetShown(bool selected)
    {
        foreach (var profile in Groups.SelectMany(g => g.Profiles))
        {
            Set(profile, selected);
        }
    }

    private IEnumerable<ConnectionProfile> AllOf(ConnectionGroup group) => _profiles.Where(p => SameGroup(p.Group, group.Name));

    private static bool SameGroup(string? a, string? b) =>
        string.Equals(string.IsNullOrWhiteSpace(a) ? null : a.Trim(), string.IsNullOrWhiteSpace(b) ? null : b.Trim(), StringComparison.CurrentCultureIgnoreCase);
}
