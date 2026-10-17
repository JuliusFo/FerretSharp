using FerretSharp.Core.Resources;

namespace FerretSharp.Core.Connections.Transfer;

/// <summary>What happens with an imported connection that already exists (same address and user).</summary>
public enum DuplicateAction
{
    /// <summary>
    /// The existing connection takes the imported settings; its id, password, workspaces, default schema and local paths
    /// stay, and its protection never loosens (see <see cref="ImportRow.ReadOnly"/>).
    /// </summary>
    Overwrite,

    /// <summary>The imported one is added beside it.</summary>
    Copy,
}

/// <summary>One line of the import preview: the found connection and what the user decided about it.</summary>
public sealed class ImportRow
{
    private readonly ConnectionImportPlan _plan;
    private readonly string? _invalid;
    private string _user;
    private ConnectionKind _kind;
    private bool _kindPicked;
    private bool _guessed;

    internal ImportRow(ConnectionImportPlan plan, ImportCandidate candidate)
    {
        _plan = plan;
        Candidate = candidate;
        _user = candidate.Profile?.User ?? "";
        _kind = candidate.Profile?.Kind ?? ConnectionKind.Other;
        _guessed = candidate.KindGuessed;

        // Checked like a saved connection, apart from the user (typed in the preview): a port of 99999 or a missing name
        // shows here instead of failing the whole import when saving.
        if (candidate.Profile is { } profile
            && ConnectionProfileValidator.Validate(profile with { User = "-" }) is { Count: > 0 } errors)
        {
            _invalid = string.Join(" ", errors.Values);
        }
    }

    public ImportCandidate Candidate { get; }

    /// <summary>Whether FerretSharp can connect this way at all.</summary>
    public bool CanImport => Candidate.Profile is not null && _invalid is null;

    /// <summary>Why not (<see cref="ImportCandidate.Unsupported"/>, or a value FerretSharp does not accept); null if it can.</summary>
    public string? Unsupported => Candidate.Unsupported ?? _invalid;

    public bool Selected { get; set; }

    /// <summary>
    /// The source's kind (or the guess, or the user's pick) – but a saved Prod connection it overwrites stays Prod unless the
    /// user picks another kind: most of the protection hangs on the kind (review 3.31.0, A5).
    /// </summary>
    public ConnectionKind Kind => KeepsProd ? ConnectionKind.Prod : _kind;

    private bool KeepsProd => !_kindPicked && Overwrites is { Kind: ConnectionKind.Prod };

    /// <summary>The kind is still the guess from the name; the connection stays read-only until the user picks one.</summary>
    public bool KindGuessed => _guessed && !KeepsProd;

    /// <summary>Picked by the user (also the guessed one, to confirm it): no longer a guess, read-only follows the kind again.</summary>
    public void SetKind(ConnectionKind kind)
    {
        _kind = kind;
        _kindPicked = true;
        _guessed = false;
    }

    /// <summary>The paths of the source's computer in this connection; taken only when the plan says so (<see cref="ConnectionImportPlan.TakeLocalPaths"/>).</summary>
    public IReadOnlyList<string> LocalPaths => Candidate.Profile?.LocalPaths() ?? [];

    /// <summary>
    /// Typed in the preview when the source had none (DBeaver without credentials). A user that makes the row a duplicate
    /// or a repeat of an earlier row deselects it – those are only imported when picked on purpose.
    /// </summary>
    public string User
    {
        get => _user;
        set
        {
            var wasPlain = Duplicate is null && SameAsEarlier is null;
            _user = value.Trim();
            if (wasPlain && (Duplicate is not null || SameAsEarlier is not null))
            {
                Selected = false;
            }
        }
    }

    public bool UserMissing => CanImport && string.IsNullOrWhiteSpace(User);

    /// <summary>The saved connection with the same address and user, if there is one.</summary>
    public ConnectionProfile? Duplicate => _plan.DuplicateOf(this);

    /// <summary>An earlier row of the same file with the same address and user (the same database in two DBeaver projects).</summary>
    public ImportRow? SameAsEarlier => _plan.EarlierTwinOf(this);

    public DuplicateAction Action { get; set; } = DuplicateAction.Overwrite;

    /// <summary>The existing connection this row overwrites when imported: selected, a duplicate, action Overwrite.</summary>
    public ConnectionProfile? Overwrites => Selected && Action == DuplicateAction.Overwrite ? Duplicate : null;

