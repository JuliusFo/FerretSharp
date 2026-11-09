using FerretSharp.Core.Oracle;
using FerretSharp.Core.Query;
using FerretSharp.Core.Resources;
using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Data;

/// <summary>
/// A row of a data diff to set back to the start point (WP-78): a deleted row with its values then, a changed one with its
/// values then and now, an added one with its values now. Values whole – LOBs as text or bytes, not as preview.
/// </summary>
/// <param name="Then">The row at the start point (deleted, changed); null for an added row.</param>
/// <param name="Now">The row as the workspace reads it now (changed, added); null for a deleted row.</param>
public sealed record RestoreRow(DiffStatus Status, RowData? Then, RowData? Now);

/// <summary>What setting rows back to a start point did (WP-78): rows now pending, and those left as they are with why.</summary>
/// <param name="Restored">Rows with a pending change now: an INSERT, UPDATE or DELETE each.</param>
/// <param name="AlreadyChanged">Rows the tab has changes of already: left alone, the user's work comes first.</param>
/// <param name="NotRestorable">Rows a value of could not be written back (a LONG or XMLTYPE that was not NULL, a number too precise).</param>
public sealed record RestoreResult(int Restored, int AlreadyChanged, int NotRestorable)
{
    /// <summary>The rows that changed in the tab, to draw again: keys of existing rows, ids of new ones.</summary>
    public IReadOnlyList<RowChange> Changes { get; init; } = [];
}

// Setting rows back to a start point (WP-78, ADR 0048): the data diff's rows become pending changes of the tab – a deleted row
// an INSERT, a changed one an UPDATE to its values then, an added one a DELETE. Written the usual way (flush, undo, commit):
// no new write path.
public sealed partial class ChangeTracker
{
    /// <summary>
    /// Why the rows of <paramref name="table"/> cannot be set back at all; null if they can. A deleted row of a table with
    /// <c>GENERATED ALWAYS AS IDENTITY</c> cannot get its old id back (ORA-32795), and a new one would break what refers to it.
    /// </summary>
    public static string? RestoreUnavailable(TableDetails table, bool deletedRows) =>
        table.Table.Kind != TableKind.Table ? OracleText.NotEditableView
        : QueryBuilder.RowKeyOf(table) == RowKeyKind.None ? OracleText.NotEditableNoRowKey
        : deletedRows && table.Columns.FirstOrDefault(c => c.IsIdentity && c.IdentityAlways) is { } identity
            ? TextFormat.Format(DataText.RestoreIdentityAlways, identity.Name)
            : null;

    /// <summary>
    /// Sets <paramref name="rows"/> back to their start point as pending changes, in one step of the edit history (Ctrl+Z takes
    /// them all back). Rows the tab has changes of are left alone. Columns that cannot be written are left out: virtual ones,
    /// identity columns generated always, LONG and object types (a row whose value there was not NULL is left alone).
    /// </summary>
    /// <exception cref="Connections.RefusedException">The table's rows cannot be set back (<see cref="RestoreUnavailable"/>).</exception>
    public RestoreResult Restore(IReadOnlyList<RestoreRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (RestoreUnavailable(Table, rows.Any(r => r.Status == DiffStatus.Deleted)) is { } why)
        {
            throw new Connections.RefusedException(why);
        }

        var before = new List<RowState>();
        var changes = new List<RowChange>();
        var (already, notRestorable) = (0, 0);
        foreach (var row in rows)
        {
            if (row.Now is { } now && _byKey.ContainsKey(now.Key))
            {
                already++;
                continue;
            }

            switch (row.Status)
            {
                case DiffStatus.Deleted when row.Then is { } then:
                    if (!Writable(then, all: true, compareTo: null, out var values))
                    {
                        notRestorable++;
                        continue;
                    }

                    var added = new RowChange(RowKey.None.Instance, null);
                    foreach (var (column, value) in values)
                    {
                        added.Set(column, value, equalsExpected: false);
                    }

                    _newRows.Add(added);
                    before.Add(RowState.Untouched(added));
                    changes.Add(added);
                    break;

                case DiffStatus.Changed when row.Then is { } then && row.Now is { } current:
                    if (!Writable(then, all: false, compareTo: current, out var changed))
                    {
                        notRestorable++;
                        continue;
                    }

                    var change = new RowChange(current.Key, current.Values);
                    foreach (var (column, value) in changed)
                    {
                        change.Set(column, value, OracleTypeMapper.ValuesEqual(value, change.ExpectedOf(column)));
                    }

                    if (!change.HasPending)
                    {
                        continue; // the row is as it was again: nothing to do
                    }

                    before.Add(RowState.Untouched(change));
                    Keep(change);
                    changes.Add(change);
                    break;

                case DiffStatus.Added when row.Now is { } current:
                    var deleted = new RowChange(current.Key, current.Values) { Deleted = ChangeStage.Pending };
                    before.Add(RowState.Untouched(deleted));
                    Keep(deleted);
                    changes.Add(deleted);
                    break;
            }
        }

        Remember(before);
        return new RestoreResult(changes.Count, already, notRestorable) { Changes = changes };
    }

    /// <summary>
    /// The values of <paramref name="row"/> to write back: every writable column (<paramref name="all"/>, an INSERT – NULLs
    /// left out, as a new row of the grid leaves them) or those that differ from <paramref name="compareTo"/> (an UPDATE).
    /// False if a value cannot be written: a LONG or object type that was not NULL, a number beyond decimal.
    /// </summary>
    private bool Writable(RowData row, bool all, RowData? compareTo, out List<(int Column, object? Value)> values)
    {
        values = [];
        for (var i = 0; i < Table.Columns.Count; i++)
        {
            var column = Table.Columns[i];
            var value = row.Values[i];
            if (column.IsVirtual || (column.IsIdentity && column.IdentityAlways))
            {
                continue; // computed by the database; an identity generated always keeps its value
            }

            if (compareTo is not null && OracleTypeMapper.ValuesEqual(value, compareTo.Values[i]))
            {
                continue;
            }

            if (value is NotNullMarker || !OracleTypeMapper.IsEditableValue(value))
            {
                return false;
            }

            if (QueryBuilder.ProjectionOf(column) == Projection.NullMarker)
            {
                continue; // NULL then: a LONG or object type is not compared and left as it is
            }

            if (all && value is null)
            {
                continue;
            }

            values.Add((i, value));
        }

        return true;
    }
}
