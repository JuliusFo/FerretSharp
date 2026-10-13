using FerretSharp.Core.Resources;

namespace FerretSharp.Core.Connections;

/// <summary>
/// In-memory list of saved connections, backed by <see cref="IConnectionStore"/> (profiles)
/// and <see cref="ISecretStore"/> (passwords).
/// </summary>
public sealed class ConnectionManager(IConnectionStore store, ISecretStore secrets)
{
    private List<ConnectionProfile> _profiles = [];

    /// <summary>Passwords entered for this run only (WP-35: "Save in Credential Manager" unchecked).</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, string> _sessionPasswords = new();

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

    public bool HasPassword(Guid profileId) => GetPassword(profileId) is not null;

    /// <summary>The saved password, else one entered for this run – for connecting, never for saving it elsewhere.</summary>
    public string? GetPassword(Guid profileId) => secrets.GetPassword(profileId) ?? _sessionPasswords.GetValueOrDefault(profileId);

    /// <summary>
    /// Only the password saved in the secret store (WP-35 review): the connection dialog shows and copies this one – a
    /// password the user chose not to save must not reach the Credential Manager through a duplicate.
    /// </summary>
    public string? GetStoredPassword(Guid profileId) => secrets.GetPassword(profileId);

    /// <summary>
    /// The password asked for when connecting (WP-35): saved in the secret store, or – <paramref name="remember"/> false –
    /// kept only until FerretSharp ends.
    /// </summary>
    public void SetPassword(Guid profileId, string password, bool remember)
    {
        if (remember)
        {
            secrets.SetPassword(profileId, password);
            _sessionPasswords.TryRemove(profileId, out _);
        }
        else
        {
            _sessionPasswords[profileId] = password;
        }
    }

    /// <summary>"Invalid username/password": a password entered for this run is forgotten then, so it is asked for again.</summary>
    public const string WrongPasswordCode = "ORA-01017";

    /// <summary>Drops a password entered for this run only; a saved one stays (it is changed under "Edit").</summary>
    public void ForgetSessionPassword(Guid profileId) => _sessionPasswords.TryRemove(profileId, out _);

    /// <summary>
    /// Adds or replaces several profiles in one write (WP-35, import): one <see cref="Changed"/>, and nothing is saved if one
    /// is invalid. Passwords are left as they are.
    /// </summary>
    public async Task SaveManyAsync(IReadOnlyList<ConnectionProfile> profiles, CancellationToken cancellationToken)
    {
        foreach (var profile in profiles)
        {
            var errors = ConnectionProfileValidator.Validate(profile);
            if (errors.Count > 0)
            {
                // No parameter name: the message is shown as it is (the import validates first, this is the last guard).
                throw new ArgumentException($"{profile.Name}: {string.Join(" ", errors.Values)}");
            }
        }

        var ids = profiles.Select(p => p.Id).ToHashSet();
        if (ids.Count != profiles.Count)
        {
            // Two profiles with one id ended up twice in the file; editing one later dropped the other (WP-35 review).
            throw new ArgumentException("The same connection id is saved twice.");
        }

        RefuseWithoutFile();
        var updated = _profiles.Where(p => !ids.Contains(p.Id)).Concat(profiles).ToList();
        await store.SaveAsync(updated, cancellationToken);

        _profiles = Sort(updated);
        Changed?.Invoke();
    }

    /// <summary>Adds or replaces the profile. <paramref name="newPassword"/> null keeps the stored password.</summary>
    public async Task SaveAsync(ConnectionProfile profile, string? newPassword, CancellationToken cancellationToken)
    {
        var errors = ConnectionProfileValidator.Validate(profile);
        if (errors.Count > 0)
        {
            throw new ArgumentException(string.Join(" ", errors.Values), nameof(profile));
        }

        RefuseWithoutFile();
        var updated = _profiles.Where(p => p.Id != profile.Id).Append(profile).ToList();
        await store.SaveAsync(updated, cancellationToken);

        if (newPassword is not null)
        {
            secrets.SetPassword(profile.Id, newPassword);
            _sessionPasswords.TryRemove(profile.Id, out _);
        }

        _profiles = Sort(updated);
        Changed?.Invoke();
    }

    /// <summary>Remembers where a DDL script of this connection was saved (WP-23); a profile that is gone is left alone.</summary>
    public async Task SetScriptFolderAsync(Guid profileId, string folder, CancellationToken cancellationToken)
    {
        if (_profiles.FirstOrDefault(p => p.Id == profileId) is { } profile && profile.ScriptFolder != folder)
        {
            await SaveAsync(profile with { ScriptFolder = folder }, null, cancellationToken);
        }
    }

    public async Task DeleteAsync(Guid profileId, CancellationToken cancellationToken)
    {
        RefuseWithoutFile();
        var updated = _profiles.Where(p => p.Id != profileId).ToList();
        await store.SaveAsync(updated, cancellationToken);
        secrets.DeletePassword(profileId);
        _sessionPasswords.TryRemove(profileId, out _);

        _profiles = updated;
        Changed?.Invoke();
    }

    /// <summary>
    /// A file that could not be read is never written (R4a): the list is empty then, and the first new connection replaced
    /// the file – every connection in it was lost.
    /// </summary>
    /// <exception cref="IOException">The file could not be read at start.</exception>
    private void RefuseWithoutFile()
    {
        if (LoadError is not null)
        {
            throw new IOException(ConnectionText.FileUnreadableNotSaved);
        }
    }

    private static List<ConnectionProfile> Sort(IEnumerable<ConnectionProfile> profiles) =>
        profiles.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
}
