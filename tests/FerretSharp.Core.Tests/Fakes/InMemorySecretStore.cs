using System.Collections.Concurrent;
using FerretSharp.Core.Connections;

namespace FerretSharp.Core.Tests.Fakes;

/// <summary>Passwords in memory. Thread-safe: sessions open on pool threads.</summary>
public sealed class InMemorySecretStore : ISecretStore
{
    public ConcurrentDictionary<Guid, string> Passwords { get; } = [];

    public string? GetPassword(Guid profileId) => Passwords.GetValueOrDefault(profileId);

    public void SetPassword(Guid profileId, string password) => Passwords[profileId] = password;

    public void DeletePassword(Guid profileId) => Passwords.TryRemove(profileId, out _);
}
