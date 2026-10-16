using System.Text;
using FerretSharp.Core.Oracle;
using FerretSharp.Core.Resources;
using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Query;

/// <summary>How a column appears in the select list.</summary>
public enum Projection
{
    /// <summary>The column itself.</summary>
    Value,

    /// <summary>Two result columns: first characters and total length (CLOB/NCLOB).</summary>
    ClobPreview,

    /// <summary>Length only (BLOB).</summary>
    BlobLength,

    /// <summary>0 for NULL, 1 otherwise (LONG, XMLTYPE, object types …).</summary>
    NullMarker,
}

public enum RowKeyKind
{
    PrimaryKey,
    RowId,
    None,
}

public sealed record ResultColumn(ColumnInfo Column, Projection Projection);

public sealed record QuerySpec(string Sql, IReadOnlyList<QueryParameter> Parameters);

/// <param name="HasRowId">The first result column is the ROWID (tables and materialized views).</param>
public sealed record SelectQuery(
    string Sql,
    IReadOnlyList<QueryParameter> Parameters,
    IReadOnlyList<ResultColumn> Columns,
    RowKeyKind RowKey,
    bool HasRowId);

/// <summary>Invalid filters or sorts; <see cref="Errors"/> maps the filter index (or -1 for sorting) to a message.</summary>
public sealed class QueryValidationException(IReadOnlyDictionary<int, string> errors)
    : Connections.RefusedException(string.Join(" ", errors.Values))
{
    public IReadOnlyDictionary<int, string> Errors { get; } = errors;
}

/// <summary>
/// Builds the SELECT and COUNT statements for browsing a table. Identifiers come from the data dictionary and are
/// always quoted; values are always bind variables. The result order is deterministic (row key as tiebreaker),
/// so OFFSET/FETCH pages neither overlap nor leave gaps.
/// </summary>
public static class QueryBuilder
{
    public const int ClobPreviewLength = 200;
    public const int MaxInListSize = 1000; // ORA-01795
    private const string Alias = "t";

    /// <summary>The primary key if its values are unique (or the table is a view), otherwise ROWID; views without a key: none.</summary>
    public static RowKeyKind RowKeyOf(TableDetails table) =>
        table.PrimaryKey.Count > 0 && (table.PrimaryKeyEnforced || table.Table.Kind == TableKind.View) ? RowKeyKind.PrimaryKey
        : table.Table.Kind != TableKind.View ? RowKeyKind.RowId
        : RowKeyKind.None;

    public static Projection ProjectionOf(ColumnInfo column) => ColumnCategories.Of(column) switch
    {
        ColumnCategory.Clob => Projection.ClobPreview,
        ColumnCategory.Blob => Projection.BlobLength,
        ColumnCategory.Long or ColumnCategory.Unsupported => Projection.NullMarker,
        _ => Projection.Value,
    };

    /// <summary>
    /// Validation errors keyed by filter index; empty if every filter that filters is usable. Rows of a disabled group are
    /// not checked, like disabled rows (WP-50).
    /// </summary>
    public static IReadOnlyDictionary<int, string> Validate(TableDetails table, IReadOnlyList<FilterCondition> filters)
    {
        var errors = new Dictionary<int, string>();
        var groups = FilterGroups.Of(filters);
        for (var i = 0; i < filters.Count; i++)
        {
            if (!FilterGroups.IsActive(filters[i], groups))
            {
                continue;
            }

            var column = table.Columns.FirstOrDefault(c => c.Name == filters[i].Column);
            var error = column is null ? TextFormat.Format(QueryText.ColumnDoesNotExist, filters[i].Column) : FilterRules.Validate(column, filters[i]);
            if (error is not null)
            {
                errors[i] = error;
            }
        }

        return errors;
    }

