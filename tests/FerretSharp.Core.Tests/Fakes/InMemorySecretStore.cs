using FerretSharp.Core.Connections;

namespace FerretSharp.Core.Tests.Fakes;

public sealed class InMemorySecretStore : ISecretStore
{
    public Dictionary<Guid, string> Passwords { get; } = [];

    public string? GetPassword(Guid profileId) => Passwords.GetValueOrDefault(profileId);

    public void SetPassword(Guid profileId, string password) => Passwords[profileId] = password;

    public void DeletePassword(Guid profileId) => Passwords.Remove(profileId);
}
