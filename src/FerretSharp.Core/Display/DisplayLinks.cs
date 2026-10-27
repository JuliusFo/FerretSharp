using FerretSharp.Core.Connections;
using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Display;

/// <summary>
/// One part of a name (WP-65): a display column of the target, or – if that column is itself a foreign key to a table with
/// display columns – the name found there (<paramref name="Via"/>, one level deep, never further).
/// </summary>
/// <param name="Column">Index of the display column in the link's target.</param>
/// <param name="Via">The link that looks up this column's value in its own target; null to show the value itself.</param>
public sealed record DisplayPart(int Column, DisplayLink? Via = null);

/// <summary>
/// A foreign key column whose target table has display columns (WP-31): its values are looked up there, by the target's
/// key column, and shown beside the column's own values – the display columns' values joined in their order (WP-65).
/// </summary>
/// <param name="Column">Index of the foreign key column in the table that shows the names.</param>
/// <param name="KeyColumn">Index of the referenced column in <paramref name="Target"/> – its primary key or a unique key.</param>
/// <param name="Parts">The display columns of <paramref name="Target"/>, in order; at least one.</param>
/// <param name="SourcePadded">The foreign key column is a CHAR/NCHAR column (its values carry blank padding).</param>
public sealed record DisplayLink(int Column, ForeignKeyInfo ForeignKey, TableDetails Target, int KeyColumn, IReadOnlyList<DisplayPart> Parts, bool SourcePadded = false)
{
    public ColumnInfo Key => Target.Columns[KeyColumn];

    /// <summary>The display columns as indexes into <see cref="Target"/>'s columns, in order (what a lookup reads).</summary>
    public IReadOnlyList<int> DisplayColumns => [.. Parts.Select(p => p.Column)];

    /// <summary>The display columns by name, as the name joins them (<c>NACHNAME - VORNAME</c>): for headers and notices.</summary>
    public string Label => string.Join(DisplayNames.Separator, Parts.Select(p => Target.Columns[p.Column].Name));

    /// <summary>The same column over the same key to the same parts (also one level deeper) – a plan that changed nothing.</summary>
    public bool SameAs(DisplayLink other) =>
        Column == other.Column && ForeignKey == other.ForeignKey && Target.Table.Ref == other.Target.Table.Ref && KeyColumn == other.KeyColumn
        && Parts.Count == other.Parts.Count
        && Parts.Zip(other.Parts).All(p => p.First.Column == p.Second.Column
                                           && (p.First.Via is null ? p.Second.Via is null : p.Second.Via is { } via && p.First.Via.SameAs(via)));
}

/// <summary>Which foreign key columns of a table get names (WP-31).</summary>
public static class DisplayLinks
{
    /// <summary>
    /// The links of <paramref name="table"/>: per column the first single-column foreign key (declared ones come first)
    /// whose target has display columns, as far as it can be looked up (<see cref="Resolve"/>). A display column of the
    /// target that is itself such a foreign key shows the name of its own target (WP-65, one level). Target details come
    /// from the schema cache (loaded once per table). If the details one level deeper cannot be read, the link shows the
    /// values there – the names of the first level stay (review WP-65); a lost connection still ends the plan.
    /// </summary>
    public static async Task<IReadOnlyList<DisplayLink>> PlanAsync(
        TableDetails table, SchemaCache schema, DisplayColumns columns, CancellationToken cancellationToken)
    {
        var links = new List<DisplayLink>();
        if (columns.Count == 0)
        {
            return links;
        }

        // One level deeper per target once: several foreign keys to one table have the same display columns there.
        var deeper = new Dictionary<TableRef, IReadOnlyDictionary<int, DisplayLink>>();
        foreach (var link in await ResolveAllAsync(table, schema, columns, null, cancellationToken))
        {
            if (!deeper.TryGetValue(link.Target.Table.Ref, out var nested))
            {
                try
                {
                    // Only the display columns are looked at in the target: details of other tables are not loaded for nothing.
                    var found = await ResolveAllAsync(link.Target, schema, columns, [.. link.DisplayColumns], cancellationToken);
                    nested = found.ToDictionary(l => l.Column);
                }
                catch (DatabaseException ex) when (!ex.IsConnectionLost)
                {
                    nested = new Dictionary<int, DisplayLink>();
                }

                deeper[link.Target.Table.Ref] = nested;
            }

            links.Add(Nest(link, nested));
        }

        return links;
    }

