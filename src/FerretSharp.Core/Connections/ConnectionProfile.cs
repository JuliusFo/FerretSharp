using System.Text.Json.Serialization;
using FerretSharp.Core.Oracle;

namespace FerretSharp.Core.Connections;

/// <summary>Kind of environment; drives the color coding and the read-only default.</summary>
public enum ConnectionKind
{
    Dev,
    Test,
    Prod,
    Other,
}

/// <summary>Where to find the database: host/port (service name or SID) or a TNS alias.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(HostPortAddress), "hostPort")]
[JsonDerivedType(typeof(TnsAliasAddress), "tnsAlias")]
public abstract partial record OracleAddress
{
    /// <summary>Short human-readable form, e.g. <c>db01:1521/ORCLPDB</c> or <c>ERP_TEST</c>.</summary>
    [JsonIgnore]
    public abstract string Display { get; }
}

/// <summary>Exactly one of <paramref name="ServiceName"/> and <paramref name="Sid"/> is expected to be set.</summary>
public sealed record HostPortAddress(string Host, int Port, string? ServiceName, string? Sid) : OracleAddress
{
    public const int DefaultPort = 1521;

    [JsonIgnore] // not inherited from the abstract base property
    public override string Display => ServiceName is { Length: > 0 }
        ? $"{Host}:{Port}/{ServiceName}"
        : $"{Host}:{Port}:{Sid}";
}

/// <param name="Alias">Entry in <c>tnsnames.ora</c>.</param>
/// <param name="TnsAdminPath">Directory containing <c>tnsnames.ora</c>; falls back to the <c>TNS_ADMIN</c> environment variable.</param>
public sealed record TnsAliasAddress(string Alias, string? TnsAdminPath) : OracleAddress
{
    [JsonIgnore]
    public override string Display => Alias;
}

/// <summary>A saved connection. The password is never part of the profile; see <see cref="ISecretStore"/>.</summary>
/// <param name="DefaultSchema">Schema to browse if it differs from <paramref name="User"/> (technical users).</param>
/// <param name="ReadOnly">Default true for Prod. Workspace sessions then run in a read-only transaction, so Oracle rejects any change.</param>
/// <param name="Group">Optional grouping, e.g. project or customer ("ERP", "Kasse"). Null = ungrouped.</param>
/// <param name="ClrProject">The .NET project whose EF Core model describes this database (v3, WP-11); null = none.</param>
/// <param name="ScriptFolder">
/// The folder a DDL script of the table designer was last saved to (WP-23) – usually the repository's script folder for
/// this database; the save dialog starts there. Null = none yet.
/// </param>
/// <param name="AppEnvironment">
/// The environment name the user's own app starts with when it uses this database (<c>-environment=erp-test</c>, WP-33):
/// a running app is matched to this connection by it. Null = none.
/// </param>
public sealed record ConnectionProfile(
    Guid Id,
    string Name,
    ConnectionKind Kind,
    OracleAddress Address,
    string User,
    string? DefaultSchema,
    bool ReadOnly,
    string? Group = null,
    ClrModel.ClrProjectLink? ClrProject = null,
    string? ScriptFolder = null,
    string? AppEnvironment = null)
{
    /// <summary>
    /// The schema whose objects are shown, in data dictionary form: <see cref="DefaultSchema"/> if set, otherwise the user.
    /// Unquoted names are upper-cased (<c>erp</c> → <c>ERP</c>), quoted ones keep their case.
    /// </summary>
    [JsonIgnore]
    public string EffectiveSchema => OracleIdentifier.Normalize(string.IsNullOrWhiteSpace(DefaultSchema) ? User : DefaultSchema);

    public static bool IsReadOnlyByDefault(ConnectionKind kind) => kind == ConnectionKind.Prod;

    /// <summary>
    /// The paths that only make sense on one computer: linked C# project, script folder, TNS_ADMIN folder. An export leaves
    /// them out unless asked, an import takes them only when asked (WP-35; review 3.31.0, A4: a project path of another
    /// computer loads – and runs – that project's DLLs, a TNS_ADMIN folder on a share decides where the login goes).
    /// </summary>
    public IReadOnlyList<string> LocalPaths() =>
        new[] { ClrProject?.ProjectFile, ScriptFolder, (Address as TnsAliasAddress)?.TnsAdminPath }
            .OfType<string>()
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToList();

    /// <summary>
    /// Whether open sessions made with this profile work differently from <paramref name="saved"/>: kind and read-only
    /// (protection, Prod confirmations, masking), address, user, default schema. Only a new connection applies them – the
    /// connection dialog offers it (review 3.31.0, A8). Name, group and paths apply at once.
    /// </summary>
    public bool SessionDiffers(ConnectionProfile saved)
    {
        ArgumentNullException.ThrowIfNull(saved);
        return Kind != saved.Kind || ReadOnly != saved.ReadOnly || Address != saved.Address
               || !string.Equals(User, saved.User, StringComparison.Ordinal)
               || !string.Equals(DefaultSchema ?? "", saved.DefaultSchema ?? "", StringComparison.Ordinal);
    }

    /// <summary>The profile without its <see cref="LocalPaths"/> (a TNS alias then reads TNS_ADMIN from the environment).</summary>
    public ConnectionProfile WithoutLocalPaths() => this with
    {
        ClrProject = null,
        ScriptFolder = null,
        Address = Address is TnsAliasAddress tns ? tns with { TnsAdminPath = null } : Address,
    };
}
