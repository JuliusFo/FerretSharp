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

    /// <remarks>Like <see cref="ReadPageAsync"/>: the first page starts a new snapshot in a locked session, later pages stay in it.</remarks>
    public async Task<SqlPage> ReadSqlAsync(QuerySpec query, int skip, int take, CancellationToken cancellationToken)
    {
        if (skip == 0)
        {
            await session.RefreshSnapshotAsync(cancellationToken);
        }

        var stopwatch = Stopwatch.StartNew();
        var (columns, rows, last) = await session.ExecuteReaderAsync(query.Sql, query.Parameters, async (reader, ct) =>
        {
            var oracle = (OracleDataReader)reader;
            var schema = oracle.GetSchemaTable();
            var columns = Enumerable.Range(0, oracle.FieldCount).Select(i => new SqlColumn(oracle.GetName(i), ColumnOf(oracle, i, schema?.Rows[i]))).ToList();
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
                var values = new object?[columns.Count];
                for (var i = 0; i < values.Length; i++)
                {
                    // LOBs as in the table grid: a preview and the length (the session reads them whole – free queries
                    // cannot be rewritten to DBMS_LOB.SUBSTR like generated ones).
                    var value = oracle.IsDBNull(i) ? null : ReadValue(oracle, i, columns[i].Column);
                    values[i] = ColumnCategories.Of(columns[i].Column) is ColumnCategory.Clob or ColumnCategory.Blob ? LobValue.FromContent(value) : value;
                }

                rows.Add(values);
            }

            // One row more tells whether this was the last page.
            return (columns, rows, rows.Count < take || !await reader.ReadAsync(ct));
        }, cancellationToken);

        var snapshot = session.Transaction is { Mode: TransactionMode.ReadOnly } transaction ? transaction.StartedAt : null;
        return new SqlPage(columns, rows, last, stopwatch.Elapsed, snapshot);
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
            throw new PlanUnavailableException(
                "Für den tatsächlichen Plan fehlen Leserechte auf V$SQL und V$SQL_PLAN_STATISTICS_ALL – ein DBA kann sie geben: " +
                "GRANT SELECT_CATALOG_ROLE TO <user> oder GRANT SELECT ON V_$SQL / V_$SQL_PLAN_STATISTICS_ALL TO <user>. " +
                "Der geschätzte Plan geht ohne sie.", ex);
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
        var child = await session.ExecuteReaderAsync(OraclePlans.LatestChild, [new QueryParameter("sql_id", sqlId)], async (reader, ct) =>
            await reader.ReadAsync(ct) ? Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture) : (int?)null, cancellationToken)
            ?? throw new PlanUnavailableException($"Der Cursor {sqlId} ist nicht mehr im Shared Pool – bitte erneut versuchen.");
        var steps = await session.ExecuteReaderAsync(OraclePlans.ActualSteps,
            [new QueryParameter("sql_id", sqlId), new QueryParameter("child", child)],
            (reader, ct) => OraclePlans.ReadAsync(reader, actual: true, ct), cancellationToken);
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
            dataType is "VARCHAR2" or "NVARCHAR2" or "CHAR" or "NCHAR" or "RAW" ? size : null, CharSemantics: false,
            dataType is "NUMBER" ? precision : null, dataType is "NUMBER" or "FLOAT" ? (scale is < 0 ? null : scale) : scale,
            Nullable: schema?["AllowDBNull"] is not false, IsIdentity: false, Default: null, Position: ordinal + 1);
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
