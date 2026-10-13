using FerretSharp.Core.Resources;

namespace FerretSharp.Core.Connections.Transfer;

/// <summary>What happens with an imported connection that already exists (same address and user).</summary>
public enum DuplicateAction
{
    /// <summary>The existing connection takes the imported settings; its id, password and workspaces stay.</summary>
    Overwrite,

    /// <summary>The imported one is added beside it.</summary>
    Copy,
}

/// <summary>One line of the import preview: the found connection and what the user decided about it.</summary>
public sealed class ImportRow
{
    private readonly ConnectionImportPlan _plan;
    private string _user;

    internal ImportRow(ConnectionImportPlan plan, ImportCandidate candidate)
    {
        _plan = plan;
        Candidate = candidate;
        _user = candidate.Profile?.User ?? "";
        Kind = candidate.Profile?.Kind ?? ConnectionKind.Other;
        KindGuessed = candidate.KindGuessed;
    }

    public ImportCandidate Candidate { get; }

    /// <summary>Whether FerretSharp can connect this way at all (see <see cref="ImportCandidate.Unsupported"/>).</summary>
    public bool CanImport => Candidate.Profile is not null;

    public bool Selected { get; set; }

    public ConnectionKind Kind { get; private set; }

    /// <summary>The kind is still the guess from the name; the connection stays read-only until the user picks one.</summary>
    public bool KindGuessed { get; private set; }

    /// <summary>Picked by the user: no longer a guess, read-only follows the kind again.</summary>
    public void SetKind(ConnectionKind kind)
    {
        Kind = kind;
        KindGuessed = false;
    }

    /// <summary>Typed in the preview when the source had none (DBeaver without credentials).</summary>
    public string User
    {
        get => _user;
        set => _user = value.Trim();
    }

    public bool UserMissing => CanImport && string.IsNullOrWhiteSpace(User);

    /// <summary>
    /// Read-only when the kind is Prod or only guessed, or when the source says so (a DBeaver connection marked
    /// read-only, a FerretSharp connection saved read-only).
    /// </summary>
    public bool ReadOnly => KindGuessed || ConnectionProfile.IsReadOnlyByDefault(Kind) || Candidate.Profile is { ReadOnly: true };

    /// <summary>The saved connection with the same address and user, if there is one.</summary>
    public ConnectionProfile? Duplicate => _plan.DuplicateOf(this);

    public DuplicateAction Action { get; set; } = DuplicateAction.Overwrite;

    /// <summary>
    /// What is saved for this row. A connection without a group from the source gets <paramref name="defaultGroup"/> – an
    /// overwritten one keeps its own group then.
    /// </summary>
    internal ConnectionProfile Build(string? defaultGroup)
    {
        var imported = Candidate.Profile ?? throw new InvalidOperationException("Not importable.");
        var fallbackGroup = string.IsNullOrWhiteSpace(defaultGroup) ? null : defaultGroup.Trim();
        var profile = imported with { Kind = Kind, User = User, ReadOnly = ReadOnly };
        if (Duplicate is { } existing && Action == DuplicateAction.Overwrite)
        {
            return profile with
            {
                Id = existing.Id,
                Group = imported.Group ?? existing.Group ?? fallbackGroup,
                ClrProject = profile.ClrProject ?? existing.ClrProject,
                ScriptFolder = profile.ScriptFolder ?? existing.ScriptFolder,
                AppEnvironment = profile.AppEnvironment ?? existing.AppEnvironment,
            };
        }

        return profile with { Group = imported.Group ?? fallbackGroup };
    }
}

/// <summary>
/// The import preview (WP-35): the found connections against the saved ones. New connections start selected, duplicates
/// (same address and user) and ones FerretSharp cannot use do not. Unsupported ones can never be selected.
/// </summary>
public sealed class ConnectionImportPlan
{
    private readonly IReadOnlyList<ConnectionProfile> _existing;

    public ConnectionImportPlan(ImportFile file, IReadOnlyList<ConnectionProfile> existing)
    {
        File = file;
        _existing = existing;
        Rows = file.Candidates.Select(c => new ImportRow(this, c)).ToList();
        foreach (var row in Rows)
        {
            row.Selected = row.CanImport && row.Duplicate is null;
        }

        DefaultGroup = file.Source switch
        {
            ConnectionImportSource.DBeaver => "DBeaver",
            ConnectionImportSource.SqlDeveloper => "SQL Developer",
            _ => "",
        };
    }

    public ImportFile File { get; }

    public IReadOnlyList<ImportRow> Rows { get; }

    /// <summary>The group for imported connections that bring none; empty = no group.</summary>
    public string DefaultGroup { get; set; }

    public IEnumerable<ImportRow> SelectedRows => Rows.Where(r => r.Selected && r.CanImport);

    /// <summary>Why the selection cannot be imported as it is; null if it can.</summary>
    public string? Problem
    {
        get
        {
            var missing = SelectedRows.Count(r => r.UserMissing);
            if (missing > 0)
            {
                return TextFormat.Plural(missing, ConnectionText.ImportUserMissingOne, ConnectionText.ImportUserMissingOther);
            }

            return SelectedRows.Any() ? null : ConnectionText.ImportNothingSelected;
        }
    }

    /// <summary>The profiles to save: new ones with their own id, overwritten ones with the id of the existing connection.</summary>
    /// <exception cref="InvalidOperationException">There is a <see cref="Problem"/>.</exception>
    public IReadOnlyList<ConnectionProfile> Build()
    {
        if (Problem is { } problem)
        {
            throw new InvalidOperationException(problem);
        }

        return SelectedRows.Select(r => r.Build(DefaultGroup)).ToList();
    }

    internal ConnectionProfile? DuplicateOf(ImportRow row) =>
        row.Candidate.Profile is { } imported && !row.UserMissing
            ? _existing.FirstOrDefault(p => SameAddress(p.Address, imported.Address) && string.Equals(p.User, row.User, StringComparison.OrdinalIgnoreCase))
            : null;

    /// <summary>Host, service, SID and alias compare without case (Oracle and DNS ignore it); the port exactly.</summary>
    internal static bool SameAddress(OracleAddress a, OracleAddress b) => (a, b) switch
    {
        (HostPortAddress x, HostPortAddress y) =>
            x.Port == y.Port
            && Same(x.Host, y.Host)
            && Same(x.ServiceName, y.ServiceName)
            && Same(x.Sid, y.Sid),
        (TnsAliasAddress x, TnsAliasAddress y) => Same(x.Alias, y.Alias),
        _ => false,
    };

    private static bool Same(string? a, string? b) =>
        string.Equals(string.IsNullOrWhiteSpace(a) ? null : a.Trim(), string.IsNullOrWhiteSpace(b) ? null : b.Trim(), StringComparison.OrdinalIgnoreCase);
}
