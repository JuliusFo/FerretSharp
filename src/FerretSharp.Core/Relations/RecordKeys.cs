using FerretSharp.Core.Data;
using FerretSharp.Core.Oracle;
using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Relations;

/// <summary>
/// The start rows of a record diagram as text for the workspace file (WP-60): a primary key as the filter texts of its
/// values (<see cref="FkNavigation.FilterValue"/>: invariant numbers, ISO dates, hex for RAW), which parse back exactly; a
/// ROWID as it is. A JSON <c>object</c> would come back as <c>JsonElement</c>.
/// </summary>
public static class RecordKeys
{
    /// <summary>The key as text, or null if a value has no exact text (a float, an interval …): that row is not saved.</summary>
    public static IReadOnlyList<string>? ToText(TableDetails table, RowKey key)
    {
        switch (key)
        {
            case RowKey.RowId rowId:
                return [rowId.Value];
            case RowKey.PrimaryKey pk when pk.Values.Count == table.PrimaryKey.Count:
                var texts = new List<string>(pk.Values.Count);
                for (var i = 0; i < pk.Values.Count; i++)
                {
                    var index = table.IndexOf(table.PrimaryKey[i]);
                    if (index < 0 || pk.Values[i] is not { } value || FkNavigation.FilterValue(table.Columns[index], value) is not { } text)
                    {
                        return null;
                    }

                    texts.Add(text);
                }

                return texts;
            default:
                return null;
        }
    }

    /// <summary>
    /// The key of a saved text, or null if it no longer fits the table: another kind of key (a primary key added or dropped
    /// since), another number of columns, a value the column type no longer takes.
    /// </summary>
    public static RowKey? FromText(TableDetails table, bool rowId, IReadOnlyList<string> texts)
    {
        var kind = QueryBuilder.RowKeyOf(table);
        if (rowId)
        {
            return kind == RowKeyKind.RowId && texts is [{ Length: > 0 } value] ? new RowKey.RowId(value) : null;
        }

        if (kind != RowKeyKind.PrimaryKey || texts.Count != table.PrimaryKey.Count)
        {
            return null;
        }

        var values = new object?[texts.Count];
        for (var i = 0; i < texts.Count; i++)
        {
            var index = table.IndexOf(table.PrimaryKey[i]);
            if (index < 0 || OracleTypeMapper.Parse(table.Columns[index], texts[i]) is not { Error: null, Value: { } parsed })
            {
                return null;
            }

            values[i] = parsed;
        }

        return new RowKey.PrimaryKey(values);
    }
}
