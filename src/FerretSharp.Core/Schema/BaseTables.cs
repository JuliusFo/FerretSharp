namespace FerretSharp.Core.Schema;

/// <summary>
/// A relationship into a base table that reaches the rows of one of its extension tables too: both share the key
/// (<see cref="BaseTables"/>), so a row referencing the base row references the extension row as well.
/// </summary>
/// <param name="ForeignKey">The relationship into <paramref name="Via"/>.</param>
/// <param name="Via">The base table it points to: the direct one or one further up.</param>
/// <param name="Columns">
/// The extension table's columns holding the key <see cref="ForeignKeyInfo.ToColumns"/> refers to, in that order.
/// </param>
public sealed record InheritedReference(ForeignKeyInfo ForeignKey, TableRef Via, IReadOnlyList<string> Columns);

/// <summary>
/// Base and extension tables (table per type): the primary key of an extension table is also a relationship to the key of
/// its base table, e.g. <c>PKW.FAHRZEUG_ID → FAHRZEUG.FAHRZEUG_ID</c>. What references the base row references the extension row too.
/// Left out are the extension tables themselves – the other ones of the same base (siblings: a vehicle is a car or a truck,
/// rarely both) and the one asked about. Over several levels (an extension of an extension); a table
/// already passed ends the way up.
/// </summary>
public static class BaseTables
{
    /// <summary>
    /// The relationship runs over exactly the primary key of its referencing table: that table extends the referenced one.
    /// </summary>
    public static bool IsExtension(ForeignKeyInfo fk, IReadOnlyList<string> primaryKey) =>
        primaryKey.Count > 0 && fk.From != fk.To && fk.FromColumns.Count == primaryKey.Count
        && fk.FromColumns.Order(StringComparer.Ordinal).SequenceEqual(primaryKey.Order(StringComparer.Ordinal), StringComparer.Ordinal);

    /// <summary>
    /// The relationships into the base tables of <paramref name="table"/> whose referenced columns <paramref name="table"/>
    /// holds as well (a reference to another unique key of the base table is left out: its value is not in the row).
    /// Nearest base table first.
    /// </summary>
    public static IReadOnlyList<InheritedReference> ReferencesOf(
        TableRef table, Func<TableRef, IReadOnlyList<string>> primaryKeyOf,
        Func<TableRef, IEnumerable<ForeignKeyInfo>> outgoing, Func<TableRef, IEnumerable<ForeignKeyInfo>> incoming)
    {
        var references = new List<InheritedReference>();
        var passed = new HashSet<TableRef> { table };
        var key = primaryKeyOf(table);

        // A table on the way up, its primary key, and which column of `table` holds each of its columns.
        var pending = new Queue<(TableRef Table, IReadOnlyList<string> Key, Dictionary<string, string> Columns)>();
        pending.Enqueue((table, key, key.ToDictionary(c => c, c => c, StringComparer.Ordinal)));
        while (pending.TryDequeue(out var current))
        {
            var extensions = outgoing(current.Table)
                .Where(f => IsExtension(f, current.Key) && f.FromColumns.All(current.Columns.ContainsKey))
                .OrderBy(f => f.To.ToString(), StringComparer.Ordinal)
                .ThenBy(f => f.Name, StringComparer.Ordinal);
            foreach (var extension in extensions)
            {
                var baseTable = extension.To;
                if (!passed.Add(baseTable))
                {
                    continue;
                }

                var columns = new Dictionary<string, string>(StringComparer.Ordinal);
                for (var i = 0; i < extension.ToColumns.Count; i++)
                {
                    columns[extension.ToColumns[i]] = current.Columns[extension.FromColumns[i]];
                }

                foreach (var fk in incoming(baseTable))
                {
                    if (!IsExtension(fk, primaryKeyOf(fk.From)) && fk.ToColumns.All(columns.ContainsKey))
                    {
                        references.Add(new InheritedReference(fk, baseTable, [.. fk.ToColumns.Select(c => columns[c])]));
                    }
                }

                pending.Enqueue((baseTable, primaryKeyOf(baseTable), columns));
            }
        }

        return references;
    }
}
