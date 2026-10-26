using FerretSharp.Core.Audit;
using FerretSharp.Core.Resources;
using FerretSharp.UI.Resources;

namespace FerretSharp.UI.State;

/// <summary>
/// An outdated audit in the status line above the columns: short parts in one line, the many details folded away. An
/// audit table not kept up for long had a sentence with the same advice for each of its columns, wrapping over half the
/// view (found by the user) – now the columns are counted, and the details list them with the advice once.
/// </summary>
public static class AuditStatusSummary
{
    /// <summary>Up to this many missing columns are named in the line, more are counted.</summary>
    public const int NamedMissing = 3;

    /// <summary>The parts of the line, in order: missing columns, audit columns that cannot take every value, the other reasons.</summary>
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

        parts.AddRange(status.Reasons);
        return parts;
    }

    /// <summary>There is more than the line says: audit columns that cannot take every value, or missing columns only counted.</summary>
    public static bool HasDetails(AuditStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        return status.ColumnIssues.Count > 0 || status.MissingColumns.Count > NamedMissing;
    }

    /// <summary>What a column issue means, short, for its row in the details.</summary>
    public static string Consequence(AuditColumnIssue issue)
    {
        ArgumentNullException.ThrowIfNull(issue);
        return issue.Fit == AuditColumnFit.TooShort ? SchemaViewText.Audit_IssueTooShort : SchemaViewText.Audit_IssueIncompatible;
    }
}
