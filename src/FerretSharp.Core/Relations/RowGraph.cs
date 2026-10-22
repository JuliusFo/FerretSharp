using FerretSharp.Core.Data;
using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Relations;

/// <summary>
/// Rows found by following relationships (<see cref="RowTraversal"/>, WP-42): per table the rows in the order they were
/// found, each once by its row key, and the links between them. It grows as more relationships are followed – the copy
/// collects all at once, the record diagram (WP-60) one relationship of one row at a time. Not thread-safe: one traversal
/// at a time.
/// </summary>
public sealed class RowGraph
{
    private readonly Dictionary<TableRef, TableRows> _tables = [];
    private readonly List<TableRef> _order = [];
    private readonly HashSet<RowLink> _links = [];
    private readonly List<RowLink> _linkOrder = [];
    private readonly List<RelationProblem> _problems = [];

    /// <summary>Tables in the order their first row was found.</summary>
    public IReadOnlyList<TableRows> Tables => [.. _order.Select(t => _tables[t])];

    /// <summary>Links between found rows, in the order they were found.</summary>
    public IReadOnlyList<RowLink> Links => _linkOrder;

    /// <summary>Relationships that could not be followed (table outside the schema, column missing, type not comparable).</summary>
    public IReadOnlyList<RelationProblem> Problems => _problems;

    public int RowCount { get; private set; }

    public TableRows? Of(TableRef table) => _tables.GetValueOrDefault(table);

    /// <returns>False if the row is known already (by its row key).</returns>
    internal bool Add(TableDetails table, RowData row)
    {
        if (!_tables.TryGetValue(table.Table.Ref, out var rows))
        {
            rows = new TableRows(table);
            _tables.Add(table.Table.Ref, rows);
            _order.Add(table.Table.Ref);
        }

        if (!rows.Add(row))
        {
            return false;
        }

        RowCount++;
        return true;
    }

    internal void Link(RowLink link)
    {
        if (_links.Add(link))
        {
            _linkOrder.Add(link);
        }
    }

    internal void Report(RelationProblem problem)
    {
        if (!_problems.Contains(problem))
        {
            _problems.Add(problem);
        }
    }
}

/// <summary>The found rows of one table, each once by its row key.</summary>
public sealed class TableRows(TableDetails details)
{
    private readonly Dictionary<RowKey, RowData> _byKey = [];
    private readonly List<RowData> _rows = [];

    public TableDetails Details { get; } = details;

    public TableRef Table => Details.Table.Ref;

    /// <summary>In the order they were found.</summary>
    public IReadOnlyList<RowData> Rows => _rows;

    public RowData? Find(RowKey key) => _byKey.GetValueOrDefault(key);

    public bool Contains(RowKey key) => _byKey.ContainsKey(key);

    internal bool Add(RowData row)
    {
        if (!_byKey.TryAdd(row.Key, row))
        {
            return false;
        }

        _rows.Add(row);
        return true;
    }
}

/// <summary>A row of <c>Relation.From</c> (<paramref name="Child"/>) references a row of <c>Relation.To</c> (<paramref name="Parent"/>).</summary>
public sealed record RowLink(ForeignKeyInfo Relation, RowKey Child, RowKey Parent);

/// <summary>A relationship that was not followed, and why (a full sentence for the preview).</summary>
public sealed record RelationProblem(ForeignKeyInfo Relation, string Message);
