using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using FerretSharp.Core.Connections;
using FerretSharp.Core.Data;
using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;
using Oracle.ManagedDataAccess.Client;

namespace FerretSharp.Core.Oracle;

/// <summary>Reads pages of table data. Read-only; writing comes with WP-09 (FlushAsync).</summary>
public sealed class OracleDataAccess(OracleSession session) : IDataAccess
{
    private const int MaxDecimalDigits = 28;

    /// <remarks>
    /// In a locked session the first page of a query starts a new snapshot (new query, filter, sort, F5 – current
    /// data); further pages stay in it, so paging neither repeats nor skips rows when data changes meanwhile.
    /// </remarks>
    public async Task<RowPage> ReadPageAsync(
        TableDetails table,
        IReadOnlyList<FilterCondition> filters,
        IReadOnlyList<SortSpec> sorts,
        PageSpec page,
        CancellationToken cancellationToken)
    {
        var query = QueryBuilder.BuildSelect(table, filters, sorts, page);
        if (page.Offset == 0)
        {
            await session.RefreshSnapshotAsync(cancellationToken);
        }

        var stopwatch = Stopwatch.StartNew();
        var rows = await session.ExecuteReaderAsync(query.Sql, query.Parameters, async (reader, ct) =>
        {
            var result = new List<RowData>(page.Limit);
            var oracle = (OracleDataReader)reader;
            while (await reader.ReadAsync(ct))
            {
                result.Add(ReadRow(oracle, query, table));
            }

            return result;
        }, cancellationToken);

        var snapshot = session.Transaction is { Mode: TransactionMode.ReadOnly } transaction ? transaction.StartedAt : null;
        return new RowPage(rows, rows.Count < page.Limit, stopwatch.Elapsed, snapshot);
    }

    public async Task<long> CountAsync(TableDetails table, IReadOnlyList<FilterCondition> filters, CancellationToken cancellationToken)
    {
        var query = QueryBuilder.BuildCount(table, filters);
        return await session.ExecuteReaderAsync(query.Sql, query.Parameters, async (reader, ct) =>
        {
            await reader.ReadAsync(ct);
            return Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture);
        }, cancellationToken);
    }

    public async Task<LobRead> ReadLobAsync(TableDetails table, RowKey key, int column, CancellationToken cancellationToken)
    {
        var query = QueryBuilder.BuildSelectLob(table, key, column);
        return await session.ExecuteReaderAsync(query.Sql, query.Parameters, async (reader, ct) =>
        {
            if (!await reader.ReadAsync(ct))
            {
                return new LobRead(false, null);
            }

            // The session fetches LOBs completely: CLOB/NCLOB come as string, BLOB as byte[].
            return new LobRead(true, reader.IsDBNull(0) ? null : reader.GetValue(0));
        }, cancellationToken);
    }

    internal static RowData ReadRow(OracleDataReader reader, SelectQuery query, TableDetails table)
    {
        var ordinal = 0;
        string? rowId = null;
        if (query.HasRowId)
        {
            rowId = Convert.ToString(reader.GetValue(ordinal++), CultureInfo.InvariantCulture);
        }

        var values = new object?[query.Columns.Count];
        for (var i = 0; i < query.Columns.Count; i++)
        {
            var column = query.Columns[i];
            switch (column.Projection)
            {
                case Projection.ClobPreview:
                    var preview = reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
                    var clobLength = reader.IsDBNull(ordinal + 1) ? (long?)null : Convert.ToInt64(reader.GetValue(ordinal + 1), CultureInfo.InvariantCulture);
                    values[i] = clobLength is null ? null : new LobValue(preview ?? "", clobLength.Value);
                    ordinal += 2;
                    break;
                case Projection.BlobLength:
                    values[i] = reader.IsDBNull(ordinal) ? null : new LobValue(null, Convert.ToInt64(reader.GetValue(ordinal), CultureInfo.InvariantCulture));
                    ordinal++;
                    break;
                case Projection.NullMarker:
                    values[i] = Convert.ToInt32(reader.GetValue(ordinal), CultureInfo.InvariantCulture) == 0 ? null : new NotNullMarker(column.Column.DataType);
                    ordinal++;
                    break;
                default:
                    values[i] = reader.IsDBNull(ordinal) ? null : ReadValue(reader, ordinal, column.Column);
                    ordinal++;
                    break;
            }
        }

        RowKey key = query.RowKey switch
        {
            RowKeyKind.PrimaryKey => new RowKey.PrimaryKey(
                table.PrimaryKey.Select(name => values[IndexOf(table, name)]).ToList()),
            RowKeyKind.RowId when rowId is not null => new RowKey.RowId(rowId),
            _ => RowKey.None.Instance,
        };

        return new RowData(key, values);
    }

    internal static object? ReadValue(OracleDataReader reader, int ordinal, ColumnInfo column)
    {
        switch (ColumnCategories.Of(column))
        {
            case ColumnCategory.Number when column.DataType is "NUMBER" or "FLOAT" or "INTEGER":
                return ToNumber(NormalizeDecimalSeparator(reader.GetOracleDecimal(ordinal).ToString()));
            case ColumnCategory.Timestamp when column.DataType.Contains("LOCAL", StringComparison.Ordinal):
                return reader.GetOracleTimeStampLTZ(ordinal).Value;
            case ColumnCategory.Timestamp:
                return reader.GetOracleTimeStamp(ordinal).Value;
            case ColumnCategory.TimestampWithTimeZone:
                var tz = reader.GetOracleTimeStampTZ(ordinal);
                return new DateTimeOffset(DateTime.SpecifyKind(tz.Value, DateTimeKind.Unspecified), tz.GetTimeZoneOffset());
            case ColumnCategory.Interval:
                return reader.GetOracleValue(ordinal).ToString();
            default:
                return reader.GetValue(ordinal);
        }
    }

    /// <summary>
    /// <c>OracleDecimal.ToString()</c> uses the current culture ("1,5" on German Windows) but never group separators,
    /// so a comma can only be the decimal separator.
    /// </summary>
    internal static string NormalizeDecimalSeparator(string text) =>
        text.Contains(',', StringComparison.Ordinal) ? text.Replace(',', '.') : text;

    /// <summary>decimal when it holds the exact value, otherwise <see cref="BigNumber"/> (NUMBER has up to 38 digits).</summary>
    internal static object ToNumber(string invariant)
    {
        var digits = invariant.Count(char.IsAsciiDigit) - LeadingZeros(invariant);
        return digits <= MaxDecimalDigits && decimal.TryParse(invariant, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : new BigNumber(invariant);
    }

    private static int LeadingZeros(string invariant)
    {
        var count = 0;
        foreach (var ch in invariant)
        {
            if (ch == '0')
            {
                count++;
            }
            else if (ch is not ('-' or '.'))
            {
                break;
            }
        }

        return count;
    }

    private static int IndexOf(TableDetails table, string column)
    {
        for (var i = 0; i < table.Columns.Count; i++)
        {
            if (table.Columns[i].Name == column)
            {
                return i;
            }
        }

        throw new InvalidOperationException($"Primary key column {column} not in column list.");
    }
}
