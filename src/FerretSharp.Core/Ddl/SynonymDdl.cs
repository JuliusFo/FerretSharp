using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Ddl;

/// <summary>
/// Creating and dropping synonyms (WP-68). Always with the owner: a schema in the profile changes only what the explorer
/// shows, not the session's schema – an unqualified <c>CREATE SYNONYM</c> would land in the login user's schema. Never
/// <c>OR REPLACE</c>: it would silently point an existing synonym somewhere else.
/// </summary>
public static class SynonymDdl
{
    /// <summary><c>CREATE SYNONYM "OWNER"."NAME" FOR "T"."X"</c> or <c>CREATE PUBLIC SYNONYM "NAME" FOR "T"."X"</c>.</summary>
    public static DdlStep Create(SynonymInfo synonym, TableRef target) =>
        new(Object(synonym), DdlStepKind.Synonym, OracleDdl.CreateSynonym(synonym, target));

    /// <summary><c>DROP SYNONYM "OWNER"."NAME"</c> or <c>DROP PUBLIC SYNONYM "NAME"</c>.</summary>
    public static DdlStep Drop(SynonymInfo synonym) =>
        new(Object(synonym), DdlStepKind.Synonym, OracleDdl.DropSynonym(synonym));

    /// <summary>
    /// The system privilege Oracle wants for the statement, to name it after ORA-01031: in the login user's schema
    /// <c>CREATE SYNONYM</c> (dropping an own one needs none), in another schema the <c>ANY</c> privilege, public synonyms
    /// their own two.
    /// </summary>
    /// <param name="loginUser">The user the session logged in as, in dictionary form.</param>
    public static string? RequiredPrivilege(SynonymInfo synonym, bool create, string loginUser) => (synonym.IsPublic, create) switch
    {
        (true, true) => "CREATE PUBLIC SYNONYM",
        (true, false) => "DROP PUBLIC SYNONYM",
        (false, _) when synonym.Owner != loginUser => create ? "CREATE ANY SYNONYM" : "DROP ANY SYNONYM",
        (false, true) => "CREATE SYNONYM",
        (false, false) => null,
    };

    private static string Object(SynonymInfo synonym) => synonym.IsPublic ? $"PUBLIC.{synonym.Name}" : $"{synonym.Owner}.{synonym.Name}";
}
