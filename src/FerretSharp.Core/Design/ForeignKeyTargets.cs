using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Design;

/// <summary>Why a table is suggested as the target of a new foreign key; lower is more certain.</summary>
public enum ForeignKeyTargetReason
{
    /// <summary>A relationship without constraint on exactly these columns (C# model, manual).</summary>
    Relation,

    /// <summary>The column has the name of the table's single-column primary key (<c>KUNDE_NR</c> → <c>KUNDE(KUNDE_NR)</c>).</summary>
    KeyName,

    /// <summary>The column's name minus <c>_ID</c>/<c>ID</c> names the table, also in a plural form (<c>KUNDE_ID</c> → <c>KUNDEN(ID)</c>).</summary>
    Convention,

    /// <summary>As <see cref="Convention"/>, with a prefix before the table's name (<c>KUNDE_ID</c> → <c>T_KUNDE</c>).</summary>
    PrefixedConvention,
}

/// <summary>A suggested target table with the columns the foreign key would reference, one per key column.</summary>
public sealed record ForeignKeyTarget(TableRef Table, IReadOnlyList<string> Key, ForeignKeyTargetReason Reason);

/// <summary>
/// Suggests the table a new foreign key references, from what is known without a round trip (the schema cache's tables,
/// primary keys and relationships): a relationship without constraint on exactly the key columns first, then – for a single
/// column – a table whose single-column primary key has the column's name, then tables named after the column
/// (<c>KUNDE_ID</c>, <c>KUNDEID</c>, <c>ID_KUNDE</c> → <c>KUNDE</c>, <c>KUNDEN</c>, <c>KUNDES</c> …) with a single-column
/// primary key. Types are not compared – the referenced table's columns are not loaded yet; Oracle refuses a mismatch with
/// ORA-02267 and the plan shows it.
/// </summary>
public static class ForeignKeyTargets
{
    /// <summary>Endings of a plural (German and English) a table name may have where the column has the singular, or the other way round.</summary>
    private static readonly string[] Plurals = ["S", "N", "EN", "E", "ES"];

    /// <summary>
    /// How many tables may share a primary key column's name for it to point to them (<see cref="ForeignKeyTargetReason.KeyName"/>):
    /// a few share one by design (a subtype's key is its base table's), <c>ID</c> is the key of half the schema.
    /// </summary>
    public const int SpecificKeyTables = 3;

    /// <summary>The suggested targets, most certain first; each table once, with its best reason. Empty without columns.</summary>
    /// <param name="table">The table that gets the foreign key.</param>
    /// <param name="columns">Its key columns as chosen.</param>
    /// <param name="tables">The tables that can be referenced (the schema's own tables).</param>
    /// <param name="primaryKeyOf">The primary key columns of a table, in key order; empty without one.</param>
    /// <param name="relations">The relationships from <paramref name="table"/> (<see cref="SchemaCache.OutgoingOf"/>); declared ones are left out.</param>
    public static IReadOnlyList<ForeignKeyTarget> Suggest(
        TableRef table, IReadOnlyList<string> columns, IEnumerable<TableRef> tables, Func<TableRef, IReadOnlyList<string>> primaryKeyOf,
        IEnumerable<ForeignKeyInfo> relations)
    {
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(tables);
        ArgumentNullException.ThrowIfNull(primaryKeyOf);
        ArgumentNullException.ThrowIfNull(relations);
        if (columns.Count == 0)
        {
            return [];
        }

        var found = new List<ForeignKeyTarget>();
        foreach (var relation in relations.Where(r => r.Source != FkSource.Declared && r.Audit is null && r.From == table))
        {
            if (KeyFor(columns, relation) is { } key)
            {
                found.Add(new ForeignKeyTarget(relation.To, key, ForeignKeyTargetReason.Relation));
            }
        }

        if (columns is [var column])
        {
            var stems = Stems(column);
            var keyed = tables.Select(t => (Table: t, Key: primaryKeyOf(t))).Where(t => t.Key.Count == 1).ToList();
            var sameName = keyed.Where(t => Same(t.Key[0], column)).ToList();
            if (sameName.Count <= SpecificKeyTables) // ID and the like name the key of many tables: they say nothing
            {
                found.AddRange(sameName.Where(t => t.Table != table) // the column is this table's own key
                    .Select(t => new ForeignKeyTarget(t.Table, [t.Key[0]], ForeignKeyTargetReason.KeyName)));
            }

            foreach (var (candidate, key) in keyed)
            {
                if (!Same(key[0], column) && ConventionOf(candidate.Name, stems) is { } reason)
                {
                    found.Add(new ForeignKeyTarget(candidate, [key[0]], reason));
                }
            }
        }

        return found
            .OrderBy(t => t.Reason) // stable: the list order within a reason
            .DistinctBy(t => t.Table)
            .ToList();
    }

