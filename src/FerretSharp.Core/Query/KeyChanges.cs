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
    /// <summary>The updates among <paramref name="operations"/> that change the primary key of their row.</summary>
    public static IReadOnlyList<PendingOperation> Of(TableDetails table, IEnumerable<PendingOperation> operations) =>
        [.. operations.Where(o => o.Kind == OperationKind.Update && DmlBuilder.ChangesKey(table, o.Values.Keys))];

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
                .Select(o => new RowData(o.Key, [.. Enumerable.Range(0, table.Columns.Count).Select(o.Change.ExpectedOf)]))
                .ToList();
            if (rows.Count > 0)
            {
                jumps.Add(FkNavigation.Incoming(table, rows, fk));
            }
        }

        return jumps;
    }
}
