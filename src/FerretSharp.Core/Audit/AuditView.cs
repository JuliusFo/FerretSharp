using FerretSharp.Core.Data;
using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Audit;

/// <summary>How the versions of one base row are found in an audit table (WP-63).</summary>
/// <param name="KeyColumns">The audit table's copies of the base table's primary key: the versions of a row share them.</param>
/// <param name="OrderColumn">The template's <c>@version-order</c> column, oldest version first.</param>
/// <param name="Compared">
/// The copies of base columns compared with the version before: types comparable with <c>&lt;&gt;</c> and read as they are
/// (no LOB, no marker type), not the key itself.
/// </param>
public sealed record AuditVersions(IReadOnlyList<int> KeyColumns, int OrderColumn, IReadOnlyList<int> Compared);

/// <summary>A row of an audit table against the version before it (<see cref="IDataAccess.ReadAuditChangesAsync"/>).</summary>
/// <param name="Key">The audit row's key, as the grid has it.</param>
/// <param name="First">The oldest version of its base row: nothing to compare with.</param>
/// <param name="Changed">The compared columns whose value differs from the version before (NULL-safe); empty for the first.</param>
public sealed record AuditRowChange(RowKey Key, bool First, IReadOnlySet<int> Changed);

/// <summary>
/// What the grid of an audit table shows besides its values (WP-63): the columns changed against the version before and the
/// action of each row – from the connection's template and the base table's key.
/// </summary>
/// <param name="BaseTable">The table the audit table records.</param>
/// <param name="Versions">How versions are compared; null without <c>@version-order</c>, without a base key or without a column to compare.</param>
/// <param name="ActionColumn">The <c>@action</c> column in the audit table; -1 without.</param>
/// <param name="Action">What its values stand for; null without.</param>
public sealed record AuditView(TableRef BaseTable, AuditVersions? Versions, int ActionColumn, AuditAction? Action)
{
    /// <summary>What the row's action column says; null without one or for a value the template does not name.</summary>
    public AuditActionKind? ActionOf(IReadOnlyList<object?> values) =>
        ActionColumn >= 0 && ActionColumn < values.Count ? Action?.KindOf(values[ActionColumn]) : null;

    /// <summary>
    /// The view of <paramref name="audit"/>, the audit table of <paramref name="baseTable"/> with the primary key
    /// <paramref name="baseKey"/>. Null if it shows nothing: no version order to compare by and no action column.
    /// </summary>
    public static AuditView? Of(TableDetails audit, TableRef baseTable, IReadOnlyList<string> baseKey, AuditTemplate template)
    {
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(baseKey);
        ArgumentNullException.ThrowIfNull(template);
        var actionColumn = template.Action is { } action ? audit.IndexOf(action.Column) : -1;
        var versions = VersionsOf(audit, baseKey, template);
        return versions is null && actionColumn < 0
            ? null
            : new AuditView(baseTable, versions, actionColumn, actionColumn < 0 ? null : template.Action);
    }

    private static AuditVersions? VersionsOf(TableDetails audit, IReadOnlyList<string> baseKey, AuditTemplate template)
    {
        var order = template.VersionOrder is { } name ? audit.IndexOf(name) : -1;
        var key = baseKey.Select(audit.IndexOf).ToList();
        if (order < 0 || key.Count == 0 || key.Any(i => i < 0))
        {
            return null;
        }

        var compared = audit.Columns
            .Select((c, i) => (Column: c, Index: i))
            .Where(c => !template.IsOwnColumn(c.Column.Name) && !key.Contains(c.Index)
                        && QueryBuilder.ProjectionOf(c.Column) == Projection.Value && OracleTypes.IsComparable(c.Column.DataType))
            .Select(c => c.Index)
            .ToList();
        return compared.Count == 0 ? null : new AuditVersions(key, order, compared);
    }
}
