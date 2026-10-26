namespace FerretSharp.Core.Ddl;

/// <summary>
/// What a DDL step does. The order of the values is not the order of a script: <see cref="DdlScript.Stage"/> says when a
/// step runs, in one place for the schema comparison and the designer (R4b).
/// </summary>
public enum DdlStepKind
{
    CreateTable,
    AddColumn,
    ModifyType,
    ModifyDefault,
    ModifyNull,
    RenameColumn,
    DropColumn,
    DropConstraint,

    /// <summary>ALTER TABLE … RENAME CONSTRAINT: the name of a constraint the designer renames.</summary>
    RenameConstraint,

    /// <summary>ALTER INDEX … RENAME TO: the index of a renamed key, which has the key's name.</summary>
    RenameIndex,

    /// <summary>A primary or unique key.</summary>
    AddKey,
    AddCheck,
    AddForeignKey,

    /// <summary>
    /// Drops a check that one added just before replaces, and gives the new one the old name (an edited check): after the
    /// adds, so the old check stays when the new one fails on the rows.
    /// </summary>
    ReplaceConstraint,
    CreateIndex,
    DropIndex,

    /// <summary>COMMENT ON TABLE or COLUMN.</summary>
    Comment,

    /// <summary>
    /// CREATE [OR REPLACE] TRIGGER and the like: a PL/SQL body, ended by a line with only <c>/</c> (WP-26; the schema path
    /// refuses them so far, ADR 0019).
    /// </summary>
    PlSqlObject,

    /// <summary>A note for the user, no statement (a different organization, the definition of a view …); never runs.</summary>
    Hint,
}

/// <summary>What a step risks when it runs.</summary>
public enum DdlRisk
{
    Safe,

    /// <summary>Fails if a value does not fit: too long, NULL for NOT NULL, rows for a mandatory new column, a constraint.</summary>
    MayFailOnValues,

    /// <summary>Works only while the column holds no value (<see cref="Schema.ColumnTypeChange.NeedsEmptyColumn"/>).</summary>
    NeedsEmptyColumn,

    /// <summary>Takes data away: DROP COLUMN.</summary>
    DataLoss,

    /// <summary>
    /// Written for the user to decide, not to run: commented out in a script (a drop or rename whose intent the schema
    /// comparison cannot know, a step Oracle would refuse). Every <see cref="DdlStepKind.Hint"/> is one.
    /// </summary>
    NotExecutable,
}

/// <summary>One statement of a DDL script (schema comparison, designer), or a note in it.</summary>
/// <param name="Object">The table, view or materialized view it is about (orders the steps of a script by object).</param>
/// <param name="Sql">
/// The statement without terminating <c>;</c>, never commented out – a step that must not run says so with
/// <see cref="Risk"/>, and <see cref="DdlScript.Render"/> comments it. For a <see cref="DdlStepKind.Hint"/>: the note.
/// </param>
/// <param name="Warning">Why the step needs care (data loss, may fail on existing data …), as the user reads it in the script; null if none.</param>
public sealed record DdlStep(string Object, DdlStepKind Kind, string Sql, DdlRisk Risk = DdlRisk.Safe, string? Warning = null)
{
    /// <summary>A statement to run: no hint and nothing the user has to decide (R4b: before, the SQL started with <c>--</c>).</summary>
    public bool IsExecutable => Kind != DdlStepKind.Hint && Risk != DdlRisk.NotExecutable;

    /// <summary>A note for the user (it never runs).</summary>
    public static DdlStep Hint(string @object, string text, string? warning) => new(@object, DdlStepKind.Hint, text, DdlRisk.NotExecutable, warning);
}
