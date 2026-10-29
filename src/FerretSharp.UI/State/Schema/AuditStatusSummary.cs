using FerretSharp.Core.Audit;
using FerretSharp.Core.Resources;
using FerretSharp.UI.Resources;

namespace FerretSharp.UI.State;

/// <summary>
/// An outdated audit in the status line above the columns: short parts in one line, the many details folded away. An
/// audit table not kept up for long had a sentence with the same advice for each of its columns, wrapping over half the
/// view (found by the user) – now the columns are counted, and the details list them with the advice once. What "Bring
/// up to date" changes without touching values is counted by kind and listed too, instead of "types or comments differ".
/// </summary>
public static class AuditStatusSummary
{
    /// <summary>Up to this many missing columns are named in the line, more are counted.</summary>
    public const int NamedMissing = 3;

    /// <summary>
    /// The parts of the line, in order: missing columns, audit columns that cannot take every value, the changes by kind
    /// (types, NOT NULL, comments), the other reasons.
    /// </summary>
    public static IReadOnlyList<string> Parts(AuditStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        var parts = new List<string>();
        if (status.MissingColumns.Count is > 0 and <= NamedMissing)
        {
            parts.Add(TextFormat.Plural(status.MissingColumns.Count, SchemaViewText.Audit_ColumnsMissingOne, SchemaViewText.Audit_ColumnsMissingOther,
                string.Join(", ", status.MissingColumns)));
        }
        else if (status.MissingColumns.Count > NamedMissing)
        {
            parts.Add(TextFormat.Format(SchemaViewText.Audit_ColumnsMissingCount, status.MissingColumns.Count));
        }

        if (status.ColumnIssues.Count > 0)
        {
            parts.Add(TextFormat.Plural(status.ColumnIssues.Count, SchemaViewText.Audit_ColumnIssuesOne, SchemaViewText.Audit_ColumnIssuesOther));
        }

        Count(status.Changes.Count(c => c.Kind == AuditChangeKind.TypeGrows), SchemaViewText.Audit_ChangesTypeOne, SchemaViewText.Audit_ChangesTypeOther);
        Count(status.Changes.Count(c => c.Kind == AuditChangeKind.BecomesNullable), SchemaViewText.Audit_ChangesNullableOne, SchemaViewText.Audit_ChangesNullableOther);
        Count(status.Changes.Count(c => c.Kind is AuditChangeKind.Comment or AuditChangeKind.TableComment), SchemaViewText.Audit_ChangesCommentOne,
            SchemaViewText.Audit_ChangesCommentOther);
        parts.AddRange(status.Reasons);
        return parts;

        void Count(int count, string one, string other)
        {
            if (count > 0)
            {
                parts.Add(TextFormat.Plural(count, one, other));
            }
        }
    }

    /// <summary>There is more than the line says: audit columns that cannot take every value, changes, or missing columns only counted.</summary>
    public static bool HasDetails(AuditStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        return status.ColumnIssues.Count > 0 || status.Changes.Count > 0 || status.MissingColumns.Count > NamedMissing;
    }

    /// <summary>
    /// Whether "Bring up to date" can do something: not when it would create the missing audit table and audit tables may
    /// not be created – the designer's audit box is locked then (<see cref="AuditDesignRules.CanAudit"/>).
    /// </summary>
    public static bool CanSync(AuditStatus status, bool createAllowed)
    {
        ArgumentNullException.ThrowIfNull(status);
        return AuditDesignRules.CanAudit(!status.TableMissing, createAllowed);
    }

    /// <summary>Where creating audit tables is switched off, named by the settings page's own texts (code review).</summary>
    public static string CreateOffHint => TextFormat.Format(SchemaViewText.Audit_DesignerCreateOff, SettingsViewText.Settings_Title, SettingsViewText.Settings_Audit);

    /// <summary>What a change does, short, for its row in the details.</summary>
    public static string Label(AuditChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        return change.Kind switch
        {
            AuditChangeKind.TypeGrows => SchemaViewText.Audit_ChangeType,
            AuditChangeKind.BecomesNullable => SchemaViewText.Audit_ChangeNullable,
            AuditChangeKind.Comment => SchemaViewText.Audit_ChangeComment,
            _ => SchemaViewText.Audit_ChangeTableComment,
        };
    }

    /// <summary>What a column issue means, short, for its row in the details.</summary>
    public static string Consequence(AuditColumnIssue issue)
    {
        ArgumentNullException.ThrowIfNull(issue);
        return issue.Fit == AuditColumnFit.TooShort ? SchemaViewText.Audit_IssueTooShort : SchemaViewText.Audit_IssueIncompatible;
    }
}
