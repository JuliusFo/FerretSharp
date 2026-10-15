using FerretSharp.Core.Data;
using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Query;

/// <summary>
/// One row source of a query result (WP-48): a table (through one alias) and the result columns read from it. Its
/// <see cref="Projection"/> is the table as far as the result shows it, so FK navigation (<see cref="FkNavigation"/>) works
/// on result rows as on table rows.
/// </summary>
public sealed class SqlResultSource
{
    private readonly int[] _resultColumns;

    internal SqlResultSource(TableDetails details, IReadOnlyList<(int Result, int Table)> columns)
    {
        Details = details;
        Columns = columns;
        _resultColumns = [.. columns.Select(c => c.Result)];
        Projection = details with { Columns = [.. columns.Select(c => details.Columns[c.Table])] };
    }

    /// <summary>The table's structure (all its columns).</summary>
    public TableDetails Details { get; }

    public TableSummary Table => Details.Table;

    /// <summary>Result column → column of <see cref="Details"/>, in result order; each table column once.</summary>
    public IReadOnlyList<(int Result, int Table)> Columns { get; }

    /// <summary>The table with only the columns the result shows, in <see cref="Columns"/> order.</summary>
    public TableDetails Projection { get; }

    /// <summary>The table column a result column holds; null if it comes from elsewhere.</summary>
    public int? TableColumnOf(int resultColumn)
    {
        foreach (var (result, table) in Columns)
        {
            if (result == resultColumn)
            {
                return table;
            }
        }

        return null;
    }

    /// <summary>All of these columns (dictionary names) are in the result: a key or FK over them can be used.</summary>
    public bool Shows(IReadOnlyList<string> columns) => columns.All(c => Projection.IndexOf(c) >= 0);

    /// <summary>A result row as a row of <see cref="Projection"/>.</summary>
    public RowData Project(RowData row) => new(RowKey.None.Instance, [.. _resultColumns.Select(i => row.Values[i])]);
}

/// <summary>An FK jump from result rows and the row source it starts from.</summary>
public sealed record SqlResultJump(FkJump Jump, SqlResultSource Source)
{
    /// <summary>The jump goes over this result column (the clicked one is usually meant).</summary>
    public bool Involves(int resultColumn) =>
        Source.TableColumnOf(resultColumn) is { } column
        && (Jump.Direction == JumpDirection.Outgoing ? Jump.ForeignKey.FromColumns : Jump.ForeignKey.ToColumns).Contains(Source.Details.Columns[column].Name);
}

/// <summary>
/// The row sources of a query result (WP-48), from <see cref="SqlResultOrigins"/> and the tables' structure: which result
/// column holds which table column, and the FK jumps from result rows – outgoing only if all FK columns are in the result,
/// incoming only if all key columns are.
/// </summary>
public sealed class SqlResultSources
{
    public static readonly SqlResultSources None = new([], 0);

    private readonly (SqlResultSource Source, int Table)?[] _byResult;

    private SqlResultSources(IReadOnlyList<SqlResultSource> sources, int resultColumns)
    {
        Sources = sources;
        _byResult = new (SqlResultSource, int)?[resultColumns];
        foreach (var source in sources)
        {
            foreach (var (result, table) in source.Columns)
            {
                _byResult[result] = (source, table);
            }
        }
    }

    public IReadOnlyList<SqlResultSource> Sources { get; }

    /// <summary>The row source and table column behind a result column; null without one.</summary>
    public (SqlResultSource Source, int Table)? Of(int resultColumn) =>
        resultColumn >= 0 && resultColumn < _byResult.Length ? _byResult[resultColumn] : null;

    /// <summary>
    /// The row sources of a result: origins resolved against the statement and the schema, each table's structure from the
    /// schema cache (loaded once per table). A column the table does not have (ROWID, a column of another table of the same
    /// name) has no source.
    /// </summary>
    public static async Task<SqlResultSources> LoadAsync(
        string sql, IReadOnlyList<SqlColumn> columns, SchemaCache schema, CancellationToken cancellationToken)
    {
        var origins = SqlResultOrigins.Resolve(sql, columns, schema);
        var sources = new List<SqlResultSource>();
        foreach (var group in origins.Select((origin, index) => (Origin: origin, Index: index)).Where(o => o.Origin is not null)
                     .GroupBy(o => o.Origin!.Source, StringComparer.Ordinal))
        {
            var details = await schema.GetDetailsAsync(group.First().Origin!.Table, cancellationToken);
            var pairs = group.Select(o => (Result: o.Index, Table: details.IndexOf(o.Origin!.Column)))
                .Where(p => p.Table >= 0)
                .DistinctBy(p => p.Table)
                .ToList();
            if (pairs.Count > 0)
            {
                sources.Add(new SqlResultSource(details, pairs));
            }
        }

        return new SqlResultSources(sources, columns.Count);
    }

    /// <summary>
    /// FK jumps from <paramref name="rows"/> (result rows, at least one): outgoing ones of all row sources first, then incoming
    /// ones; the same jump reached through two sources (a column read twice) once.
    /// </summary>
    public IReadOnlyList<SqlResultJump> JumpsFor(
        IReadOnlyList<RowData> rows, Func<TableRef, IEnumerable<ForeignKeyInfo>> outgoing, Func<TableRef, IEnumerable<ForeignKeyInfo>> incoming)
    {
        var jumps = new List<SqlResultJump>();
        foreach (var source in Sources)
        {
            var projected = rows.Select(source.Project).ToList();
            var table = source.Table.Ref;
            jumps.AddRange(FkNavigation.JumpsFor(source.Projection, projected,
                    outgoing(table).Where(f => source.Shows(f.FromColumns)), incoming(table).Where(f => source.Shows(f.ToColumns)))
                .Select(j => new SqlResultJump(j, source)));
        }

        return jumps.OrderBy(j => j.Jump.Direction)
            .DistinctBy(j => (j.Jump.Direction, j.Jump.ForeignKey, j.Jump.Condition))
            .ToList();
    }
}