    /// <summary>
    /// Read-only when the kind is Prod or only guessed, when the source says so (a DBeaver connection marked read-only, a
    /// FerretSharp connection saved read-only) – or when it overwrites a connection that is Prod or read-only: an import
    /// never loosens protection.
    /// </summary>
    public bool ReadOnly =>
        KindGuessed
        || ConnectionProfile.IsReadOnlyByDefault(Kind)
        || Candidate.Profile is { ReadOnly: true }
        || Overwrites is { } existing && (existing.ReadOnly || ConnectionProfile.IsReadOnlyByDefault(existing.Kind));

    /// <summary>What is saved for this row.</summary>
    internal ConnectionProfile Build(string? defaultGroup)
    {
        var imported = Candidate.Profile ?? throw new InvalidOperationException("Not importable.");
        if (!_plan.TakeLocalPaths)
        {
            imported = imported.WithoutLocalPaths();
        }

        var fallbackGroup = string.IsNullOrWhiteSpace(defaultGroup) ? null : defaultGroup.Trim();
        var profile = imported with { Kind = Kind, User = User, ReadOnly = ReadOnly };
        if (Overwrites is { } existing)
        {
            // The existing connection's own settings win where the import only fills in: its default schema (DBeaver and
            // SQL Developer have none), its group if the source has none, and the paths of this computer (an import from
            // a colleague's connections.json brings theirs).
            return profile with
            {
                Id = existing.Id,
                Address = (profile.Address, existing.Address) is (TnsAliasAddress tns, TnsAliasAddress own) && own.TnsAdminPath is { } path
                    ? tns with { TnsAdminPath = path }
                    : profile.Address,
                DefaultSchema = imported.DefaultSchema ?? existing.DefaultSchema,
                Group = imported.Group ?? existing.Group ?? fallbackGroup,
                ClrProject = existing.ClrProject ?? imported.ClrProject,
                ScriptFolder = existing.ScriptFolder ?? imported.ScriptFolder,
                AppEnvironment = existing.AppEnvironment ?? imported.AppEnvironment,
                AuditTemplatePath = existing.AuditTemplatePath ?? imported.AuditTemplatePath,
            };
        }

        return profile with { Group = imported.Group ?? fallbackGroup };
    }
}

/// <summary>
/// The import preview (WP-35): the found connections against the saved ones. New connections start selected; duplicates
/// (same address and user), repeats within the file and ones FerretSharp cannot use do not. Unsupported ones can never be
/// selected.
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
            row.Selected = row.CanImport && row.Duplicate is null && row.SameAsEarlier is null;
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

    /// <summary>
    /// Take the paths of the source's computer (C# project, script folder, TNS_ADMIN) – moving one's own connections to a new
    /// computer. Off by default (decision of the user, review 3.31.0 A4): a colleague's paths do not fit, and a project
    /// path would load and run that project's code.
    /// </summary>
    public bool TakeLocalPaths { get; set; }

    /// <summary>Whether any importable row brings local paths (the dialog offers <see cref="TakeLocalPaths"/> only then).</summary>
    public bool HasLocalPaths => Rows.Any(r => r.CanImport && r.LocalPaths.Count > 0);

    public IEnumerable<ImportRow> SelectedRows => Rows.Where(r => r.Selected && r.CanImport);

    /// <summary>The rows the "all" check box acts on: importable and neither a duplicate nor a repeat within the file.</summary>
    public IEnumerable<ImportRow> PlainRows => Rows.Where(r => r.CanImport && r.Duplicate is null && r.SameAsEarlier is null);

    /// <summary>
    /// "All" selects the plain rows only – duplicates are picked one by one (decision of the user: skipped unless chosen);
    /// "none" deselects every row.
    /// </summary>
    public void SelectAll(bool selected)
    {
        foreach (var row in selected ? PlainRows : Rows)
        {
            row.Selected = selected;
        }
    }

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

            var twice = SelectedRows
                .Where(r => r.Overwrites is not null)
                .GroupBy(r => r.Overwrites!.Id)
                .FirstOrDefault(g => g.Count() > 1);
            if (twice is not null)
            {
                return TextFormat.Format(
                    ConnectionText.ImportSameTargetTwice, twice.First().Candidate.Name, twice.Skip(1).First().Candidate.Name, twice.First().Overwrites!.Name);
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
            ? _existing.FirstOrDefault(p => p.Address.SameAs(imported.Address) && SameUser(p.User, row.User))
            : null;

    internal ImportRow? EarlierTwinOf(ImportRow row) =>
        row.Candidate.Profile is { } imported && !row.UserMissing
            ? Rows.TakeWhile(r => r != row).FirstOrDefault(r =>
                r.Candidate.Profile is { } earlier && !r.UserMissing && earlier.Address.SameAs(imported.Address) && SameUser(r.User, row.User))
            : null;

    private static bool SameUser(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
