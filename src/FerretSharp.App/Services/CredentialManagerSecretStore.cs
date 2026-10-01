using FerretSharp.Core.Connections;
using Meziantou.Framework.Win32;

namespace FerretSharp.App.Services;

/// <summary>Stores connection passwords as generic credentials in the Windows Credential Manager.</summary>
public sealed class CredentialManagerSecretStore : ISecretStore
{
    private const string TargetPrefix = "FerretSharp:connection:";

    public string? GetPassword(Guid profileId) => CredentialManager.ReadCredential(Target(profileId))?.Password;

    public void SetPassword(Guid profileId, string password) =>
        CredentialManager.WriteCredential(
            Target(profileId),
            "FerretSharp",
            password,
            "Oracle connection password (FerretSharp)",
            CredentialPersistence.LocalMachine);

    public void DeletePassword(Guid profileId) => CredentialManager.TryDeleteCredential(Target(profileId));

    private static string Target(Guid profileId) => TargetPrefix + profileId.ToString("D");
}
