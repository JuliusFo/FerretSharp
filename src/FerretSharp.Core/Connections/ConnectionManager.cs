namespace FerretSharp.Core.Connections;

/// <summary>
/// In-memory list of saved connections, backed by <see cref="IConnectionStore"/> (profiles)
/// and <see cref="ISecretStore"/> (passwords).
/// </summary>
public sealed class ConnectionManager(IConnectionStore store, ISecretStore secrets)
{
    private List<ConnectionProfile> _profiles = [];

    /// <summary>Raised after the list changed (load, save, delete).</summary>
    public event Action? Changed;

    public IReadOnlyList<ConnectionProfile> Profiles => _profiles;

    /// <summary>Set when the connections file could not be read; the list is empty in that case.</summary>
    public string? LoadError { get; private set; }

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            _profiles = Sort(await store.LoadAsync(cancellationToken));
            LoadError = null;
        }
        catch (ConnectionStoreException ex)
        {
            _profiles = [];
            LoadError = ex.Message;
        }

        Changed?.Invoke();
    }

    public bool HasPassword(Guid profileId) => secrets.GetPassword(profileId) is not null;

    public string? GetPassword(Guid profileId) => secrets.GetPassword(profileId);

    /// <summary>Adds or replaces the profile. <paramref name="newPassword"/> null keeps the stored password.</summary>
    public async Task SaveAsync(ConnectionProfile profile, string? newPassword, CancellationToken cancellationToken)
    {
        var errors = ConnectionProfileValidator.Validate(profile);
        if (errors.Count > 0)
        {
            throw new ArgumentException(string.Join(" ", errors.Values), nameof(profile));
        }

        var updated = _profiles.Where(p => p.Id != profile.Id).Append(profile).ToList();
        await store.SaveAsync(updated, cancellationToken);

        if (newPassword is not null)
        {
            secrets.SetPassword(profile.Id, newPassword);
        }

        _profiles = Sort(updated);
        Changed?.Invoke();
    }

    public async Task DeleteAsync(Guid profileId, CancellationToken cancellationToken)
    {
        var updated = _profiles.Where(p => p.Id != profileId).ToList();
        await store.SaveAsync(updated, cancellationToken);
        secrets.DeletePassword(profileId);

        _profiles = updated;
        Changed?.Invoke();
    }

    private static List<ConnectionProfile> Sort(IEnumerable<ConnectionProfile> profiles) =>
        profiles.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
}
