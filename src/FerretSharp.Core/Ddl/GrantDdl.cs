using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Ddl;

/// <summary>
/// Granting and revoking object privileges (WP-71), always with the object's owner and the grantee as in the dictionary;
/// <c>PUBLIC</c> as the keyword. A REVOKE is per object and privilege – Oracle has none per column (ORA-01750).
/// </summary>
public static class GrantDdl
{
    /// <summary>One GRANT of <paramref name="privileges"/> to one grantee; <paramref name="columns"/> as in <see cref="OracleDdl.Grant"/>.</summary>
    public static DdlStep Grant(TableRef target, IReadOnlyList<string> privileges, IReadOnlyList<string> columns, string grantee, bool grantOption) =>
        new(Object(target), DdlStepKind.Grant, OracleDdl.Grant(target, privileges, columns, grantee, grantOption));

    public static DdlStep Revoke(TableRef target, ObjectGrant grant) =>
        new(Object(target), DdlStepKind.Grant, OracleDdl.Revoke(target, grant.Privilege, grant.Grantee));

    /// <summary>
    /// The GRANT statements that give <paramref name="grants"/> again (the DDL view): one per grantee, options and column
    /// set, privileges on the whole object first. Who granted is left out – run by the owner, they all work.
    /// </summary>
    public static IReadOnlyList<string> Script(TableRef target, IReadOnlyList<ObjectGrant> grants) =>
    [
        .. grants
            .GroupBy(g => (g.Grantee, g.Grantable, g.Hierarchy, Columns: string.Join("\0", g.Columns)))
            .OrderBy(g => g.Key.Grantee == ObjectGrant.PublicGrantee ? 0 : 1)
            .ThenBy(g => g.Key.Grantee, StringComparer.Ordinal)
            .ThenBy(g => g.Key.Columns.Length)
            .ThenBy(g => g.Key.Columns, StringComparer.Ordinal)
            .ThenBy(g => g.Key.Grantable)
            .Select(g => OracleDdl.Grant(target, g.Select(x => x.Privilege).Distinct().Order(StringComparer.Ordinal), g.First().Columns,
                g.Key.Grantee, g.Key.Grantable, g.Key.Hierarchy)),
    ];

    private static string Object(TableRef target) => $"{target.Owner}.{target.Name}";
}
