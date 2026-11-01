using FerretSharp.Core.Ddl;
using FerretSharp.Core.Oracle;
using FerretSharp.Core.Resources;
using FerretSharp.Core.Schema;
using FerretSharp.UI.Resources;

namespace FerretSharp.UI.State;

/// <summary>
/// A grant in the views "Berechtigungen" and "Berechtigungen im Schema" (WP-71): grantee as typed, privileges from the list
/// for the kind of object, optionally columns and the grant option. One GRANT to one grantee – and, if asked for, a private
/// synonym of the grantee on the object, so it can name the object without its schema (idea of a colleague, decided by the
/// user on 2026-11-01). Roles cannot be listed without DBA rights, so a grantee is a role when the object's grants name it or
/// it is not a user (<paramref name="users"/>).
/// </summary>
/// <param name="columns">The object's columns to grant INSERT, UPDATE and REFERENCES on; empty where that is not offered.</param>
/// <param name="users">The users of the database; empty if they could not be read – then nothing is checked against them.</param>
/// <param name="roles">Grantees of the object's grants that are no users: roles as far as the session can see.</param>
/// <param name="synonyms">
/// The synonyms on the object (<see cref="SchemaCache.GetSynonymsAsync"/>): a grantee's own one is visible, the object being
/// readable. Empty if they could not be read – then none is known.
/// </param>
public sealed class GrantDraft(
    TableRef target, GrantObjectKind kind, IReadOnlyList<string> columns, IReadOnlyList<string> users, IReadOnlyCollection<string> roles,
    IReadOnlyList<SynonymInfo>? synonyms = null)
{
    private readonly HashSet<string> _users = users.ToHashSet(StringComparer.Ordinal);
    private readonly HashSet<string> _privileges = [];
    private readonly HashSet<string> _columns = [];

    /// <summary>Statements that ran already (<see cref="Ran"/>): a GRANT among them is left out of <see cref="Steps"/>.</summary>
    private readonly HashSet<string> _ran = new(StringComparer.Ordinal);

    /// <summary>
    /// Every CREATE SYNONYM the draft wrote, with its synonym: a failure is told by the statement that ran, not by the draft
    /// as it is now – it can change while a run takes its time over the VPN (third pass of the review).
    /// </summary>
    private readonly Dictionary<string, SynonymInfo> _synonymStatements = new(StringComparer.Ordinal);

    public TableRef Target { get; } = target;

    public GrantObjectKind Kind { get; } = kind;

    /// <summary>The privileges to choose from (<see cref="GrantPrivileges.For"/>).</summary>
    public IReadOnlyList<string> Offered { get; } = GrantPrivileges.For(kind);

    public IReadOnlyList<string> OfferedColumns { get; } = columns;

    /// <summary>The grantee as typed; without quotes it is upper-cased (<see cref="OracleIdentifier.Normalize"/>).</summary>
    public string Grantee { get; set; } = "";

    public bool GrantOption { get; set; }

    /// <summary>Also create <see cref="Synonym"/>; off unless chosen (decision of the user, 2026-11-01).</summary>
    public bool CreateSynonym { get; set; }

    /// <summary>The synonym's name as typed; it starts as the object's name, quoted where Oracle needs it.</summary>
    public string SynonymName { get; set; } = OracleIdentifier.QuoteIfNeeded(target.Name);

    /// <summary>
    /// A synonym is offered for a user other than the object's owner – a role has no schema, and PUBLIC would be a public
    /// synonym, which is not offered (the user's database has tens of thousands).
    /// </summary>
    public bool SynonymOffered =>
        GranteeName is { } grantee && OracleIdentifier.Problem(grantee) is null && grantee != ObjectGrant.PublicGrantee
        && grantee != Target.Owner && !IsRole(grantee);

    /// <summary>The grantee's private synonym on the object, if it has one already; then none is created.</summary>
    public SynonymInfo? ExistingSynonym => GranteeName is { } grantee ? (synonyms ?? []).FirstOrDefault(s => s.Owner == grantee) : null;

    /// <summary>The synonym to create with the grant; null unless chosen, offered, not there yet and named as Oracle takes it.</summary>
    public SynonymInfo? Synonym =>
        CreateSynonym && SynonymOffered && ExistingSynonym is null
        && OracleIdentifier.Normalize(SynonymName) is { Length: > 0 } name && OracleIdentifier.Problem(name) is null
            ? new SynonymInfo(GranteeName!, name)
            : null;

    /// <summary>Whether the synonym's name is a problem right now (chosen, offered and not there yet).</summary>
    private bool SynonymWanted => CreateSynonym && SynonymOffered && ExistingSynonym is null;

    /// <summary>The chosen privileges in the order of <see cref="Offered"/>.</summary>
    public IReadOnlyList<string> Privileges => [.. Offered.Where(_privileges.Contains)];

    /// <summary>The columns chosen while a privilege takes them; empty for privileges on the whole object.</summary>
    public IReadOnlyList<string> Columns => ColumnsApply ? [.. OfferedColumns.Where(_columns.Contains)] : [];

    /// <summary>A chosen privilege Oracle grants per column, and the object has columns to choose.</summary>
    public bool ColumnsApply => OfferedColumns.Count > 0 && _privileges.Any(GrantPrivileges.TakesColumns);

    /// <summary>For the input's list, as typed (<see cref="Typed"/>): PUBLIC, the roles seen, then the users.</summary>
    public IReadOnlyList<string> Suggestions =>
        [ObjectGrant.PublicGrantee, .. roles.Order(StringComparer.Ordinal).Select(Typed), .. users.Where(u => !roles.Contains(u)).Select(Typed)];

    /// <summary>
    /// A grantee as the dictionary names it, written as one types it: quoted unless Oracle would read it back the same
    /// without (a role <c>app_ro</c> created in quotes would become <c>APP_RO</c> – another grantee; review of WP-71).
    /// PUBLIC stays the keyword. A name no grantee can have (a double quote, too long – typed or from a workspace file) stays as
    /// it is: quoting would throw while rendering (second pass of the review).
    /// </summary>
    public static string Typed(string grantee) =>
        grantee.Length == 0 || grantee == ObjectGrant.PublicGrantee || OracleIdentifier.Problem(grantee) is not null
            ? grantee
            : OracleIdentifier.QuoteIfNeeded(grantee);

    /// <summary>The grantee as Oracle names it; null while none is typed.</summary>
    public string? GranteeName => OracleIdentifier.Normalize(Grantee) is { Length: > 0 } name ? name : null;

    public bool IsChosen(string privilege) => _privileges.Contains(privilege);

    public bool IsColumnChosen(string column) => _columns.Contains(column);

    public void Choose(string privilege, bool chosen)
    {
        if (chosen && Offered.Contains(privilege))
        {
            _privileges.Add(privilege);
        }
        else
        {
            _privileges.Remove(privilege);
        }
    }

    public void ChooseColumn(string column, bool chosen)
    {
        if (chosen && OfferedColumns.Contains(column))
        {
            _columns.Add(column);
        }
        else
        {
            _columns.Remove(column);
        }
    }

    /// <summary>
    /// The GRANT, then the synonym: it runs only once the grantee may use the object. A GRANT that ran already – the synonym
    /// failed after it – is left out, so running again, copying and the SQL editor take the synonym alone: the grantee who
    /// is to run it could not grant (third pass of the review). Changing the grant brings it back.
    /// </summary>
    public IReadOnlyList<DdlStep> Steps
    {
        get
        {
            if (GranteeName is not { } grantee || OracleIdentifier.Problem(grantee) is not null || Privileges.Count == 0)
            {
                return [];
            }

            var grant = GrantDdl.Grant(Target, Privileges, Columns, grantee, GrantOption);
            var steps = _ran.Contains(grant.Sql) ? new List<DdlStep>() : [grant];
            if (Synonym is { } synonym)
            {
                var create = SynonymDdl.Create(synonym, Target);
                _synonymStatements[create.Sql] = synonym;
                steps.Add(create);
            }

            return steps;
        }
    }

    /// <summary>Statements of a run that ran before one failed (<c>DdlRunResult.Ran</c>).</summary>
    public void Ran(IEnumerable<string> statements) => _ran.UnionWith(statements);

    /// <summary>
    /// What keeps it from running: no grantee or one Oracle cannot take, the owner (who has every privilege), no privilege, a
    /// role with the grant option (ORA-01926), a synonym without a name Oracle takes.
    /// </summary>
    public IReadOnlyList<string> Problems
    {
        get
        {
            if (GranteeName is not { } grantee)
            {
                return [SchemaViewText.Grants_GranteeMissing];
            }

            if (OracleIdentifier.Problem(grantee) is { } problem)
            {
                return [problem];
            }

            return
            [
                .. grantee == Target.Owner ? [TextFormat.Format(SchemaViewText.Grants_OwnerHasAll, grantee)] : Array.Empty<string>(),
                .. Privileges.Count == 0 ? [SchemaViewText.Grants_PrivilegeMissing] : Array.Empty<string>(),
                .. GrantOption && IsRole(grantee) ? [TextFormat.Format(SchemaViewText.Grants_RoleNoGrantOption, grantee)] : Array.Empty<string>(),
                .. SynonymWanted ? SynonymNameProblems() : [],
            ];
        }
    }

    private IReadOnlyList<string> SynonymNameProblems() =>
        OracleIdentifier.Normalize(SynonymName) is not { Length: > 0 } name ? [SchemaViewText.Synonyms_NameMissing]
        : OracleIdentifier.Problem(name) is { } problem ? [problem]
        : [];

    /// <summary>
    /// A grant to PUBLIC is one to every user; a grantee neither a user nor a role seen here may not exist (ORA-01917); a
    /// synonym name SQL must write in quotes.
    /// </summary>
    public IReadOnlyList<string> Warnings =>
    [
        .. GranteeName switch
        {
            ObjectGrant.PublicGrantee => [SchemaViewText.Grants_PublicWarning],
            { } grantee when _users.Count > 0 && !_users.Contains(grantee) && !roles.Contains(grantee) =>
                [TextFormat.Format(SchemaViewText.Grants_UnknownGrantee, grantee)],
            _ => Array.Empty<string>(),
        },
        .. Synonym is { } synonym && OracleIdentifier.NeedsQuotes(synonym.Name) ? [SchemaViewText.Synonyms_NeedsQuotes] : Array.Empty<string>(),
    ];

    /// <summary>
    /// What a failed run needs: the synonym refused after the grant ran – the privilege to create it in the grantee's schema
    /// (ORA-01031) or its name taken there (ORA-00955) –, else <see cref="ErrorHint"/> for the GRANT.
    /// </summary>
    /// <param name="failed">The statement that failed (<c>DdlRunResult.Failed</c>).</param>
    /// <param name="loginUser">The user the session logged in as, in dictionary form.</param>
    public string? FailureHint(string? code, string? failed, string loginUser) =>
        failed is not null && _synonymStatements.TryGetValue(failed, out var synonym)
            ? code switch
            {
                "ORA-01031" => TextFormat.Format(SchemaViewText.Grants_SynonymNeedsPrivilege, synonym.Owner,
                    SynonymDdl.RequiredPrivilege(synonym, create: true, loginUser) ?? "CREATE SYNONYM"),
                "ORA-00955" => TextFormat.Format(SchemaViewText.Grants_SynonymNameTaken, synonym.Owner),
                _ => null,
            }
            : ErrorHint(code, revoked: null);

    private bool IsRole(string grantee) =>
        grantee != ObjectGrant.PublicGrantee && (roles.Contains(grantee) || (_users.Count > 0 && !_users.Contains(grantee)));

    /// <summary>The grantees of <paramref name="grants"/> that are no users and not PUBLIC: roles.</summary>
    public static IReadOnlyCollection<string> RolesOf(IEnumerable<ObjectGrant> grants, IReadOnlyCollection<string> users) =>
        grants.Select(g => g.Grantee).Where(g => g != ObjectGrant.PublicGrantee && !users.Contains(g)).ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// What revoking <paramref name="grant"/> does beyond the row (measured against Oracle 23): the privilege goes on every
    /// column and with the grant's other rows of the same privilege; what the grantee passed on goes along; from PUBLIC it
    /// goes for everyone who had it only that way; only the grantor can revoke; a foreign key over REFERENCES refuses it.
    /// </summary>
    /// <param name="all">The grants on the object (the row's siblings).</param>
    /// <param name="loginUser">The user the session logged in as, in dictionary form.</param>
    /// <param name="synonyms">The synonyms on the object: one of the grantee leads nowhere once its last privilege goes.</param>
    public static IReadOnlyList<string> RevokeNotes(ObjectGrant grant, IReadOnlyList<ObjectGrant> all, string loginUser, IReadOnlyList<SynonymInfo>? synonyms = null)
    {
        var notes = new List<string>();
        var siblings = all.Count(g => g.Grantee == grant.Grantee && g.Privilege == grant.Privilege && g.Grantor == grant.Grantor);
        if (grant.Columns.Count > 0 || siblings > 1)
        {
            notes.Add(TextFormat.Format(SchemaViewText.Grants_RevokeAllColumns, grant.Privilege, grant.Grantee));
        }

        if (grant.Grantable)
        {
            notes.Add(TextFormat.Format(SchemaViewText.Grants_RevokeCascades, grant.Grantee));
        }

        if (grant.IsPublic)
        {
            notes.Add(SchemaViewText.Grants_RevokePublic);
        }

        if (grant.Grantor != loginUser)
        {
            notes.Add(TextFormat.Format(SchemaViewText.Grants_RevokeOtherGrantor, grant.Grantor));
        }

        if (grant.Privilege == "REFERENCES")
        {
            notes.Add(TextFormat.Format(SchemaViewText.Grants_RevokeReferences, grant.Grantee));
        }

        // The REVOKE takes every row of this privilege and grantor; another privilege of the grantee – or any of PUBLIC (third
        // pass of the review) – keeps the synonym useful. One through a role the dictionary does not show; the note says so.
        var remains = all.Any(g => (g.IsPublic && !grant.IsPublic)
            || (g.Grantee == grant.Grantee && (g.Privilege != grant.Privilege || g.Grantor != grant.Grantor)));
        if (!remains && (synonyms ?? []).FirstOrDefault(s => s.Owner == grant.Grantee) is { } synonym)
        {
            notes.Add(TextFormat.Format(SchemaViewText.Grants_RevokeLeavesSynonym, grant.Grantee, synonym.Name));
        }

        return notes;
    }

    /// <summary>
    /// What keeps a revoke from running: the grant is someone else's and the login user gave the grantee the same privilege
    /// too – Oracle revokes only the executing user's grants, so the REVOKE would take that one and leave the chosen row
    /// (measured against Oracle 23; review of WP-71).
    /// </summary>
    public static IReadOnlyList<string> RevokeProblems(ObjectGrant grant, IReadOnlyList<ObjectGrant> all, string loginUser) =>
        grant.Grantor != loginUser && all.Any(g => g.Grantee == grant.Grantee && g.Privilege == grant.Privilege && g.Grantor == loginUser)
            ? [TextFormat.Format(SchemaViewText.Grants_RevokeHitsOwn, grant.Grantee, grant.Privilege, grant.Grantor)]
            : [];

    /// <summary>
    /// What a refused GRANT or REVOKE needs, by its Oracle code; null for codes whose message says it all.
    /// </summary>
    /// <param name="revoked">The grant a REVOKE was for; null for a GRANT.</param>
    public static string? ErrorHint(string? code, ObjectGrant? revoked) => code switch
    {
        "ORA-01031" => SchemaViewText.Grants_HintNoPrivilege,
        "ORA-01720" => SchemaViewText.Grants_HintViewGrantOption,
        "ORA-01927" when revoked is not null => TextFormat.Format(SchemaViewText.Grants_HintOtherGrantor, revoked.Grantor),
        "ORA-01981" when revoked is not null => TextFormat.Format(SchemaViewText.Grants_HintCascadeConstraints, revoked.Grantee),
        "ORA-01926" => SchemaViewText.Grants_HintRoleGrantOption,
        _ => null,
    };
}