    /// <summary>
    /// The target to choose without asking: the only one of the most certain reason there is – a relationship always wins;
    /// null if none is, or two tables share the best reason.
    /// </summary>
    public static ForeignKeyTarget? Clear(IReadOnlyList<ForeignKeyTarget> suggestions)
    {
        ArgumentNullException.ThrowIfNull(suggestions);
        if (suggestions.Count == 0)
        {
            return null;
        }

        var best = suggestions[0];
        return suggestions.Count(s => s.Reason == best.Reason) == 1 ? best : null;
    }

    /// <summary>The relationship's referenced columns in the order of <paramref name="columns"/>; null unless it has exactly those columns.</summary>
    private static string[]? KeyFor(IReadOnlyList<string> columns, ForeignKeyInfo relation)
    {
        if (relation.FromColumns.Count != columns.Count || relation.ToColumns.Count != columns.Count)
        {
            return null;
        }

        var key = new string[columns.Count];
        for (var i = 0; i < columns.Count; i++)
        {
            var at = relation.FromColumns.ToList().FindIndex(c => Same(c, columns[i]));
            if (at < 0)
            {
                return null;
            }

            key[i] = relation.ToColumns[at];
        }

        return key;
    }

    /// <summary>
    /// What the column's name says the table is called, without underscores: <c>KUNDE_ID</c>, <c>KUNDEID</c> and <c>ID_KUNDE</c>
    /// give <c>KUNDE</c>; a name without <c>ID</c> at either end gives nothing.
    /// </summary>
    private static List<string> Stems(string column)
    {
        var name = column.Trim().ToUpperInvariant();
        var stems = new List<string>();
        if (name.Length > 2 && name.EndsWith("ID", StringComparison.Ordinal))
        {
            stems.Add(Compact(name[..^2]));
        }

        if (name.Length > 3 && name.StartsWith("ID_", StringComparison.Ordinal))
        {
            stems.Add(Compact(name[3..]));
        }

        return stems.Where(s => s.Length > 0).Distinct().ToList();
    }

    /// <summary>How a table's name matches one of the stems: as it is or in a plural form, else after a prefix that ends with "_".</summary>
    private static ForeignKeyTargetReason? ConventionOf(string tableName, List<string> stems)
    {
        if (stems.Count == 0)
        {
            return null;
        }

        var name = tableName.ToUpperInvariant();
        if (stems.Any(s => Names(Compact(name), s)))
        {
            return ForeignKeyTargetReason.Convention;
        }

        var prefix = name.IndexOf('_', StringComparison.Ordinal);
        return prefix > 0 && prefix < name.Length - 1 && stems.Any(s => Names(Compact(name[(prefix + 1)..]), s))
            ? ForeignKeyTargetReason.PrefixedConvention
            : null;
    }

    /// <summary>The table's compact name is the stem, or one of them has a plural ending the other lacks.</summary>
    private static bool Names(string table, string stem) =>
        table == stem || Plurals.Any(p => table == stem + p || stem == table + p);

    private static string Compact(string name) => name.Replace("_", "", StringComparison.Ordinal);

    /// <summary>Column names as typed in the designer may differ in case from the dictionary's.</summary>
    private static bool Same(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
}
