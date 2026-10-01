namespace FerretSharp.Core.Connections;

/// <summary>Stores connection passwords outside the JSON files (Windows: Credential Manager).</summary>
public interface ISecretStore
{
    string? GetPassword(Guid profileId);

    void SetPassword(Guid profileId, string password);

    void DeletePassword(Guid profileId);
}