    public static SelectQuery BuildSelect(
        TableDetails table, IReadOnlyList<FilterCondition> filters, IReadOnlyList<SortSpec> sorts, PageSpec page)
    {
        var builder = new Builder(table);
        var hasRowId = table.Table.Kind != TableKind.View;
        var columns = table.Columns.Select(c => new ResultColumn(c, ProjectionOf(c))).ToList();
        var select = SelectList(builder, columns, hasRowId);

        var sql = new StringBuilder()
            .Append("SELECT ").AppendJoin(",\n       ", select)
            .Append("\n  FROM ").Append(OracleIdentifier.Qualify(table.Table.Owner, table.Table.Name)).Append(' ').Append(Alias)
            .Append(builder.Where(filters))
            .Append("\n ORDER BY ").AppendJoin(", ", builder.OrderBy(sorts))
            .Append("\nOFFSET :p_offset ROWS FETCH NEXT :p_limit ROWS ONLY")
            .ToString();

        builder.Parameters.Add(new QueryParameter("p_offset", page.Offset, OracleTypeHint.Number));
        builder.Parameters.Add(new QueryParameter("p_limit", page.Limit, OracleTypeHint.Number));
        return new SelectQuery(sql, builder.Parameters, columns, RowKeyOf(table), hasRowId);
    }

    /// <summary>The row a ROWID points to, with the same result columns as <see cref="BuildSelect"/> (reload after INSERT).</summary>
    public static SelectQuery BuildSelectByRowId(TableDetails table, string rowId)
    {
        var builder = new Builder(table);
        var columns = table.Columns.Select(c => new ResultColumn(c, ProjectionOf(c))).ToList();
        var sql = new StringBuilder()
            .Append("SELECT ").AppendJoin(",\n       ", SelectList(builder, columns, hasRowId: true))
            .Append("\n  FROM ").Append(OracleIdentifier.Qualify(table.Table.Owner, table.Table.Name)).Append(' ').Append(Alias)
            .Append("\n WHERE ").Append(Alias).Append(".ROWID = :p_rowid")
            .ToString();
        builder.Parameters.Add(new QueryParameter("p_rowid", rowId, OracleTypeHint.Varchar2));
        return new SelectQuery(sql, builder.Parameters, columns, RowKeyOf(table), HasRowId: true);
    }

    /// <summary>
    /// The whole value of one LOB column of one row (LOB editor, WP-10), found by its row key. The session fetches LOBs
    /// completely; the caller checks the length first (<see cref="Data.LobLimits"/>).
    /// </summary>
    public static QuerySpec BuildSelectLob(TableDetails table, Data.RowKey key, int column)
    {
        var parameters = new List<QueryParameter>();
        var sql = $"SELECT {OracleIdentifier.Quote(table.Columns[column].Name)}\n  FROM {OracleIdentifier.Qualify(table.Table.Owner, table.Table.Name)}"
                  + $"\n WHERE {DmlBuilder.KeyCondition(table, key, parameters)}";
        return new QuerySpec(sql, parameters);
    }

    /// <summary>
    /// Sizes the IN list of a lookup is filled up to (WP-31): every other count would be a statement of its own in the
    /// shared pool (a hard parse per count). The last key is repeated to fill.
    /// </summary>
    public static readonly IReadOnlyList<int> LookupSizes = [1, 10, 50, 100, 500];

    /// <summary>Keys of one lookup at most: one grid block.</summary>
    public static int MaxLookupKeys => LookupSizes[^1];

    /// <summary>
    /// Key and display value of the rows of <paramref name="target"/> whose key column holds one of
    /// <paramref name="keys"/> (display columns, WP-31). The keys are bound with the key column's type; the IN list is
    /// filled up to the next of <see cref="LookupSizes"/>.
    /// </summary>
    /// <exception cref="ArgumentException">No keys, more than <see cref="MaxLookupKeys"/>, or a NULL among them.</exception>
    public static QuerySpec BuildLookup(TableDetails target, int keyColumn, int displayColumn, IReadOnlyList<object> keys)
    {
        if (keys.Count == 0 || keys.Count > MaxLookupKeys || keys.Any(k => k is null))
        {
            throw new ArgumentException($"A lookup takes 1 to {MaxLookupKeys} keys, none of them NULL.", nameof(keys));
        }

        var key = target.Columns[keyColumn];
        var type = OracleTypeMapper.BindType(key);
        var size = LookupSizes.First(s => s >= keys.Count);
        var parameters = Enumerable.Range(0, size)
            .Select(i => new QueryParameter("k" + i.ToString(System.Globalization.CultureInfo.InvariantCulture), keys[Math.Min(i, keys.Count - 1)], type))
            .ToList();
        var keyRef = $"{Alias}.{OracleIdentifier.Quote(key.Name)}";
        var sql = $"SELECT {keyRef}, {Alias}.{OracleIdentifier.Quote(target.Columns[displayColumn].Name)}"
                  + $"\n  FROM {OracleIdentifier.Qualify(target.Table.Owner, target.Table.Name)} {Alias}"
                  + $"\n WHERE {keyRef} IN ({string.Join(", ", parameters.Select(p => ":" + p.Name))})";
        return new QuerySpec(sql, parameters);
    }

