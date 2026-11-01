namespace FerretSharp.Core.Schema;

/// <summary>The kinds of object whose privileges FerretSharp shows and grants (WP-71).</summary>
public enum GrantObjectKind
{
    Table,
    View,
    MaterializedView,
    Sequence,
    Package,
    Procedure,
    Function,
}

/// <summary>
/// One object privilege of a grantee (WP-71): on the whole object, or on <see cref="Columns"/> – Oracle grants INSERT,
/// UPDATE and REFERENCES per column too, one row each in <c>ALL_COL_PRIVS</c>; those of one grant are one entry here.
/// </summary>
/// <param name="Grantee">A user, a role or <see cref="PublicGrantee"/>.</param>
/// <param name="Columns">Empty for a privilege on the whole object.</param>
/// <param name="Grantable">Granted <c>WITH GRANT OPTION</c>: the grantee may pass it on.</param>
/// <param name="Hierarchy">Granted <c>WITH HIERARCHY OPTION</c> (SELECT on an object view and its subviews).</param>
public sealed record ObjectGrant(string Grantee, string Privilege, IReadOnlyList<string> Columns, string Grantor, bool Grantable, bool Hierarchy)
{
    /// <summary>The grantee that stands for every user of the database – a keyword, not a user or role.</summary>
    public const string PublicGrantee = "PUBLIC";

    public bool IsPublic => Grantee == PublicGrantee;

    public bool Equals(ObjectGrant? other) =>
        other is not null && Grantee == other.Grantee && Privilege == other.Privilege && Grantor == other.Grantor
        && Grantable == other.Grantable && Hierarchy == other.Hierarchy && Columns.SequenceEqual(other.Columns, StringComparer.Ordinal);

    public override int GetHashCode() => HashCode.Combine(Grantee, Privilege, Grantor, Grantable, Hierarchy, Columns.Count);
}

/// <summary>An object of a schema with the privileges the session can see on it (the list "Berechtigungen im Schema", WP-71).</summary>
public sealed record SchemaObjectGrants(TableRef Object, GrantObjectKind Kind, IReadOnlyList<ObjectGrant> Grants)
{
    /// <summary>
    /// Whether <paramref name="grantee"/> has any privilege on the object as far as the dictionary shows: granted directly or
    /// to <c>PUBLIC</c>. Roles are not resolved – a user who has it only through a role counts as without.
    /// </summary>
    public bool Reaches(string grantee) => Grants.Any(g => g.Grantee == grantee || g.IsPublic);
}

/// <summary>The object privileges FerretSharp offers per kind of object (WP-71; measured against Oracle 23).</summary>
public static class GrantPrivileges
{
    /// <summary>
    /// The privileges to choose from, the common ones first. Sequences take only SELECT and ALTER (ORA-02205), PL/SQL
    /// EXECUTE and DEBUG, a table no EXECUTE (ORA-02224). Left out: ON COMMIT REFRESH and QUERY REWRITE (for materialized
    /// views of others), UNDER (object views).
    /// </summary>
    public static IReadOnlyList<string> For(GrantObjectKind kind) => kind switch
    {
        GrantObjectKind.Table => ["SELECT", "READ", "INSERT", "UPDATE", "DELETE", "REFERENCES", "ALTER", "INDEX", "DEBUG", "FLASHBACK"],
        GrantObjectKind.View => ["SELECT", "READ", "INSERT", "UPDATE", "DELETE", "REFERENCES", "MERGE VIEW", "DEBUG", "FLASHBACK"],
        GrantObjectKind.MaterializedView => ["SELECT", "READ"],
        GrantObjectKind.Sequence => ["SELECT", "ALTER"],
        _ => ["EXECUTE", "DEBUG"],
    };

    /// <summary>A privilege Oracle grants per column as well: INSERT, UPDATE and REFERENCES on tables and views.</summary>
    public static bool TakesColumns(string privilege) => privilege is "INSERT" or "UPDATE" or "REFERENCES";

    public static GrantObjectKind KindOf(TableKind kind) => kind switch
    {
        TableKind.View => GrantObjectKind.View,
        TableKind.MaterializedView => GrantObjectKind.MaterializedView,
        _ => GrantObjectKind.Table,
    };

    public static GrantObjectKind KindOf(PlSqlKind kind) => kind switch
    {
        PlSqlKind.Package => GrantObjectKind.Package,
        PlSqlKind.Function => GrantObjectKind.Function,
        _ => GrantObjectKind.Procedure,
    };
}
