using System.Text;
using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Data;

/// <summary>
/// The primary key of a written row from the values written (WP-85): the write path reads the row back over its ROWID for
/// the key Oracle stored, but a row a policy hides (VPD) cannot be read back. Its ROWID as key found no row after the
/// reload – the grid keys rows by primary key – and the change hung on nothing. A trigger that rewrites the key stays unseen
/// this way; the read back is always tried first.
/// </summary>
public static class WrittenKey
{
    /// <summary>
    /// The key the row of <paramref name="operation"/> has after it was written: an update's old key with its changed key
    /// columns, an insert's key columns; values as a read returns them (CHAR blank-padded). Null if the table is not keyed by
    /// its primary key or an insert leaves a key column to the database (identity, trigger, default).
    /// </summary>
    public static RowKey? Of(TableDetails table, PendingOperation operation)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(operation);
        if (QueryBuilder.RowKeyOf(table) != RowKeyKind.PrimaryKey || operation.Kind == OperationKind.Delete)
        {
            return null;
        }

        var old = operation.Key as RowKey.PrimaryKey;
        var values = new List<object?>(table.PrimaryKey.Count);
        for (var i = 0; i < table.PrimaryKey.Count; i++)
        {
            var index = table.Columns.ToList().FindIndex(c => c.Name == table.PrimaryKey[i]);
            if (index >= 0 && operation.Values.TryGetValue(index, out var written) && written is not null)
            {
                values.Add(AsRead(table.Columns[index], written));
            }
            else if (operation.Kind == OperationKind.Update && old is not null && i < old.Values.Count)
            {
                values.Add(old.Values[i]);
            }
            else
            {
                return null;
            }
        }

        return new RowKey.PrimaryKey(values);
    }

    /// <summary>A value as typed in the form a read returns it: CHAR and NCHAR filled with blanks to their length.</summary>
    private static object AsRead(ColumnInfo column, object value)
    {
        if (value is not string text || !OracleTypes.IsFixedChar(column.DataType) || column.Length is not { } length)
        {
            return value;
        }

        var used = OracleTypes.LengthInBytes(column) ? Encoding.UTF8.GetByteCount(text) : text.Length;
        return used < length ? text + new string(' ', length - used) : text;
    }
}
