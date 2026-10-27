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

    /// <summary>The select list of <see cref="BuildSelectStar"/>, also for showing it (settings).</summary>
    public const string StarSelect = "SELECT " + Alias + ".*";

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
        TableDetails table, IReadOnlyList<FilterCondition> filters, IReadOnlyList<SortSpec> sorts, PageSpec page) =>
        Select(table, filters, sorts, page);

    /// <summary>
    /// Every row of <see cref="BuildSelect(TableDetails, IReadOnlyList{FilterCondition}, IReadOnlyList{SortSpec}, PageSpec)"/>
    /// in the same order, without paging: the export of all rows of a filter (WP-54) reads them through one cursor –
    /// a large OFFSET gets slower page by page.
    /// </summary>
    public static SelectQuery BuildSelectAll(TableDetails table, IReadOnlyList<FilterCondition> filters, IReadOnlyList<SortSpec> sorts) =>
        Select(table, filters, sorts, page: null);

    /// <summary>
    /// The filter as a statement to read and go on with in the SQL editor ("SQL" in the filter bar): <c>SELECT t.*</c> with
    /// the same conditions and binds, ordered by the sorts and the row key – without ROWID in the select list, LOB
    /// projections and page. A table without primary key still ends its ORDER BY with <c>t.ROWID</c>: the SQL editor reads
    /// later pages by running the statement again, an order that is not total would repeat or skip rows (review of the
    /// change, decision of the user). A view without key is ordered by the sorts only.
    /// </summary>
    public static QuerySpec BuildSelectStar(TableDetails table, IReadOnlyList<FilterCondition> filters, IReadOnlyList<SortSpec> sorts)
    {
        var builder = new Builder(table);
        var sql = new StringBuilder()
            .Append(StarSelect)
            .Append("\n  FROM ").Append(OracleIdentifier.Qualify(table.Table.Owner, table.Table.Name)).Append(' ').Append(Alias)
            .Append(builder.Where(filters));
        var order = builder.OrderBy(sorts, keylessViewByAllColumns: false).ToList();
        if (order.Count > 0)
        {
            sql.Append("\n ORDER BY ").AppendJoin(", ", order);
        }

        return new QuerySpec(sql.ToString(), builder.Parameters);
    }

    /// <summary>
    /// The values of columns for every row of <see cref="BuildSelectAll"/> (copy the values of a column, WP-57, or of marked
    /// columns, WP-58): in the grid's order, or each value once (<paramref name="distinct"/>, for an IN list – one column only)
    /// ordered by the value – with DISTINCT, Oracle only sorts by what is selected. Only columns read as they are
    /// (<see cref="Projection.Value"/>).
    /// </summary>
    /// <param name="columns">Indexes into the table's columns, in the order of the result.</param>
    /// <exception cref="ArgumentException">
    /// A column is a LOB, LONG or a type read only as a marker; no column; DISTINCT over several columns.
    /// </exception>
    public static SelectQuery BuildColumnValues(
        TableDetails table, IReadOnlyList<int> columns, IReadOnlyList<FilterCondition> filters, IReadOnlyList<SortSpec> sorts, bool distinct)
    {
        if (columns.Count == 0 || (distinct && columns.Count > 1))
        {
            throw new ArgumentException("One column for DISTINCT, at least one otherwise.", nameof(columns));
        }

        var infos = columns.Select(c => table.Columns[c]).ToList();
        if (infos.FirstOrDefault(i => ProjectionOf(i) != Projection.Value) is { } info)
        {
            throw new ArgumentException($"Column {info.Name} ({info.DataType}) is not read as a value.", nameof(columns));
        }

        var builder = new Builder(table);
        var refs = infos.Select(builder.Ref).ToList();
        var sql = new StringBuilder()
            .Append(distinct ? "SELECT DISTINCT " : "SELECT ").AppendJoin(", ", refs)
            .Append("\n  FROM ").Append(OracleIdentifier.Qualify(table.Table.Owner, table.Table.Name)).Append(' ').Append(Alias)
            .Append(builder.Where(filters))
            .Append("\n ORDER BY ").AppendJoin(", ", distinct ? refs : builder.OrderBy(sorts));
        return new SelectQuery(sql.ToString(), builder.Parameters, [.. infos.Select(i => new ResultColumn(i, Projection.Value))], RowKeyKind.None, HasRowId: false);
    }

    private static SelectQuery Select(
        TableDetails table, IReadOnlyList<FilterCondition> filters, IReadOnlyList<SortSpec> sorts, PageSpec? page)
    {
        var builder = new Builder(table);
        var hasRowId = table.Table.Kind != TableKind.View;
        var columns = table.Columns.Select(c => new ResultColumn(c, ProjectionOf(c))).ToList();
        var select = SelectList(builder, columns, hasRowId);

        var sql = new StringBuilder()
            .Append("SELECT ").AppendJoin(",\n       ", select)
            .Append("\n  FROM ").Append(OracleIdentifier.Qualify(table.Table.Owner, table.Table.Name)).Append(' ').Append(Alias)
            .Append(builder.Where(filters))
            .Append("\n ORDER BY ").AppendJoin(", ", builder.OrderBy(sorts));
        if (page is not null)
        {
            sql.Append("\nOFFSET :p_offset ROWS FETCH NEXT :p_limit ROWS ONLY");
            builder.Parameters.Add(new QueryParameter("p_offset", page.Offset, OracleTypeHint.Number));
            builder.Parameters.Add(new QueryParameter("p_limit", page.Limit, OracleTypeHint.Number));
        }

        return new SelectQuery(sql.ToString(), builder.Parameters, columns, RowKeyOf(table), hasRowId);
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
    /// Key and display values of the rows of <paramref name="target"/> whose key column holds one of
    /// <paramref name="keys"/> (display columns, WP-31; several in their order since WP-65). The keys are bound with the key
    /// column's type; the IN list is filled up to the next of <see cref="LookupSizes"/>.
    /// </summary>
    /// <exception cref="ArgumentException">No display column, no keys, more than <see cref="MaxLookupKeys"/>, or a NULL among them.</exception>
    public static QuerySpec BuildLookup(TableDetails target, int keyColumn, IReadOnlyList<int> displayColumns, IReadOnlyList<object> keys)
    {
        if (keys.Count == 0 || keys.Count > MaxLookupKeys || keys.Any(k => k is null))
        {
            throw new ArgumentException($"A lookup takes 1 to {MaxLookupKeys} keys, none of them NULL.", nameof(keys));
        }

        if (displayColumns.Count == 0)
        {
            throw new ArgumentException("A lookup reads at least one display column.", nameof(displayColumns));
        }

        var key = target.Columns[keyColumn];
        var type = OracleTypeMapper.BindType(key);
        var size = LookupSizes.First(s => s >= keys.Count);
        var parameters = Enumerable.Range(0, size)
            .Select(i => new QueryParameter("k" + i.ToString(System.Globalization.CultureInfo.InvariantCulture), keys[Math.Min(i, keys.Count - 1)], type))
            .ToList();
        var keyRef = $"{Alias}.{OracleIdentifier.Quote(key.Name)}";
        var display = string.Join(", ", displayColumns.Select(c => $"{Alias}.{OracleIdentifier.Quote(target.Columns[c].Name)}"));
        var sql = $"SELECT {keyRef}, {display}"
                  + $"\n  FROM {OracleIdentifier.Qualify(target.Table.Owner, target.Table.Name)} {Alias}"
                  + $"\n WHERE {keyRef} IN ({string.Join(", ", parameters.Select(p => ":" + p.Name))})";
        return new QuerySpec(sql, parameters);
    }

    /// <summary>
    /// The rows of <paramref name="table"/> whose <paramref name="keyColumns"/> hold one of <paramref name="keys"/> – the rows a
    /// relationship leads to (WP-42, <see cref="Relations.RowTraversal"/>). One column: <c>t."C" IN (:k0, …)</c>; several: a tuple
    /// list <c>(t."A", t."B") IN ((:k0_0, :k0_1), …)</c>, which the filter model does not have; no columns: by ROWID. The list is
    /// filled up to the next of <see cref="LookupSizes"/> with the last key (one statement per size in the shared pool),
    /// ordered by the row key.
    /// </summary>
    /// <param name="keyColumns">Indexes into the table's columns; null for ROWIDs (one text per key).</param>
    /// <param name="wholeLobs">
    /// LOB columns as whole values (a copy writes them) instead of preview and length; LONG and types read only as a marker
    /// stay markers either way.
    /// </param>
    /// <exception cref="ArgumentException">No keys, more than <see cref="MaxLookupKeys"/>, a key of the wrong size or with a NULL.</exception>
    public static SelectQuery BuildRowsByKeys(TableDetails table, IReadOnlyList<int>? keyColumns, IReadOnlyList<IReadOnlyList<object>> keys, bool wholeLobs)
    {
        var width = keyColumns?.Count ?? 1;
        if (keys.Count == 0 || keys.Count > MaxLookupKeys || width == 0
            || keys.Any(k => k.Count != width || k.Any(v => v is null)))
        {
            throw new ArgumentException($"A lookup takes 1 to {MaxLookupKeys} keys of {width} values, none of them NULL.", nameof(keys));
        }

        var builder = new Builder(table);
        var hasRowId = table.Table.Kind != TableKind.View;
        var columns = table.Columns.Select(c => new ResultColumn(c, wholeLobs && ProjectionOf(c) is Projection.ClobPreview or Projection.BlobLength
            ? Projection.Value
            : ProjectionOf(c))).ToList();
        var sql = new StringBuilder()
            .Append("SELECT ").AppendJoin(",\n       ", SelectList(builder, columns, hasRowId))
            .Append("\n  FROM ").Append(OracleIdentifier.Qualify(table.Table.Owner, table.Table.Name)).Append(' ').Append(Alias)
            .Append("\n WHERE ").Append(KeyIn(table, keyColumns, keys, builder.Parameters))
            .Append("\n ORDER BY ").AppendJoin(", ", builder.OrderBy([]))
            .ToString();
        return new SelectQuery(sql, builder.Parameters, columns, RowKeyOf(table), hasRowId);
    }

    /// <summary>
    /// <c>t."C" IN (:k0, …)</c>, <c>(t."A", t."B") IN ((:k0_0, :k0_1), …)</c> or <c>t.ROWID IN (…)</c> for
    /// <see cref="BuildRowsByKeys"/>; values bound with the column's type, filled up to the next of <see cref="LookupSizes"/>.
    /// </summary>
    /// <param name="prefix">Starts the bind names, so two lists can share a statement.</param>
    /// <param name="alias">The table or inline view the columns belong to.</param>
    /// <param name="rowId">How <paramref name="alias"/> names the ROWID (an inline view selects it under a name of its own).</param>
    internal static string KeyIn(
        TableDetails table, IReadOnlyList<int>? keyColumns, IReadOnlyList<IReadOnlyList<object>> keys, List<QueryParameter> parameters,
        string prefix = "k", string alias = Alias, string rowId = "ROWID")
    {
        var size = LookupSizes.First(s => s >= keys.Count);
        var invariant = System.Globalization.CultureInfo.InvariantCulture;
        if (keyColumns is null)
        {
            var rowIds = Enumerable.Range(0, size).Select(i =>
            {
                var name = prefix + i.ToString(invariant);
                parameters.Add(new QueryParameter(name, keys[Math.Min(i, keys.Count - 1)][0], OracleTypeHint.Varchar2));
                return ":" + name;
            });
            return $"{alias}.{rowId} IN ({string.Join(", ", rowIds)})";
        }

        var columns = keyColumns.Select(c => table.Columns[c]).ToList();
        var types = columns.Select(OracleTypeMapper.BindType).ToList();
        var tuples = Enumerable.Range(0, size).Select(i =>
        {
            var key = keys[Math.Min(i, keys.Count - 1)];
            var binds = columns.Select((_, c) =>
            {
                var name = columns.Count == 1 ? prefix + i.ToString(invariant) : $"{prefix}{i.ToString(invariant)}_{c.ToString(invariant)}";
                parameters.Add(new QueryParameter(name, key[c], types[c]));
                return ":" + name;
            });
            return columns.Count == 1 ? binds.Single() : $"({string.Join(", ", binds)})";
        }).ToList();
        var refs = columns.Select(c => $"{alias}.{OracleIdentifier.Quote(c.Name)}").ToList();
        return columns.Count == 1
            ? $"{refs[0]} IN ({string.Join(", ", tuples)})"
            : $"({string.Join(", ", refs)}) IN ({string.Join(", ", tuples)})";
    }

    /// <summary>The audit table's own row key as the inner view of <see cref="BuildAuditChanges"/> selects a ROWID.</summary>
    internal const string AuditRowId = "FS_RID";

    /// <summary>
    /// The rows of an audit table (WP-63) with their audit row keys <paramref name="rowKeys"/> against the version before each:
    /// the row key (primary key columns, or the ROWID as <see cref="AuditRowId"/>), the number of the version among those of
    /// its base row (1 = the first) and one character per compared column, <c>1</c> where it differs from the version before
    /// (<c>DECODE</c> holds two NULLs equal). The versions are numbered inside all rows of the base keys
    /// <paramref name="baseKeys"/> – before the audit rows are picked, so the version before counts even if the grid's filter
    /// leaves it out. The order is the template's column, then the ROWID (two rows of one instant stay apart).
    /// </summary>
    /// <exception cref="ArgumentException">No keys or more than <see cref="MaxLookupKeys"/> of either; nothing to compare; a view.</exception>
    public static QuerySpec BuildAuditChanges(
        TableDetails table, Audit.AuditVersions versions, IReadOnlyList<IReadOnlyList<object>> baseKeys, IReadOnlyList<IReadOnlyList<object>> rowKeys)
    {
        ArgumentNullException.ThrowIfNull(versions);
        if (baseKeys.Count == 0 || baseKeys.Count > MaxLookupKeys || rowKeys.Count == 0 || rowKeys.Count > MaxLookupKeys
            || versions.Compared.Count == 0 || table.Table.Kind == TableKind.View)
        {
            throw new ArgumentException($"Versions of a table are compared for 1 to {MaxLookupKeys} keys, over at least one column.", nameof(versions));
        }

        var builder = new Builder(table);
        var byPrimaryKey = RowKeyOf(table) == RowKeyKind.PrimaryKey;
        var partition = string.Join(", ", versions.KeyColumns.Select(i => builder.Ref(table.Columns[i])));
        var window = $"OVER (PARTITION BY {partition} ORDER BY {builder.Ref(table.Columns[versions.OrderColumn])}, {Alias}.ROWID)";
        var rowKey = byPrimaryKey
            ? table.PrimaryKey.Select(name => OracleIdentifier.Quote(name)).ToList()
            : [AuditRowId];
        var changed = string.Join("\n                 || ", versions.Compared.Select(i =>
        {
            var c = builder.Ref(table.Columns[i]);
            return $"DECODE({c}, LAG({c}) {window}, '0', '1')";
        }));

        var sql = new StringBuilder()
            .Append("SELECT ").AppendJoin(", ", rowKey.Select(c => "v." + c)).Append(", v.FS_N, v.FS_CHANGED")
            .Append("\n  FROM (SELECT ").AppendJoin(", ", byPrimaryKey ? rowKey.Select(c => $"{Alias}.{c}") : [$"{Alias}.ROWID AS {AuditRowId}"])
            .Append(",\n               ROW_NUMBER() ").Append(window).Append(" AS FS_N")
            .Append(",\n               ").Append(changed).Append(" AS FS_CHANGED")
            .Append("\n          FROM ").Append(OracleIdentifier.Qualify(table.Table.Owner, table.Table.Name)).Append(' ').Append(Alias)
            .Append("\n         WHERE ").Append(KeyIn(table, versions.KeyColumns, baseKeys, builder.Parameters)).Append(") v")
            .Append("\n WHERE ").Append(KeyIn(table, byPrimaryKey ? [.. table.PrimaryKey.Select(table.IndexOf)] : null, rowKeys, builder.Parameters,
                prefix: "r", alias: "v", rowId: AuditRowId))
            .ToString();
        return new QuerySpec(sql, builder.Parameters);
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

        /// <param name="keylessViewByAllColumns">
        /// A view without key is ordered by all its sortable columns as well (the grid's paging); otherwise by the sorts only.
        /// </param>
        public IEnumerable<string> OrderBy(IReadOnlyList<SortSpec> sorts, bool keylessViewByAllColumns = true)
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
                case RowKeyKind.None when keylessViewByAllColumns:
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

        /// <summary>NCHAR, NVARCHAR2 and NCLOB: "contains" and the like work on an NCLOB too (review 3.40.0, A21 – it was bound as VARCHAR2).</summary>
        private static bool IsNational(ColumnInfo column) => OracleTypes.IsNational(column.DataType);

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
