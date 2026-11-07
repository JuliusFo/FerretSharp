using FerretSharp.Core.Data;
using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Query;

/// <summary>
/// Changed primary keys of loaded rows (ADR 0007 addendum): which pending updates change the key the rows are found by, and
/// which rows may still point to the old keys – along every incoming relationship, declared or not (manual, by convention, of
/// the C# model). Oracle refuses the change for a declared FK with rows behind it (ORA-02292); the others would point nowhere.
/// </summary>
public static class KeyChanges
{
    /// <summary>
    /// The updates among <paramref name="operations"/> that change a column of the primary key – also of one that is not
    /// enforced (rows found by ROWID): relationships without a constraint point to it all the same (review of `small-pk-edit`,
    /// 2). Whether the write changes the row's identity is <see cref="DmlBuilder.ChangesKey"/>.
    /// </summary>
    public static IReadOnlyList<PendingOperation> Of(TableDetails table, IEnumerable<PendingOperation> operations) =>
        [.. operations.Where(o => o.Kind == OperationKind.Update && o.Values.Keys.Any(c => table.PrimaryKey.Contains(table.Columns[c].Name)))];

    /// <summary>
    /// Per incoming relationship that references a changed column: the jump to the rows that hold one of the old values (as the
    /// database holds them in the workspace's transaction), in the order of <paramref name="incoming"/>. A jump that cannot be
    /// built (several rows with a key of more columns, too many values) says why in <see cref="FkJump.Unavailable"/>.
    /// </summary>
    /// <param name="changes">Updates that change a primary key (<see cref="Of"/>).</param>
    public static IReadOnlyList<FkJump> References(TableDetails table, IReadOnlyList<PendingOperation> changes, IEnumerable<ForeignKeyInfo> incoming)
    {
        var jumps = new List<FkJump>();
        foreach (var fk in incoming)
        {
            var rows = changes
                .Where(o => o.Values.Keys.Any(c => fk.ToColumns.Contains(table.Columns[c].Name)))
                .Select(o => new RowData(o.Key, OldValues(table, o)))
                .ToList();
            if (rows.Count > 0)
            {
                jumps.Add(FkNavigation.Incoming(table, rows, fk));
            }
        }

        return jumps;
    }

    /// <summary>
    /// The row's values as the database holds them: the key columns from its row key – as stored, also for a row inserted in
    /// this transaction whose key a trigger or default filled (review of `small-pk-edit`, 6: the typed value was looked for) –,
    /// the others as written or loaded.
    /// </summary>
    private static object?[] OldValues(TableDetails table, PendingOperation operation)
    {
        var key = operation.Key is RowKey.PrimaryKey pk && pk.Values.Count == table.PrimaryKey.Count ? pk.Values : null;
        var values = new object?[table.Columns.Count];
        for (var i = 0; i < values.Length; i++)
        {
            var position = key is null ? -1 : IndexIn(table.PrimaryKey, table.Columns[i].Name);
            values[i] = position >= 0 ? key![position] : operation.Change.ExpectedOf(i);
        }

        return values;
    }

    private static int IndexIn(IReadOnlyList<string> names, string name)
    {
        for (var i = 0; i < names.Count; i++)
        {
            if (names[i] == name)
            {
                return i;
            }
        }

        return -1;
    }
}
