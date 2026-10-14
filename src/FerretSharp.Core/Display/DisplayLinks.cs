using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Display;

/// <summary>
/// A foreign key column whose target table has a display column (WP-31): its values are looked up there, by the
/// target's key column, and shown beside the column's own values.
/// </summary>
/// <param name="Column">Index of the foreign key column in the table that shows the names.</param>
/// <param name="KeyColumn">Index of the referenced column in <paramref name="Target"/> – its primary key or a unique key.</param>
/// <param name="DisplayColumn">Index of the display column in <paramref name="Target"/>.</param>
/// <param name="SourcePadded">The foreign key column is a CHAR/NCHAR column (its values carry blank padding).</param>
public sealed record DisplayLink(int Column, ForeignKeyInfo ForeignKey, TableDetails Target, int KeyColumn, int DisplayColumn, bool SourcePadded = false)
{
    public ColumnInfo Key => Target.Columns[KeyColumn];

    public ColumnInfo Display => Target.Columns[DisplayColumn];
}

/// <summary>Which foreign key columns of a table get names (WP-31).</summary>
public static class DisplayLinks
{
    /// <summary>
    /// The links of <paramref name="table"/>: per column the first single-column foreign key (declared ones come first)
    /// whose target has a display column, as far as it can be looked up (<see cref="Resolve"/>). Target details come
    /// from the schema cache (loaded once per table).
    /// </summary>
    public static async Task<IReadOnlyList<DisplayLink>> PlanAsync(
        TableDetails table, SchemaCache schema, DisplayColumns columns, CancellationToken cancellationToken)
    {
        var links = new List<DisplayLink>();
        if (columns.Count == 0)
        {
            return links;
        }

        foreach (var (column, fk) in Candidates(table, schema.OutgoingOf(table.Table.Ref), columns))
        {
            if (schema.Find(fk.To) is not { } summary || summary.Kind == TableKind.View)
            {
                continue;
            }

            var target = summary.Ref == table.Table.Ref ? table : await schema.GetDetailsAsync(summary, cancellationToken);
            if (Resolve(table, column, fk, target, columns) is { } link)
            {
                links.Add(link);
            }
        }

        return links;
    }

    /// <summary>Single-column foreign keys to a table with a display column, one per column (the first wins).</summary>
    public static IReadOnlyList<(int Column, ForeignKeyInfo ForeignKey)> Candidates(
        TableDetails table, IEnumerable<ForeignKeyInfo> outgoing, DisplayColumns columns)
    {
        var candidates = new List<(int, ForeignKeyInfo)>();
        var taken = new HashSet<int>();
        foreach (var fk in outgoing)
        {
            if (fk.FromColumns.Count != 1 || columns.ColumnOf(fk.To.Name) is null)
            {
                continue;
            }

            var column = table.IndexOf(fk.FromColumns[0]);
            if (column >= 0 && taken.Add(column))
            {
                candidates.Add((column, fk));
            }
        }

        return candidates;
    }

    /// <summary>
    /// The link of one foreign key, or null if its values cannot be looked up: the target lacks the display column (an
    /// entry for another database), the referenced column is neither the target's primary key nor a unique key (no
    /// index for sure – a full scan per block; views have neither), the display column does not read as text, the
    /// two key columns are of different type families (the value would not bind), or the key is NCHAR (bound without
    /// padding).
    /// </summary>
    public static DisplayLink? Resolve(TableDetails table, int column, ForeignKeyInfo fk, TableDetails target, DisplayColumns columns)
    {
        if (fk.ToColumns.Count != 1 || target.Table.Kind == TableKind.View || columns.IndexIn(target) is not { } display)
        {
            return null;
        }

        var key = target.IndexOf(fk.ToColumns[0]);
        if (key < 0 || !IsUnique(target, fk.ToColumns[0]) || DisplayColumns.NotDisplayableReason(target.Columns[display]) is not null)
        {
            return null;
        }

        // An NCHAR key binds as NCHAR (blank-padded) since review 3.31.0, A18; WP-31 left it out while it bound as NVARCHAR2.
        var family = ColumnCategories.Of(table.Columns[column]);
        if (family != ColumnCategories.Of(target.Columns[key]) || !IsKeyFamily(family))
        {
            return null;
        }

        return new DisplayLink(column, fk, target, key, display, OracleTypes.IsFixedChar(table.Columns[column].DataType));
    }

    private static bool IsUnique(TableDetails target, string column) =>
        target.PrimaryKey is [var pk] && pk == column || target.UniqueKeys.Any(u => u is [var unique] && unique == column);

    /// <summary>Types whose values compare exactly as bind variables (as for FK jumps).</summary>
    private static bool IsKeyFamily(ColumnCategory category) =>
        category is ColumnCategory.Number or ColumnCategory.Text or ColumnCategory.Date or ColumnCategory.Timestamp or ColumnCategory.Raw;
}