    /// <summary>The links of <paramref name="table"/> without a second level; only over <paramref name="only"/> if given.</summary>
    private static async Task<IReadOnlyList<DisplayLink>> ResolveAllAsync(
        TableDetails table, SchemaCache schema, DisplayColumns columns, HashSet<int>? only, CancellationToken cancellationToken)
    {
        var links = new List<DisplayLink>();
        foreach (var (column, fk) in Candidates(table, schema.LinksOf(table.Table.Ref), columns))
        {
            if (only is not null && !only.Contains(column) || schema.Find(fk.To) is not { } summary || summary.Kind == TableKind.View)
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

    /// <summary>
    /// <paramref name="link"/> with each part that is a foreign key of the target in <paramref name="nested"/> (the target's
    /// own links, by column) looked up through it – one level: the nested links keep their plain parts.
    /// </summary>
    public static DisplayLink Nest(DisplayLink link, IReadOnlyDictionary<int, DisplayLink> nested)
    {
        if (!link.Parts.Any(p => nested.ContainsKey(p.Column)))
        {
            return link;
        }

        return link with { Parts = [.. link.Parts.Select(p => nested.TryGetValue(p.Column, out var via) ? p with { Via = Flat(via) } : p)] };
    }

    private static DisplayLink Flat(DisplayLink link) =>
        link.Parts.Any(p => p.Via is not null) ? link with { Parts = [.. link.Parts.Select(p => p with { Via = null })] } : link;

    /// <summary>Single-column foreign keys to a table with display columns, one per column (the first wins).</summary>
    public static IReadOnlyList<(int Column, ForeignKeyInfo ForeignKey)> Candidates(
        TableDetails table, IEnumerable<ForeignKeyInfo> outgoing, DisplayColumns columns)
    {
        var candidates = new List<(int, ForeignKeyInfo)>();
        var taken = new HashSet<int>();
        foreach (var fk in outgoing)
        {
            if (fk.FromColumns.Count != 1 || columns.ColumnsOf(fk.To.Name).Count == 0)
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
    /// The link of one foreign key, or null if its values cannot be looked up: the target has none of the display columns
    /// (an entry for another database) or none that reads as text, the referenced column is neither the target's primary
    /// key nor a unique key (no index for sure – a full scan per block; views have neither), the two key columns are of
    /// different type families (the value would not bind), or the key is NCHAR (bound without padding). Display columns
    /// the target lacks or that do not read as text are left out of the name (WP-65).
    /// </summary>
    public static DisplayLink? Resolve(TableDetails table, int column, ForeignKeyInfo fk, TableDetails target, DisplayColumns columns)
    {
        if (fk.ToColumns.Count != 1 || target.Table.Kind == TableKind.View)
        {
            return null;
        }

        var parts = columns.PartsIn(target);
        var key = target.IndexOf(fk.ToColumns[0]);
        if (parts.Count == 0 || key < 0 || !IsUnique(target, fk.ToColumns[0]))
        {
            return null;
        }

        // An NCHAR key binds as NCHAR (blank-padded) since review 3.31.0, A18; WP-31 left it out while it bound as NVARCHAR2.
        var family = ColumnCategories.Of(table.Columns[column]);
        if (family != ColumnCategories.Of(target.Columns[key]) || !IsKeyFamily(family))
        {
            return null;
        }

        return new DisplayLink(column, fk, target, key, [.. parts.Select(p => new DisplayPart(p))], OracleTypes.IsFixedChar(table.Columns[column].DataType));
    }

    private static bool IsUnique(TableDetails target, string column) =>
        target.PrimaryKey is [var pk] && pk == column || target.UniqueKeys.Any(u => u is [var unique] && unique == column);

    /// <summary>Types whose values compare exactly as bind variables (as for FK jumps).</summary>
    private static bool IsKeyFamily(ColumnCategory category) =>
        category is ColumnCategory.Number or ColumnCategory.Text or ColumnCategory.Date or ColumnCategory.Timestamp or ColumnCategory.Raw;
}
