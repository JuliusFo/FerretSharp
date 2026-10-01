using System.Text;
using FerretSharp.Core.Oracle;
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
    : Exception(string.Join(" ", errors.Values))
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

    public static RowKeyKind RowKeyOf(TableDetails table) =>
        table.PrimaryKey.Count > 0 ? RowKeyKind.PrimaryKey
        : table.Table.Kind != TableKind.View ? RowKeyKind.RowId
        : RowKeyKind.None;

    public static Projection ProjectionOf(ColumnInfo column) => ColumnCategories.Of(column) switch
    {
        ColumnCategory.Clob => Projection.ClobPreview,
        ColumnCategory.Blob => Projection.BlobLength,
        ColumnCategory.Long or ColumnCategory.Unsupported => Projection.NullMarker,
        _ => Projection.Value,
    };

    /// <summary>Validation errors keyed by filter index; empty if every enabled filter is usable.</summary>
    public static IReadOnlyDictionary<int, string> Validate(TableDetails table, IReadOnlyList<FilterCondition> filters)
    {
        var errors = new Dictionary<int, string>();
        for (var i = 0; i < filters.Count; i++)
        {
            if (!filters[i].Enabled)
            {
                continue;
            }

            var column = table.Columns.FirstOrDefault(c => c.Name == filters[i].Column);
            var error = column is null ? $"Spalte {filters[i].Column} existiert nicht." : FilterRules.Validate(column, filters[i]);
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

            var conditions = filters.Where(f => f.Enabled).Select(Condition).ToList();
            return conditions.Count == 0 ? "" : "\n WHERE " + string.Join("\n   AND ", conditions);
        }

        public IEnumerable<string> OrderBy(IReadOnlyList<SortSpec> sorts)
        {
            var used = new HashSet<string>(StringComparer.Ordinal);
            foreach (var sort in sorts)
            {
                var column = table.Columns.FirstOrDefault(c => c.Name == sort.Column);
                if (column is null || !ColumnCategories.IsSortable(ColumnCategories.Of(column)))
                {
                    throw new QueryValidationException(new Dictionary<int, string> { [-1] = $"Nach {sort.Column} kann nicht sortiert werden." });
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
                    return Like(c, "%" + EscapeLike(filter.Values[0]) + "%");
                case FilterOperator.StartsWith:
                    return Like(c, EscapeLike(filter.Values[0]) + "%");
                case FilterOperator.EndsWith:
                    return Like(c, "%" + EscapeLike(filter.Values[0]));
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
                        ? $"{c} >= {Add(column, first.Value)} AND {c} < {Add(column, to.Value.AddDays(1))}"
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
            FilterRules.TryParseDate(text, out var value, out var dateOnly);
            return (value, dateOnly);
        }

        /// <summary>Like C#/LINQ "!=": rows where the column is NULL count as "not equal".</summary>
        private static string NotEqualsIncludingNull(ColumnInfo column, string c, string condition) =>
            column.Nullable ? $"({condition} OR {c} IS NULL)" : condition;

        /// <summary>Case-insensitive; the bind value is already escaped and wrapped in wildcards.</summary>
        private string Like(string c, string pattern) =>
            $"UPPER({c}) LIKE UPPER({Add(new QueryParameter(NextName(), pattern, OracleTypeHint.Varchar2))}) ESCAPE '\\'";

        private string Bind(ColumnInfo column, string text)
        {
            var category = ColumnCategories.Of(column);
            if (category == ColumnCategory.Number)
            {
                FilterRules.TryParseNumber(text, out var number);
                return Add(new QueryParameter(NextName(), number, OracleTypeHint.Number));
            }

            var hint = column.DataType is "CHAR" or "NCHAR" ? OracleTypeHint.Char : OracleTypeHint.Varchar2;
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

        private static string EscapeLike(string value) =>
            value.Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("%", "\\%", StringComparison.Ordinal)
                .Replace("_", "\\_", StringComparison.Ordinal);
    }
}
