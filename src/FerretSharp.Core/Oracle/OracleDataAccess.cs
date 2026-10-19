using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using FerretSharp.Core.Connections;
using FerretSharp.Core.Data;
using FerretSharp.Core.Query;
using FerretSharp.Core.Resources;
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

    /// <remarks>Like <see cref="ReadPageAsync"/>: the first page starts a new snapshot in a locked session, later pages stay in it.</remarks>
    public async Task<SqlPage> ReadSqlAsync(QuerySpec query, int skip, int take, CancellationToken cancellationToken)
    {
        if (skip == 0)
        {
            await session.RefreshSnapshotAsync(cancellationToken);
        }

        var stopwatch = Stopwatch.StartNew();
        // Only a preview of each LOB comes with the row, its length too (review 3.40.0, A12): the page keeps nothing else.
        // Measured against Oracle 23, 500 rows with a 32 KB CLOB: 124 ms whole, 15 ms with the preview – both 2 round trips;
        // without any prefetch every LOB cost two round trips more.
        var (columns, rows, last) = await session.ExecuteReaderAsync(query.Sql, query.Parameters, async (reader, ct) =>
        {
            var oracle = (OracleDataReader)reader;
            var columns = SqlColumnsOf(oracle);
            for (var skipped = 0; skipped < skip; skipped++)
            {
                if (!await reader.ReadAsync(ct))
                {
                    return (columns, new List<IReadOnlyList<object?>>(), true);
                }
            }

            var rows = new List<IReadOnlyList<object?>>(Math.Min(take, 1000));
            while (rows.Count < take && await reader.ReadAsync(ct))
            {
                rows.Add(ReadSqlRow(oracle, columns));
            }

            // One row more tells whether this was the last page.
            return (columns, rows, rows.Count < take || !await reader.ReadAsync(ct));
        }, QueryBuilder.ClobPreviewLength, cancellationToken);

        var snapshot = session.Transaction is { Mode: TransactionMode.ReadOnly } transaction ? transaction.StartedAt : null;
        return new SqlPage(columns, rows, last, stopwatch.Elapsed, snapshot);
    }

    public Task ReadAllAsync(
        TableDetails table,
        IReadOnlyList<FilterCondition> filters,
        IReadOnlyList<SortSpec> sorts,
        RowBlockHandler onBlock,
        CancellationToken cancellationToken)
    {
        var query = QueryBuilder.BuildSelectAll(table, filters, sorts);
        return session.ExecuteStreamingReaderAsync(query.Sql, query.Parameters, (reader, nested, ct) =>
            ReadBlocksAsync(reader, oracle => ReadRow(oracle, query, table), new NestedLookup(nested), onBlock, ct),
            OracleSession.WholeLobs, cancellationToken);
    }

    public Task ReadSqlAllAsync(QuerySpec query, IReadOnlyList<string> columns, RowBlockHandler onBlock, CancellationToken cancellationToken) =>
        session.ExecuteStreamingReaderAsync(query.Sql, query.Parameters, (reader, nested, ct) =>
        {
            var oracle = (OracleDataReader)reader;
            var read = SqlColumnsOf(oracle);
            // The same statement, but the schema may have changed since (a column dropped, SELECT * picking up a new one):
            // the export's columns would no longer fit.
            if (!read.Select(c => c.Name).SequenceEqual(columns, StringComparer.Ordinal))
            {
                throw new RefusedException(DataText.ExportResultChanged);
            }

            return ReadBlocksAsync(reader, r => new RowData(RowKey.None.Instance, ReadSqlRow(r, read)), new NestedLookup(nested), onBlock, ct);
        }, QueryBuilder.ClobPreviewLength, cancellationToken);

    /// <summary>
    /// The rows of an open reader in blocks of <see cref="IDataAccess.BlockSize"/>, fetched in large portions (thousands of
    /// rows over a VPN); stops when <paramref name="onBlock"/> returns false.
    /// </summary>
    private static async Task<bool> ReadBlocksAsync(
        DbDataReader reader, Func<OracleDataReader, RowData> readRow, IRowLookup lookup, RowBlockHandler onBlock, CancellationToken cancellationToken)
    {
        OracleReading.FetchManyRows(reader);
        var oracle = (OracleDataReader)reader;
        var block = new List<RowData>(IDataAccess.BlockSize);
        while (await reader.ReadAsync(cancellationToken))
        {
            block.Add(readRow(oracle));
            if (block.Count < IDataAccess.BlockSize)
            {
                continue;
            }

            if (!await onBlock(block, lookup, cancellationToken))
            {
                return false;
            }

            block = new List<RowData>(IDataAccess.BlockSize);
        }

        return block.Count == 0 || await onBlock(block, lookup, cancellationToken);
    }

    /// <summary>The columns of a free query's result, with their origin (WP-48).</summary>
    private static List<SqlColumn> SqlColumnsOf(OracleDataReader reader)
    {
        var schema = reader.GetSchemaTable();
        return Enumerable.Range(0, reader.FieldCount)
            .Select(i => new SqlColumn(reader.GetName(i), ColumnOf(reader, i, schema?.Rows[i]), OriginOf(schema?.Rows[i]))).ToList();
    }

    /// <summary>
    /// A row of a free query; LOBs as in the table grid: a preview and the length (free queries cannot be rewritten to
    /// DBMS_LOB.SUBSTR like generated ones; the prefetch brings what is needed).
    /// </summary>
    private static object?[] ReadSqlRow(OracleDataReader reader, IReadOnlyList<SqlColumn> columns)
    {
        var values = new object?[columns.Count];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = reader.IsDBNull(i) ? null
                : ColumnCategories.Of(columns[i].Column) switch
                {
                    ColumnCategory.Clob => ClobPreview(reader, i),
                    ColumnCategory.Blob => BlobLength(reader, i),
                    _ => ReadValue(reader, i, columns[i].Column),
                };
        }

        return values;
    }

    public async Task<ExecutionPlan> ExplainActualAsync(QuerySpec query, bool wholeResult, CancellationToken cancellationToken)
    {
        // Rights first: running a long query to find out afterwards that the plan cannot be read would waste the wait.
        try
        {
            await session.ExecuteReaderAsync(OraclePlans.RightsProbe, [], async (reader, ct) => await reader.ReadAsync(ct), cancellationToken);
        }
        catch (DatabaseException ex) when (ex.IsAny(OracleErrorCodes.MissingRights))
        {
            throw new PlanUnavailableException(OracleText.ActualPlanNoRights, ex);
        }

        var hinted = Plans.WithStatistics(query.Sql);
        await session.RefreshSnapshotAsync(cancellationToken); // a fresh run, like a new query in the grid
        var fetched = await session.ExecuteReaderAsync(hinted, query.Parameters, async (reader, ct) =>
        {
            long rows = 0;
            while ((wholeResult || rows < IDataAccess.ActualPlanPageSize) && await reader.ReadAsync(ct))
            {
                rows++;
            }

            return rows;
        }, cancellationToken);

        var sqlId = Plans.SqlId(hinted);
        var (child, steps) = await OraclePlans.ReadActualAsync(session, sqlId, cancellationToken);
        return new ExecutionPlan(PlanSource.Actual, hinted, steps, sqlId, child, wholeResult, fetched);
    }

    /// <summary>
    /// A column of a free query as <see cref="ColumnInfo"/>, so it formats like a table column: the Oracle type name
    /// from the driver's type, precision and scale from the reader's schema.
    /// </summary>
    internal static ColumnInfo ColumnOf(OracleDataReader reader, int ordinal, System.Data.DataRow? schema)
    {
        int? Int(string name) => schema?[name] is int value ? value : schema?[name] is short small ? small : null;
        var precision = Int("NumericPrecision");
        var scale = Int("NumericScale");
        var size = Int("ColumnSize");
        var dataType = DataTypeName(reader.GetDataTypeName(ordinal), scale);
        // NUMBER without precision reports 0 (or 38) and scale -127/0: unknown, as ALL_TAB_COLUMNS would have it.
        if (dataType == "NUMBER" && precision is 0 or 38)
        {
            precision = null;
            scale = scale is 0 ? 0 : null;
        }

        return new ColumnInfo(reader.GetName(ordinal), dataType,
            OracleTypes.HasLength(dataType) ? size : null, CharSemantics: false,
            dataType is "NUMBER" ? precision : null, dataType is "NUMBER" or "FLOAT" ? (scale is < 0 ? null : scale) : scale,
            Nullable: schema?["AllowDBNull"] is not false, IsIdentity: false, Default: null, Position: ordinal + 1);
    }

    /// <summary>
    /// The origin the driver reports (WP-48): read on the client from the statement, without <c>CommandBehavior.KeyInfo</c>
    /// (that costs two round trips more and only adds whether a column is a key). Null for an expression.
    /// </summary>
    internal static SqlColumnOrigin? OriginOf(System.Data.DataRow? schema)
    {
        string? Text(string name) => schema?.Table.Columns.Contains(name) == true && schema[name] is string { Length: > 0 } text ? text : null;
        return Text("BaseColumnName") is { } column ? new SqlColumnOrigin(Text("BaseSchemaName"), Text("BaseTableName"), column) : null;
    }

    /// <summary>ODP.NET type names (<c>Decimal</c>, <c>TimeStampTZ</c>) as Oracle names (<c>NUMBER</c>, <c>TIMESTAMP(6) WITH TIME ZONE</c>).</summary>
    internal static string DataTypeName(string driverType, int? scale) => driverType switch
    {
        "Decimal" or "Int16" or "Int32" or "Int64" or "Byte" or "Double" or "Single" => "NUMBER",
        "BinaryDouble" => "BINARY_DOUBLE",
        "BinaryFloat" => "BINARY_FLOAT",
        "Varchar2" => "VARCHAR2",
        "NVarchar2" => "NVARCHAR2",
        "Char" => "CHAR",
        "NChar" => "NCHAR",
        "Date" => "DATE",
        "TimeStamp" => $"TIMESTAMP({scale ?? 6})",
        "TimeStampTZ" => $"TIMESTAMP({scale ?? 6}) WITH TIME ZONE",
        "TimeStampLTZ" => $"TIMESTAMP({scale ?? 6}) WITH LOCAL TIME ZONE",
        "IntervalDS" => "INTERVAL DAY TO SECOND",
        "IntervalYM" => "INTERVAL YEAR TO MONTH",
        "Raw" => "RAW",
        "LongRaw" => "LONG RAW",
        "Long" => "LONG",
        "Clob" => "CLOB",
        "NClob" => "NCLOB",
        "Blob" => "BLOB",
        "Boolean" => "BOOLEAN",
        "XmlType" => "XMLTYPE",
        _ => driverType.ToUpperInvariant(),
    };

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

    public async Task<IReadOnlyList<LookupRow>> LookupAsync(
        TableDetails target, int keyColumn, int displayColumn, IReadOnlyList<object> keys, CancellationToken cancellationToken)
    {
        var query = QueryBuilder.BuildLookup(target, keyColumn, displayColumn, keys);
        return await session.ExecuteReaderAsync(query.Sql, query.Parameters, LookupReader(target, keyColumn, displayColumn), cancellationToken);
    }

    private static Func<DbDataReader, CancellationToken, Task<IReadOnlyList<LookupRow>>> LookupReader(TableDetails target, int keyColumn, int displayColumn)
    {
        var key = target.Columns[keyColumn];
        var display = target.Columns[displayColumn];
        return async (reader, ct) =>
        {
            var oracle = (OracleDataReader)reader;
            var rows = new List<LookupRow>();
            while (await reader.ReadAsync(ct))
            {
                rows.Add(new LookupRow(ReadValue(oracle, 0, key)!, reader.IsDBNull(1) ? null : ReadValue(oracle, 1, display)));
            }

            return rows;
        };
    }

    /// <summary>Lookups while a whole read is open (WP-54): on its connection, inside the gate it holds.</summary>
    private sealed class NestedLookup(OracleSession.NestedReader nested) : IRowLookup
    {
        public Task<IReadOnlyList<LookupRow>> LookupAsync(
            TableDetails target, int keyColumn, int displayColumn, IReadOnlyList<object> keys, CancellationToken cancellationToken)
        {
            var query = QueryBuilder.BuildLookup(target, keyColumn, displayColumn, keys);
            return nested.ReadAsync(query.Sql, query.Parameters, LookupReader(target, keyColumn, displayColumn), cancellationToken);
        }
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

    /// <summary>
    /// The first characters and the length of a CLOB/NCLOB, from what the prefetch brought (no round trip).
    /// <see cref="OracleClob.Length"/> counts bytes of UTF-16, two per character.
    /// </summary>
    private static LobValue ClobPreview(OracleDataReader reader, int ordinal)
    {
        using var clob = reader.GetOracleClob(ordinal);
        var characters = clob.Length / 2;
        var buffer = new char[(int)Math.Min(characters, QueryBuilder.ClobPreviewLength)];
        var read = buffer.Length == 0 ? 0 : clob.Read(buffer, 0, buffer.Length);
        return new LobValue(new string(buffer, 0, read), characters);
    }

    private static LobValue BlobLength(OracleDataReader reader, int ordinal)
    {
        using var blob = reader.GetOracleBlob(ordinal);
        return new LobValue(null, blob.Length);
    }

    internal static object? ReadValue(OracleDataReader reader, int ordinal, ColumnInfo column)
    {
        switch (ColumnCategories.Of(column))
        {
            case ColumnCategory.Number when OracleTypes.IsDecimalNumber(column.DataType):
                return ToNumber(NormalizeDecimalSeparator(reader.GetOracleDecimal(ordinal).ToString()));
            case ColumnCategory.Timestamp:
                return reader.GetOracleTimeStamp(ordinal).Value;
            case ColumnCategory.TimestampWithTimeZone when OracleTypeMapper.IsLocalTimeZone(column):
                // Stored in the database's time zone, read in the session's: a time without offset, as Oracle shows it.
                return reader.GetOracleTimeStampLTZ(ordinal).Value;
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

    /// <summary>
    /// decimal when it holds the exact value, otherwise <see cref="BigNumber"/>: NUMBER has up to 38 significant digits, and
    /// a tiny value may have more than the 28 decimal places of decimal (0.000…0001 would round to 0).
    /// </summary>
    internal static object ToNumber(string invariant)
    {
        var digits = invariant.Count(char.IsAsciiDigit) - LeadingZeros(invariant);
        var places = invariant.IndexOf('.', StringComparison.Ordinal) is >= 0 and var dot ? invariant.Length - dot - 1 : 0;
        return digits <= MaxDecimalDigits && places <= MaxDecimalDigits && !invariant.Contains('E', StringComparison.OrdinalIgnoreCase)
               && decimal.TryParse(invariant, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
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

    private static int IndexOf(TableDetails table, string column) =>
        table.IndexOf(column) is >= 0 and var index ? index : throw new InvalidOperationException($"Primary key column {column} not in column list.");
}