    private static List<string> SelectList(Builder builder, IReadOnlyList<ResultColumn> columns, bool hasRowId)
    {
        var select = new List<string>();
        if (hasRowId)
        {
            select.Add($"{Alias}.ROWID");
        }

        foreach (var column in columns)
        {
            var reference = builder.Ref(column.Column);
            select.AddRange(column.Projection switch
            {
                Projection.ClobPreview => [$"DBMS_LOB.SUBSTR({reference}, {ClobPreviewLength}, 1)", $"DBMS_LOB.GETLENGTH({reference})"],
                Projection.BlobLength => [$"DBMS_LOB.GETLENGTH({reference})"],
                Projection.NullMarker => [$"CASE WHEN {reference} IS NULL THEN 0 ELSE 1 END"],
                _ => [reference],
            });
        }

        return select;
    }

    public static QuerySpec BuildCount(TableDetails table, IReadOnlyList<FilterCondition> filters)
    {
        var builder = new Builder(table);
        var sql = $"SELECT COUNT(*)\n  FROM {OracleIdentifier.Qualify(table.Table.Owner, table.Table.Name)} {Alias}{builder.Where(filters)}";
        return new QuerySpec(sql, builder.Parameters);
    }

    private sealed class Builder(TableDetails table)
    {
        public List<QueryParameter> Parameters { get; } = [];

        public string Ref(ColumnInfo column) => $"{Alias}.{OracleIdentifier.Quote(column.Name)}";

        public string Where(IReadOnlyList<FilterCondition> filters)
        {
            var errors = Validate(table, filters);
            if (errors.Count > 0)
            {
                throw new QueryValidationException(errors);
            }

            var conditions = FilterGroups.Terms(filters)
                .Select(term => term.Group is { } group
                    ? GroupCondition(group.Kind, term.Indexes.Select(i => filters[i]).ToList())
                    : Condition(filters[term.Indexes[0]]))
                .ToList();
            return conditions.Count == 0 ? "" : "\n WHERE " + string.Join("\n   AND ", conditions);
        }

        /// <summary>
        /// A filter group (WP-50). Each condition is a single term on its own (none ends in a bare AND or OR), so OR can
        /// join them. "None" follows C#'s <c>!(a || b)</c>: a row whose conditions are unknown (NULL) counts as "does not
        /// apply" and stays – <c>NOT (…)</c> would drop it. Only where the SQL of every condition is never unknown, the plain NOT.
        /// </summary>
        private string GroupCondition(FilterGroupKind kind, IReadOnlyList<FilterCondition> filters)
        {
            var any = string.Join(" OR ", filters.Select(Condition));
            return kind switch
            {
                FilterGroupKind.Any => filters.Count == 1 ? any : $"({any})",
                FilterGroupKind.None when filters.All(NeverUnknown) => $"NOT ({any})",
                FilterGroupKind.None => $"CASE WHEN {any} THEN 1 ELSE 0 END = 0",
                _ => throw new InvalidOperationException($"Unhandled filter group {kind}."),
            };
        }

        /// <summary>
        /// Whether the SQL of the condition itself is always true or false, never unknown: NULL checks, and "≠" on a
        /// nullable column (it says <c>OR c IS NULL</c>). Not the dictionary's NOT NULL: a view over an outer join, or a
        /// constraint ENABLE NOVALIDATE, can hold NULLs in such a column (review of WP-50).
        /// </summary>
        private bool NeverUnknown(FilterCondition filter) =>
            filter.Op is FilterOperator.IsNull or FilterOperator.IsNotNull
            || filter.Op == FilterOperator.NotEquals && table.Columns.First(c => c.Name == filter.Column).Nullable;

