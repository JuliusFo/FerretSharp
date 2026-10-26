namespace FerretSharp.Core.Audit;

/// <summary>What bringing the audit up to date changes in an audit table that keeps all its values.</summary>
public enum AuditChangeKind
{
    /// <summary>The audit column grows to take the table's values (longer, more digits, DATE → TIMESTAMP, CHAR semantics).</summary>
    TypeGrows,

    /// <summary>NOT NULL is taken away: the table no longer has the column, or allows NULL in it.</summary>
    BecomesNullable,

    /// <summary>The column comment follows the table's (or the template's).</summary>
    Comment,

    /// <summary>The audit table's comment follows the template.</summary>
    TableComment,
}

/// <summary>
/// One change "Bring up to date" makes to an existing audit table without touching its values – as data, so that the
/// status line can count them and list them under "Details" instead of a vague "types or comments differ" (found by the
/// user). Columns added and the trigger are not here: the status has them apart.
/// </summary>
/// <param name="Column">The audit column; the audit table's name for <see cref="AuditChangeKind.TableComment"/>.</param>
/// <param name="Before">The type as shown, or the comment, before; null for no comment.</param>
/// <param name="After">The same after.</param>
public sealed record AuditChange(string Column, AuditChangeKind Kind, string? Before, string? After);

/// <summary>What the audit planner finds in an existing audit table (<see cref="AuditPlanner.Review"/>).</summary>
/// <param name="Issues">Audit columns that do not take every value whole.</param>
/// <param name="Changes">What bringing it up to date changes without touching values.</param>
public sealed record AuditReview(IReadOnlyList<AuditColumnIssue> Issues, IReadOnlyList<AuditChange> Changes)
{
    public static readonly AuditReview None = new([], []);
}
