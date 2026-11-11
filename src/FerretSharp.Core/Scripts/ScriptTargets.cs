using FerretSharp.Core.Query;
using FerretSharp.Core.Resources;
using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Scripts;

/// <summary>A column of a table or view.</summary>
public sealed record ColumnRef(TableRef Table, string Column)
{
    public override string ToString() => $"{Table}.{Column}";
}

/// <summary>A script offered for a column (WP-84) and the target it is offered by.</summary>
public sealed record ScriptMatch(ScriptEntry Script, ScriptTarget Target);

/// <summary>
/// Which scripts the cell menu of a column offers (WP-84, decision of the user 2026-11-11): those with a <c>@target</c> on
/// that column, or on a column it refers to – along the relationships FerretSharp knows (declared, manual, by convention,
/// from the C# model), as far as they lead. A script that takes a customer (<c>CUSTOMER.CUSTOMER_ID</c>) so shows on every
/// column holding a customer's key; the script names nothing but the column its values come from.
/// </summary>
public static class ScriptTargets
{
    /// <summary>How many relationships are followed from a column at most (a chain longer than this is no key of a key).</summary>
    public const int MaxSteps = 8;

    /// <summary>
    /// The table a target names in the connection's schema: an own object of that name, else the object a synonym of that
    /// name leads to; null if neither is there (the script is then offered nowhere, without error – it may be for another
    /// product).
    /// </summary>
    public static TableRef? Resolve(string table, IReadOnlyList<TableSummary> tables, string owner)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(tables);
        return tables.FirstOrDefault(t => t.Synonym is null && t.Owner == owner && t.Name == table)?.Ref
            ?? tables.FirstOrDefault(t => t.Synonym?.Name == table)?.Ref;
    }

    /// <summary>
    /// <paramref name="column"/> and every column it refers to: one step per relationship from it, the column at the same
    /// position on the other side (a relationship over two columns maps each of them), on up to <see cref="MaxSteps"/>.
    /// </summary>
    /// <param name="links">The relationships from a table's rows (<see cref="SchemaCache.LinksOf"/>).</param>
    public static IReadOnlySet<ColumnRef> Reached(ColumnRef column, Func<TableRef, IEnumerable<ForeignKeyInfo>> links)
    {
        ArgumentNullException.ThrowIfNull(column);
        ArgumentNullException.ThrowIfNull(links);
        var reached = new HashSet<ColumnRef> { column };
        var current = new List<ColumnRef> { column };
        for (var step = 0; step < MaxSteps && current.Count > 0; step++)
        {
            var next = new List<ColumnRef>();
            foreach (var from in current)
            {
                foreach (var fk in links(from.Table))
                {
                    var index = IndexOf(fk.FromColumns, from.Column);
                    if (index >= 0 && index < fk.ToColumns.Count && reached.Add(new ColumnRef(fk.To, fk.ToColumns[index])))
                    {
                        next.Add(new ColumnRef(fk.To, fk.ToColumns[index]));
                    }
                }
            }

            current = next;
        }

        return reached;
    }

    /// <summary>
    /// The scripts to offer for <paramref name="column"/>: each once, by its first target that the column reaches
    /// (<see cref="Reached"/>), in the order of <paramref name="scripts"/>.
    /// </summary>
    /// <param name="resolve">The table a target names (<see cref="Resolve"/>); null if the schema has none.</param>
    public static IReadOnlyList<ScriptMatch> For(
        ColumnRef column, IEnumerable<ScriptEntry> scripts, Func<string, TableRef?> resolve, Func<TableRef, IEnumerable<ForeignKeyInfo>> links)
    {
        ArgumentNullException.ThrowIfNull(scripts);
        ArgumentNullException.ThrowIfNull(resolve);
        var reached = Reached(column, links);
        var tables = new Dictionary<string, TableRef?>(StringComparer.Ordinal);
        var result = new List<ScriptMatch>();
        foreach (var script in scripts)
        {
            foreach (var target in script.Targets)
            {
                if (!tables.TryGetValue(target.Table, out var table))
                {
                    table = resolve(target.Table);
                    tables[target.Table] = table;
                }

                if (table is not null && reached.Contains(new ColumnRef(table, target.Column)))
                {
                    result.Add(new ScriptMatch(script, target));
                    break;
                }
            }
        }

        return result;
    }

    private static int IndexOf(IReadOnlyList<string> columns, string column)
    {
        for (var i = 0; i < columns.Count; i++)
        {
            if (columns[i] == column)
            {
                return i;
            }
        }

        return -1;
    }
}
