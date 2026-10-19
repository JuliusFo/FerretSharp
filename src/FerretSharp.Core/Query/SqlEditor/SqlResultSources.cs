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

/// <summary>An FK jump from result rows and the row sources it starts from (two if the result reads its columns twice).</summary>
public sealed record SqlResultJump(FkJump Jump, IReadOnlyList<SqlResultSource> Sources)
{
    /// <summary>The jump goes over this result column, in any of its sources (the clicked one is usually meant).</summary>
    public bool Involves(int resultColumn) =>
        Sources.Any(source => source.TableColumnOf(resultColumn) is { } column
            && Jump.RowColumns.Contains(source.Details.Columns[column].Name));
}

/// <summary>
/// The row sources of a query result (WP-48), from <see cref="SqlResultOrigins"/> and the tables' structure: which result
/// column holds which table column, and the FK jumps from result rows – outgoing only if all FK columns are in the result,
/// incoming only if all key columns are.
/// </summary>
public sealed class SqlResultSources
{
    public static readonly SqlResultSources None = new([]);

    private SqlResultSources(IReadOnlyList<SqlResultSource> sources) => Sources = sources;

    public IReadOnlyList<SqlResultSource> Sources { get; }

    /// <summary>
    /// The row sources of a result: origins resolved against the statement and the schema, each table's structure from the
    /// schema cache – all tables asked for at once (the explorer session queues them, no table waits for the previous one to
    /// be asked for), each once. A column the table does not have (ROWID, a column of another table of the same name) has no
    /// source.
    /// </summary>
    public static async Task<SqlResultSources> LoadAsync(
        string sql, IReadOnlyList<SqlColumn> columns, SchemaCache schema, CancellationToken cancellationToken)
    {
        var groups = SqlResultOrigins.Resolve(sql, columns, schema)
            .Select((origin, index) => (Origin: origin!, Index: index))
            .Where(o => o.Origin is not null)
            .GroupBy(o => o.Origin.Source, StringComparer.Ordinal)
            .ToList();
        var loads = groups.Select(g => g.First().Origin.Table).DistinctBy(t => t.Ref)
            .ToDictionary(t => t.Ref, t => schema.GetDetailsAsync(t, cancellationToken));
        await Task.WhenAll(loads.Values);

        var sources = new List<SqlResultSource>();
        foreach (var group in groups)
        {
            var details = await loads[group.First().Origin.Table.Ref];
            var pairs = group.Select(o => (Result: o.Index, Table: details.IndexOf(o.Origin.Column)))
                .Where(p => p.Table >= 0)
                .DistinctBy(p => p.Table)
                .ToList();
            if (pairs.Count > 0)
            {
                sources.Add(new SqlResultSource(details, pairs));
            }
        }

        return new SqlResultSources(sources);
    }

    /// <summary>
    /// FK jumps from <paramref name="rows"/> (result rows, at least one): outgoing ones of all row sources first, then incoming
    /// ones (also through base tables, <paramref name="inherited"/>); the same jump reached through two sources (a column read
    /// twice) once, with both sources.
    /// </summary>
    public IReadOnlyList<SqlResultJump> JumpsFor(
        IReadOnlyList<RowData> rows, Func<TableRef, IEnumerable<ForeignKeyInfo>> outgoing, Func<TableRef, IEnumerable<ForeignKeyInfo>> incoming,
        Func<TableRef, IEnumerable<InheritedReference>>? inherited = null)
    {
        var jumps = new List<(FkJump Jump, SqlResultSource Source)>();
        foreach (var source in Sources)
        {
            var projected = rows.Select(source.Project).ToList();
            var table = source.Table.Ref;
            jumps.AddRange(FkNavigation.JumpsFor(source.Projection, projected,
                    outgoing(table).Where(f => source.Shows(f.FromColumns)), incoming(table).Where(f => source.Shows(f.ToColumns)),
                    inherited?.Invoke(table).Where(r => source.Shows(r.Columns)))
                .Select(j => (j, source)));
        }

        return jumps.OrderBy(j => j.Jump.Direction)
            .GroupBy(j => (j.Jump.Direction, j.Jump.ForeignKey, j.Jump.Condition))
            .Select(g => new SqlResultJump(g.First().Jump, [.. g.Select(j => j.Source)]))
            .ToList();
    }
}
