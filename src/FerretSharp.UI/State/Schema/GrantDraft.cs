using FerretSharp.Core.Ddl;
using FerretSharp.Core.Oracle;
using FerretSharp.Core.Resources;
using FerretSharp.Core.Schema;
using FerretSharp.UI.Resources;

namespace FerretSharp.UI.State;

/// <summary>
/// A grant in the views "Berechtigungen" and "Berechtigungen im Schema" (WP-71): grantee as typed, privileges from the list
/// for the kind of object, optionally columns and the grant option. One GRANT to one grantee. Roles cannot be listed without
/// DBA rights, so a grantee is a role when the object's grants name it or it is not a user (<paramref name="users"/>).
/// </summary>
/// <param name="columns">The object's columns to grant INSERT, UPDATE and REFERENCES on; empty where that is not offered.</param>
/// <param name="users">The users of the database; empty if they could not be read – then nothing is checked against them.</param>
/// <param name="roles">Grantees of the object's grants that are no users: roles as far as the session can see.</param>
public sealed class GrantDraft(TableRef target, GrantObjectKind kind, IReadOnlyList<string> columns, IReadOnlyList<string> users, IReadOnlyCollection<string> roles)
{
    private readonly HashSet<string> _users = users.ToHashSet(StringComparer.Ordinal);
    private readonly HashSet<string> _privileges = [];
    private readonly HashSet<string> _columns = [];

    public TableRef Target { get; } = target;

    public GrantObjectKind Kind { get; } = kind;

    /// <summary>The privileges to choose from (<see cref="GrantPrivileges.For"/>).</summary>
    public IReadOnlyList<string> Offered { get; } = GrantPrivileges.For(kind);

    public IReadOnlyList<string> OfferedColumns { get; } = columns;

    /// <summary>The grantee as typed; without quotes it is upper-cased (<see cref="OracleIdentifier.Normalize"/>).</summary>
    public string Grantee { get; set; } = "";

    public bool GrantOption { get; set; }

    /// <summary>The chosen privileges in the order of <see cref="Offered"/>.</summary>
    public IReadOnlyList<string> Privileges => [.. Offered.Where(_privileges.Contains)];

    /// <summary>The columns chosen while a privilege takes them; empty for privileges on the whole object.</summary>
    public IReadOnlyList<string> Columns => ColumnsApply ? [.. OfferedColumns.Where(_columns.Contains)] : [];

    /// <summary>A chosen privilege Oracle grants per column, and the object has columns to choose.</summary>
    public bool ColumnsApply => OfferedColumns.Count > 0 && _privileges.Any(GrantPrivileges.TakesColumns);

    /// <summary>For the input's list: PUBLIC, the roles seen, then the users.</summary>
    public IReadOnlyList<string> Suggestions =>
        [ObjectGrant.PublicGrantee, .. roles.Order(StringComparer.Ordinal), .. users.Where(u => !roles.Contains(u))];

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

    public IReadOnlyList<DdlStep> Steps =>
        GranteeName is { } grantee && OracleIdentifier.Problem(grantee) is null && Privileges.Count > 0
            ? [GrantDdl.Grant(Target, Privileges, Columns, grantee, GrantOption)]
            : [];

    /// <summary>
    /// What keeps it from running: no grantee or one Oracle cannot take, the owner (who has every privilege), no privilege, a
    /// role with the grant option (ORA-01926).
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
            ];
        }
    }

    /// <summary>A grant to PUBLIC is one to every user; a grantee neither a user nor a role seen here may not exist (ORA-01917).</summary>
    public IReadOnlyList<string> Warnings => GranteeName switch
    {
        ObjectGrant.PublicGrantee => [SchemaViewText.Grants_PublicWarning],
        { } grantee when _users.Count > 0 && !_users.Contains(grantee) && !roles.Contains(grantee) =>
            [TextFormat.Format(SchemaViewText.Grants_UnknownGrantee, grantee)],
        _ => [],
    };

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
    public static IReadOnlyList<string> RevokeNotes(ObjectGrant grant, IReadOnlyList<ObjectGrant> all, string loginUser)
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

        return notes;
    }

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
