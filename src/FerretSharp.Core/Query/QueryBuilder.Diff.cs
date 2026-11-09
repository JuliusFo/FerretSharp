using System.Globalization;
using System.Text;
using FerretSharp.Core.Oracle;
using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Query;

/// <summary>
/// The data diff of a table (WP-44): the rows that differ between now and a start SCN, as read by
/// <see cref="QueryBuilder.BuildDiff"/>. The result starts with the side (<see cref="QueryBuilder.DiffSideNow"/> or
/// <see cref="QueryBuilder.DiffSideThen"/>), then the ROWID if <see cref="HasRowId"/>, then the compared columns in
/// <see cref="Columns"/> order with their projection – columns in <see cref="Omitted"/> are not in the result.
/// </summary>
/// <param name="Columns">Every column of the table, in its order (values are read into this order).</param>
/// <param name="Omitted">Indexes of columns a set operation cannot compare (LONG, XMLTYPE, object types …).</param>
public sealed record DiffQuery(
    string Sql,
    IReadOnlyList<QueryParameter> Parameters,
    IReadOnlyList<ResultColumn> Columns,
    IReadOnlySet<int> Omitted,
    RowKeyKind RowKey,
    bool HasRowId);

public static partial class QueryBuilder
{
    /// <summary>The side of a diff row: the row as it is now.</summary>
    public const int DiffSideNow = 1;

    /// <summary>The side of a diff row: the row as it was at the start SCN.</summary>
    public const int DiffSideThen = 0;

    /// <summary>
    /// The current SCN without rights on <c>V$DATABASE</c> (WP-44), with the database it belongs to (WP-78). Mapped from the
    /// time, so it may lie a few seconds back: changes committed just before may count as changed since.
    /// </summary>
    public const string CurrentScnSql = "SELECT TIMESTAMP_TO_SCN(SYSTIMESTAMP), " + DatabaseSelect + " FROM dual";

    /// <summary>
    /// The rows of a table that differ between now and <paramref name="scn"/> (WP-44), computed in the database: the rows of the
    /// filter now <c>MINUS</c> those of the filter <c>AS OF SCN</c>, and the other way round, joined with <c>UNION ALL</c> and
    /// marked with their side. Over the line come only rows that changed; a key on one side only is a new (or deleted) row,
    /// on both a changed one – the cells are compared by the caller. With a filter, a row that moved into or out of it counts
    /// as new or deleted. Compared like the grid shows them: a CLOB by its first <see cref="ClobPreviewLength"/> characters and
    /// its length, a BLOB by its length; LONG, XMLTYPE and object types cannot be compared by a set operation and are left out.
    /// Ordered by the row key, then the side, at most <paramref name="limit"/> rows.
    /// </summary>
    /// <param name="limit">Rows of the result at most (two for a changed row); the caller reads one more to know it was cut.</param>
    /// <exception cref="ArgumentException">A view, or a table without a row key.</exception>
    /// <exception cref="QueryValidationException">A filter cannot be applied.</exception>
    public static DiffQuery BuildDiff(TableDetails table, IReadOnlyList<FilterCondition> filters, long scn, int limit)
    {
        var rowKey = RowKeyOf(table);
        if (table.Table.Kind == TableKind.View || rowKey == RowKeyKind.None)
        {
            throw new ArgumentException("Only tables and materialized views can be compared with an earlier state.", nameof(table));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        var invariant = CultureInfo.InvariantCulture;
        var columns = table.Columns.Select(c => new ResultColumn(c, ProjectionOf(c))).ToList();
        var omitted = columns.Select((c, i) => (c, i)).Where(x => x.c.Projection == Projection.NullMarker).Select(x => x.i).ToHashSet();
        var hasRowId = rowKey == RowKeyKind.RowId;

        // The select list with an alias per item, so the outer query can name them (expressions have no usable names).
        var items = new List<(string Expression, string Alias)>();
        if (hasRowId)
        {
            items.Add(($"{Alias}.ROWID", AuditRowId));
        }

        for (var i = 0; i < columns.Count; i++)
        {
            if (omitted.Contains(i))
            {
                continue;
            }

            var reference = $"{Alias}.{OracleIdentifier.Quote(columns[i].Column.Name)}";
            var alias = "C" + i.ToString(invariant);
            switch (columns[i].Projection)
            {
                case Projection.ClobPreview:
                    items.Add(($"DBMS_LOB.SUBSTR({reference}, {ClobPreviewLength}, 1)", alias + "_P"));
                    items.Add(($"DBMS_LOB.GETLENGTH({reference})", alias + "_L"));
                    break;
                case Projection.BlobLength:
                    items.Add(($"DBMS_LOB.GETLENGTH({reference})", alias));
                    break;
                default:
                    items.Add((reference, alias));
                    break;
            }
        }

        // Ordered by the key in its own order, as the grid sorts; the side last, so a changed row's two halves follow each other.
        List<string> keyAliases = hasRowId ? [AuditRowId] : [.. table.PrimaryKey.Select(key => "C" + table.IndexOf(key).ToString(invariant))];

        var parameters = new List<QueryParameter>();
        var selectList = string.Join(", ", items.Select(i => $"{i.Expression} AS {i.Alias}"));
        var name = OracleIdentifier.Qualify(table.Table.Owner, table.Table.Name);
        var scnBinds = 0;

        string Now() => $"SELECT {selectList}\n                  FROM {name} {Alias}{Indent(new Builder(table, parameters: parameters).Where(filters))}";

        string Then()
        {
            var bind = "d_scn" + scnBinds++.ToString(invariant);
            parameters.Add(new QueryParameter(bind, scn, OracleTypeHint.Number));
            return $"SELECT {selectList}\n                  FROM {name} AS OF SCN :{bind} {Alias}{Indent(new Builder(table, parameters: parameters).Where(filters))}";
        }

        var aliases = string.Join(", ", items.Select(i => i.Alias));
        var sql = new StringBuilder()
            .Append("SELECT FS_SIDE, ").Append(aliases)
            .Append("\n  FROM (SELECT ").Append(DiffSideNow.ToString(invariant)).Append(" AS FS_SIDE, n.*")
            .Append("\n          FROM (").Append(Now())
            .Append("\n                MINUS\n                ").Append(Then()).Append(") n")
            .Append("\n        UNION ALL")
            .Append("\n        SELECT ").Append(DiffSideThen.ToString(invariant)).Append(" AS FS_SIDE, o.*")
            .Append("\n          FROM (").Append(Then())
            .Append("\n                MINUS\n                ").Append(Now()).Append(") o)")
            .Append("\n ORDER BY ").AppendJoin(", ", keyAliases).Append(", FS_SIDE")
            .Append("\n FETCH FIRST :d_limit ROWS ONLY")
            .ToString();
        parameters.Add(new QueryParameter("d_limit", limit, OracleTypeHint.Number));
        return new DiffQuery(sql, parameters, columns, omitted, rowKey, hasRowId);
    }

    /// <summary>A WHERE of the builder, indented to sit inside the diff's inline views.</summary>
    private static string Indent(string where) => where.Replace("\n", "\n                ", StringComparison.Ordinal);
}