        public IEnumerable<string> OrderBy(IReadOnlyList<SortSpec> sorts)
        {
            var used = new HashSet<string>(StringComparer.Ordinal);
            foreach (var sort in sorts)
            {
                var column = table.Columns.FirstOrDefault(c => c.Name == sort.Column);
                if (column is null || !ColumnCategories.IsSortable(ColumnCategories.Of(column)))
                {
                    throw new QueryValidationException(new Dictionary<int, string> { [-1] = TextFormat.Format(QueryText.ColumnNotSortable, sort.Column) });
                }

                if (used.Add(column.Name))
                {
                    yield return Ref(column) + (sort.Descending ? " DESC" : "");
                }
            }

            // Tiebreaker: makes the order total, so OFFSET/FETCH pages are stable.
            switch (RowKeyOf(table))
            {
                case RowKeyKind.PrimaryKey:
                    foreach (var name in table.PrimaryKey.Where(used.Add))
                    {
                        yield return $"{Alias}.{OracleIdentifier.Quote(name)}";
                    }

                    break;
                case RowKeyKind.RowId:
                    yield return $"{Alias}.ROWID";
                    break;
                default:
                    // Views without a key: all sortable columns (fully deterministic unless rows are exact duplicates).
                    foreach (var column in table.Columns.Where(c => ColumnCategories.IsSortable(ColumnCategories.Of(c)) && used.Add(c.Name)))
                    {
                        yield return Ref(column);
                    }

                    if (used.Count == 0)
                    {
                        yield return "1";
                    }

                    break;
            }
        }

        private string Condition(FilterCondition filter)
        {
            var column = table.Columns.First(c => c.Name == filter.Column);
            var category = ColumnCategories.Of(column);
            var c = Ref(column);

            switch (filter.Op)
            {
                case FilterOperator.IsNull:
                    return $"{c} IS NULL";
                case FilterOperator.IsNotNull:
                    return $"{c} IS NOT NULL";
                case FilterOperator.Contains:
                    return Like(column, c, "%" + EscapeLike(filter.Values[0]) + "%");
                case FilterOperator.StartsWith:
                    return Like(column, c, EscapeLike(filter.Values[0]) + "%");
                case FilterOperator.EndsWith:
                    // CHAR/NCHAR values end in their padding: without RTRIM "ends with AB" found no 'XAB  ' (review 3.31.0, A32).
                    return Like(column, OracleTypes.IsFixedChar(column.DataType) ? $"RTRIM({c})" : c, "%" + EscapeLike(filter.Values[0]));
            }

            if (category is ColumnCategory.Date or ColumnCategory.Timestamp or ColumnCategory.TimestampWithTimeZone)
            {
                return DateCondition(column, c, filter);
            }

            switch (filter.Op)
            {
                case FilterOperator.Equals:
                    return $"{c} = {Bind(column, filter.Values[0])}";
                case FilterOperator.NotEquals:
                    return NotEqualsIncludingNull(column, c, $"{c} <> {Bind(column, filter.Values[0])}");
                case FilterOperator.Gt:
                    return $"{c} > {Bind(column, filter.Values[0])}";
                case FilterOperator.Gte:
                    return $"{c} >= {Bind(column, filter.Values[0])}";
                case FilterOperator.Lt:
                    return $"{c} < {Bind(column, filter.Values[0])}";
                case FilterOperator.Lte:
                    return $"{c} <= {Bind(column, filter.Values[0])}";
                case FilterOperator.Between:
                    return $"{c} BETWEEN {Bind(column, filter.Values[0])} AND {Bind(column, filter.Values[1])}";
                case FilterOperator.In:
                    var chunks = filter.Values.Chunk(MaxInListSize)
                        .Select(chunk => $"{c} IN ({string.Join(", ", chunk.Select(v => Bind(column, v)))})")
                        .ToList();
                    return chunks.Count == 1 ? chunks[0] : "(" + string.Join(" OR ", chunks) + ")";
                default:
                    throw new InvalidOperationException($"Unhandled operator {filter.Op}.");
            }
        }

