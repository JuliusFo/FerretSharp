using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Data;

/// <summary>What <see cref="DataRestore.ReadAsync"/> found: the rows to set back, and how many are gone or back as they were.</summary>
/// <param name="Gone">Rows of the diff that no longer are as the diff saw them: a changed or added row deleted since, a deleted one there again.</param>
public sealed record RestoreRead(IReadOnlyList<RestoreRow> Rows, int Gone);

/// <summary>
/// Reads the rows of a data diff to set back (WP-78, ADR 0048) whole: the values at the start point <c>AS OF SCN</c> on the read
/// session (<paramref name="then"/>), the values now on the workspace's session (<paramref name="now"/>) – what the workspace
/// sees is what its pending UPDATE and DELETE check against when written. The diff itself read LOBs only as preview and length.
/// </summary>
public static class DataRestore
{
    public static async Task<RestoreRead> ReadAsync(
        IDataAccess then, IDataAccess now, TableDetails table, IReadOnlyList<DiffRow> rows, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(then);
        ArgumentNullException.ThrowIfNull(now);
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(rows);
        var keyColumns = QueryBuilder.RowKeyOf(table) == RowKeyKind.PrimaryKey ? table.PrimaryKey.Select(table.IndexOf).ToList() : null;
        var thenKeys = rows.Where(r => r.Status != DiffStatus.Added).Select(r => r.Key).ToList();
        var thenRows = await ByKeyAsync(then, table, keyColumns, thenKeys, cancellationToken);
        // Every key now, the deleted ones too: one inserted again since is not brought back a second time.
        var nowRows = await ByKeyAsync(now, table, keyColumns, rows.Select(r => r.Key).ToList(), cancellationToken);

        var result = new List<RestoreRow>();
        var gone = 0;
        foreach (var row in rows)
        {
            var before = thenRows.GetValueOrDefault(row.Key);
            var current = nowRows.GetValueOrDefault(row.Key);
            var usable = row.Status switch
            {
                DiffStatus.Deleted => before is not null && current is null, // inserted again since: nothing to bring back
                DiffStatus.Changed => before is not null && current is not null,
                _ => current is not null,
            };
            if (usable)
            {
                result.Add(new RestoreRow(row.Status, row.Status == DiffStatus.Added ? null : before, row.Status == DiffStatus.Deleted ? null : current));
            }
            else
            {
                gone++;
            }
        }

        return new RestoreRead(result, gone);
    }

    private static async Task<Dictionary<RowKey, RowData>> ByKeyAsync(
        IDataAccess data, TableDetails table, IReadOnlyList<int>? keyColumns, IReadOnlyList<RowKey> keys, CancellationToken cancellationToken)
    {
        if (keys.Count == 0)
        {
            return [];
        }

        IReadOnlyList<IReadOnlyList<object>> values = [.. keys.Select(KeyValues)];
        var read = await data.ReadByKeysAsync(table, keyColumns, values, wholeLobs: true, maxRows: null, cancellationToken);
        return read.GroupBy(r => r.Key).ToDictionary(g => g.Key, g => g.First());
    }

    private static IReadOnlyList<object> KeyValues(RowKey key) => key switch
    {
        RowKey.PrimaryKey primary => [.. primary.Values.Select(v => v ?? throw new InvalidOperationException("A primary key holds no NULL."))],
        RowKey.RowId rowId => [rowId.Value],
        _ => throw new InvalidOperationException("Rows of a diff have a key."),
    };
}
