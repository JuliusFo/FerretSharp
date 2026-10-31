namespace FerretSharp.Core.Schema;

/// <summary>
/// Whether a synonym of the schema leads anywhere (WP-68, measured against Oracle 23). <c>ALL_OBJECTS</c> shows only what
/// the session may access, so a target that is not there is either gone or hidden – only some cases tell which.
/// </summary>
public enum SynonymTargetState
{
    /// <summary>The target is there (a table, a view, a package, another synonym …).</summary>
    Ok,

    /// <summary>
    /// Certainly gone: dropped after the synonym was created (Oracle marks the synonym INVALID), its owner no longer exists,
    /// or it would lie in the login user's own schema, where every object is visible.
    /// </summary>
    Missing,

    /// <summary>
    /// Not visible to the session: gone, never created (<c>CREATE SYNONYM</c> does not check its target), or no privilege on
    /// it – the dictionary does not say which.
    /// </summary>
    MissingOrNoAccess,

    /// <summary>Over a database link: what is behind it is not checked.</summary>
    DbLink,
}

/// <summary>A private synonym of a schema with where it points (the list "Synonyme des Schemas", WP-68).</summary>
/// <param name="TargetOwner">Null for a synonym over a database link that names no owner.</param>
/// <param name="TargetType">The target's <c>OBJECT_TYPE</c> (<c>TABLE</c>, <c>PACKAGE</c>, <c>SYNONYM</c> …) if it is visible; null otherwise.</param>
public sealed record SchemaSynonym(
    string Owner,
    string Name,
    string? TargetOwner,
    string TargetName,
    string? DbLink,
    string? TargetType,
    SynonymTargetState State)
{
    public SynonymInfo Synonym => new(Owner, Name);

    /// <summary><c>OWNER.NAME</c>, with <c>@LINK</c> for a database link.</summary>
    public string Target => (TargetOwner is null ? TargetName : $"{TargetOwner}.{TargetName}") + (DbLink is null ? "" : $"@{DbLink}");

    /// <summary>
    /// The state by what the dictionary shows (<see cref="SynonymTargetState"/>): the target found, the synonym marked
    /// INVALID, the target's owner gone, or the target in the login user's schema.
    /// </summary>
    public static SynonymTargetState StateOf(bool overDbLink, bool targetVisible, bool synonymInvalid, bool targetOwnerExists, bool targetInOwnSchema) =>
        overDbLink ? SynonymTargetState.DbLink
        : targetVisible ? SynonymTargetState.Ok
        : synonymInvalid || !targetOwnerExists || targetInOwnSchema ? SynonymTargetState.Missing
        : SynonymTargetState.MissingOrNoAccess;
}