        /// <summary>A date without time means the whole day: "= 1.10." covers 00:00 to before 2.10.</summary>
        private string DateCondition(ColumnInfo column, string c, FilterCondition filter)
        {
            // Only bind what the condition uses: unused bind variables fail with ORA-01036.
            var first = ParseDate(filter.Values[0]);
            switch (filter.Op)
            {
                case FilterOperator.Equals:
                    return DayOrInstant(column, c, first);
                case FilterOperator.NotEquals:
                    return NotEqualsIncludingNull(column, c, first.DateOnly
                        ? $"({c} < {Add(column, first.Value)} OR {c} >= {Add(column, first.Value.AddDays(1))})"
                        : $"{c} <> {Add(column, first.Value)}");
                case FilterOperator.Gt:
                    return first.DateOnly ? $"{c} >= {Add(column, first.Value.AddDays(1))}" : $"{c} > {Add(column, first.Value)}";
                case FilterOperator.Gte:
                    return $"{c} >= {Add(column, first.Value)}";
                case FilterOperator.Lt:
                    return $"{c} < {Add(column, first.Value)}";
                case FilterOperator.Lte:
                    return first.DateOnly ? $"{c} < {Add(column, first.Value.AddDays(1))}" : $"{c} <= {Add(column, first.Value)}";
                case FilterOperator.Between:
                    var to = ParseDate(filter.Values[1]);
                    return to.DateOnly
                        ? $"({c} >= {Add(column, first.Value)} AND {c} < {Add(column, to.Value.AddDays(1))})"
                        : $"{c} BETWEEN {Add(column, first.Value)} AND {Add(column, to.Value)}";
                case FilterOperator.In:
                    var parts = filter.Values.Select(v => DayOrInstant(column, c, ParseDate(v))).ToList();
                    return parts.Count == 1 ? parts[0] : "(" + string.Join(" OR ", parts) + ")";
                default:
                    throw new InvalidOperationException($"Unhandled operator {filter.Op}.");
            }
        }

        private string DayOrInstant(ColumnInfo column, string c, (DateTime Value, bool DateOnly) date) =>
            date.DateOnly
                ? $"({c} >= {Add(column, date.Value)} AND {c} < {Add(column, date.Value.AddDays(1))})"
                : $"{c} = {Add(column, date.Value)}";

        private static (DateTime Value, bool DateOnly) ParseDate(string text)
        {
            ValueInput.TryParseDate(text, out var value, out var dateOnly);
            return (value, dateOnly);
        }

        /// <summary>Like C#/LINQ "!=": rows where the column is NULL count as "not equal".</summary>
        private static string NotEqualsIncludingNull(ColumnInfo column, string c, string condition) =>
            column.Nullable ? $"({condition} OR {c} IS NULL)" : condition;

        /// <summary>
        /// Case-insensitive; the bind value is already escaped and wrapped in wildcards. National for NCHAR/NVARCHAR2: in the
        /// database character set, characters it lacks became "?" and found nothing (review 3.31.0, A18).
        /// </summary>
        private string Like(ColumnInfo column, string c, string pattern) =>
            $"UPPER({c}) LIKE UPPER({Add(new QueryParameter(NextName(), pattern, IsNational(column) ? OracleTypeHint.NVarchar2 : OracleTypeHint.Varchar2))}) ESCAPE '\\'";

        /// <summary>NCHAR and NVARCHAR2 (an NCLOB is never compared with LIKE).</summary>
        private static bool IsNational(ColumnInfo column) => OracleTypes.IsCharacter(column.DataType) && OracleTypes.IsNational(column.DataType);

        private string Bind(ColumnInfo column, string text)
        {
            var category = ColumnCategories.Of(column);
            if (category == ColumnCategory.Number)
            {
                ValueInput.TryParseNumber(text, out var number);
                return Add(new QueryParameter(NextName(), number, OracleTypeHint.Number));
            }

            if (category == ColumnCategory.Raw)
            {
                ValueInput.TryParseHex(text, out var bytes);
                return Add(new QueryParameter(NextName(), bytes, OracleTypeHint.Raw));
            }

            // The bind type of writing too (A18: NVARCHAR2/NCHAR were bound in the database character set here).
            var hint = OracleTypes.IsCharacter(column.DataType) ? OracleTypeMapper.BindType(column) : OracleTypeHint.Varchar2;
            return Add(new QueryParameter(NextName(), text, hint));
        }

        private string Add(ColumnInfo column, DateTime value) =>
            Add(new QueryParameter(
                NextName(),
                value,
                ColumnCategories.Of(column) == ColumnCategory.Date ? OracleTypeHint.Date : OracleTypeHint.TimeStamp));

        private string Add(QueryParameter parameter)
        {
            Parameters.Add(parameter);
            return ":" + parameter.Name;
        }

        private string NextName() => "p" + Parameters.Count;

        private static string EscapeLike(string value) => LikePattern.Escape(value);
    }
}
