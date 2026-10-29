using System.Globalization;
using FerretSharp.Core.Connections;
using FerretSharp.Core.Data;
using FerretSharp.Core.Query;
using FerretSharp.Core.Resources;
using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Relations;

/// <summary>Which way to follow a relationship from a row.</summary>
public enum LinkDirection
{
    /// <summary>To the rows the row references (it is in <c>From</c>, they are in <c>To</c>).</summary>
    Parents,

    /// <summary>To the rows that reference the row (it is in <c>To</c>, they are in <c>From</c>).</summary>
    Children,
}

/// <summary>
/// Which children a traversal collects (<see cref="RowTraversal.CollectAsync"/>): paths of child relationships from the start
/// table – <c>[AUFTRAG→KUNDE]</c> are the orders of a customer, <c>[AUFTRAG→KUNDE, POSITION→AUFTRAG]</c> their positions too –,
/// each at most <see cref="MaxDepth"/> long. Parents are always collected, except along <see cref="SkippedParents"/>.
/// </summary>
/// <param name="SkippedParents"><see cref="RelationGraph.KeyOf"/> of relationships whose parents are not collected.</param>
public sealed record TraversalPlan(IReadOnlyList<IReadOnlyList<ForeignKeyInfo>> Children, IReadOnlySet<string> SkippedParents)
{
    /// <summary>Levels of children below the start rows (decision of the user: 1 by default, up to 3).</summary>
    public const int MaxDepth = 3;

    internal static readonly TraversalPlan ParentsOnly = new([], new HashSet<string>());

    /// <summary>The children of the start table one level deep along every relationship (the default).</summary>
    public static TraversalPlan Default(RelationGraph graph, TableRef start) =>
        new([.. graph.ChildrenOf(start).Select(r => (IReadOnlyList<ForeignKeyInfo>)[r])], new HashSet<string>());
}

/// <summary>Rows were read up to the limit; the traversal stopped.</summary>
public sealed class TooManyRowsException(int limit)
    : RefusedException(TextFormat.Format(RelationText.TooManyRows, limit))
{
    public int Limit { get; } = limit;
}

/// <summary>
/// Follows relationships through the data (WP-42): from rows to the rows they reference (parents) or that reference them
/// (children), read on the read path of one workspace's session (<see cref="IDataAccess.ReadByKeysAsync"/>) – so its own
/// uncommitted rows count. Independent of any copy target: the copy collects with <see cref="CollectAsync"/>, the record
/// diagram (WP-60) grows a graph with <see cref="FollowAsync"/>.
/// </summary>
/// <param name="detailsOf">Columns and keys of a table; null if the connection does not know it (another schema without synonym).</param>
/// <param name="wholeLobs">LOB values whole (a copy writes them) instead of preview and length.</param>
/// <param name="maxRows">Rows a graph may hold; reading more stops with <see cref="TooManyRowsException"/>.</param>
public sealed class RowTraversal(
    IDataAccess data, RelationGraph relations, Func<TableRef, CancellationToken, Task<TableDetails?>> detailsOf,
    bool wholeLobs, int maxRows = RowTraversal.DefaultMaxRows)
{
    /// <summary>Rows of one copy at most (decision of the user).</summary>
    public const int DefaultMaxRows = 10_000;

    /// <summary>A new graph with the rows of <paramref name="table"/> that have these keys now (a deleted one is left out).</summary>
    public async Task<RowGraph> StartAsync(TableDetails table, IReadOnlyList<RowKey> keys, CancellationToken cancellationToken)
    {
        var graph = new RowGraph();
        var byKey = keys.OfType<RowKey.PrimaryKey>().Select(k => (IReadOnlyList<object>)[.. k.Values.Select(v => v!)])
            .Where(k => k.All(v => v is not null)).ToList();
        var byRowId = keys.OfType<RowKey.RowId>().Select(k => (IReadOnlyList<object>)[k.Value]).ToList();
        if (byKey.Count > 0)
        {
            AddAll(graph, table, await data.ReadByKeysAsync(table, [.. table.PrimaryKey.Select(table.IndexOf)], byKey, wholeLobs, maxRows: null, cancellationToken));
        }

        if (byRowId.Count > 0)
        {
            AddAll(graph, table, await data.ReadByKeysAsync(table, null, byRowId, wholeLobs, maxRows: null, cancellationToken));
        }

        return graph;
    }

    /// <summary>
    /// The start rows, the children along <paramref name="plan"/>'s paths, then the parents of every row found – and theirs,
    /// until none is missing (a row whose parent is not copied along would violate the relationship in the target).
    /// </summary>
    /// <param name="progress">Each table before it is read, with the rows found so far.</param>
    public async Task<RowGraph> CollectAsync(
        TableDetails table, IReadOnlyList<RowKey> keys, TraversalPlan plan, IProgress<TraversalProgress>? progress, CancellationToken cancellationToken)
    {
        if (plan.Children.Any(p => p.Count is 0 or > TraversalPlan.MaxDepth))
        {
            throw new ArgumentException($"A path of children has 1 to {TraversalPlan.MaxDepth} relationships.", nameof(plan));
        }

        progress?.Report(new TraversalProgress(table.Table.Ref, 0));
        var graph = await StartAsync(table, keys, cancellationToken);
        var start = graph.Of(table.Table.Ref)?.Rows ?? [];
        await FollowPathsAsync(graph, table.Table.Ref, start, plan.Children, 0, progress, cancellationToken);
        await CollectParentsAsync(graph, plan.SkippedParents, progress, cancellationToken);
        return graph;
    }

    /// <summary>Children along the paths that continue at <paramref name="depth"/>, grouped by their next relationship.</summary>
    private async Task FollowPathsAsync(
        RowGraph graph, TableRef table, IReadOnlyList<RowData> rows, IReadOnlyList<IReadOnlyList<ForeignKeyInfo>> paths, int depth,
        IProgress<TraversalProgress>? progress, CancellationToken cancellationToken)
    {
        foreach (var next in paths.Where(p => p.Count > depth).GroupBy(p => p[depth]))
        {
            if (next.Key.To != table || rows.Count == 0)
            {
                continue;
            }

            progress?.Report(new TraversalProgress(next.Key.From, graph.RowCount));
            await FollowAsync(graph, next.Key, LinkDirection.Children, rows, cancellationToken);

            // All children of these rows go on along the path, also those the graph had already (reached by another path).
            var parentKeys = rows.Select(r => r.Key).ToHashSet();
            var children = graph.Of(next.Key.From);
            List<RowData> reached = children is null ? [] : [.. graph.LinksAlong(next.Key)
                .Where(l => parentKeys.Contains(l.Parent))
                .Select(l => l.Child).Distinct()
                .Select(k => children.Find(k)).OfType<RowData>()];
            graph.NoteReached(next.First().Take(depth + 1).ToList(), reached.Count);
            await FollowPathsAsync(graph, next.Key.From, reached, [.. next], depth + 1, progress, cancellationToken);
        }
    }

    /// <summary>Parents of every row, then of the parents found, until nothing new turns up.</summary>
    private async Task CollectParentsAsync(
        RowGraph graph, IReadOnlySet<string> skipped, IProgress<TraversalProgress>? progress, CancellationToken cancellationToken)
    {
        var pending = graph.Tables.ToDictionary(t => t.Table, t => t.Rows.ToList());
        while (pending.Count > 0)
        {
            var (table, rows) = pending.First();
            pending.Remove(table);
            foreach (var relation in relations.ParentsOf(table).Where(r => !skipped.Contains(RelationGraph.KeyOf(r))))
            {
                progress?.Report(new TraversalProgress(relation.To, graph.RowCount));
                var found = await FollowAsync(graph, relation, LinkDirection.Parents, rows, cancellationToken);
                if (found.Count > 0)
                {
                    if (pending.TryGetValue(relation.To, out var waiting))
                    {
                        waiting.AddRange(found);
                    }
                    else
                    {
                        pending[relation.To] = [.. found];
                    }
                }
            }
        }
    }

    /// <summary>
    /// Follows one relationship from <paramref name="rows"/> (rows of the graph's table on this side) and links what it finds –
    /// also rows the graph had already. A parent the graph holds by the key the relationship points to is linked without
    /// reading. Rows with a NULL in the relationship's columns reference nothing. A relationship that cannot be followed is
    /// reported in <see cref="RowGraph.Problems"/>.
    /// </summary>
    /// <returns>The rows that were new to the graph.</returns>
    /// <exception cref="TooManyRowsException">The graph would hold more than the limit.</exception>
    public async Task<IReadOnlyList<RowData>> FollowAsync(
        RowGraph graph, ForeignKeyInfo relation, LinkDirection direction, IReadOnlyList<RowData> rows, CancellationToken cancellationToken)
    {
        var parents = direction == LinkDirection.Parents;
        var own = graph.Of(parents ? relation.From : relation.To)?.Details
                  ?? throw new ArgumentException("The rows are not in the graph.", nameof(rows));
        var target = parents ? relation.To : relation.From;
        var ownColumns = parents ? relation.FromColumns : relation.ToColumns;
        var targetColumns = parents ? relation.ToColumns : relation.FromColumns;

        if (await detailsOf(target, cancellationToken) is not { } details)
        {
            graph.Report(new RelationProblem(relation, TextFormat.Format(RelationText.TableNotInSchema, relation.Name, target)));
            return [];
        }

        if ((Missing(own, ownColumns) ?? Missing(details, targetColumns)) is { } missing)
        {
            graph.Report(new RelationProblem(relation, TextFormat.Format(RelationText.ColumnMissing, relation.Name, missing.Column, missing.Table)));
            return [];
        }

        var ownIndexes = ownColumns.Select(own.IndexOf).ToArray();
        var targetIndexes = targetColumns.Select(details.IndexOf).ToArray();
        var trim = ownIndexes.Select(i => own.Columns[i]).Concat(targetIndexes.Select(i => details.Columns[i])).Any(c => OracleTypes.IsFixedChar(c.DataType));

        // Each row's key on this side, to match what comes back.
        var fromRows = new KeyGroups(trim);
        foreach (var row in rows)
        {
            var values = ownIndexes.Select(i => row.Values[i]).ToArray();
            if (values.Any(v => v is null))
            {
                continue;
            }

            if (ownIndexes.Select(i => own.Columns[i]).Zip(values).FirstOrDefault(p => FkNavigation.FilterValue(p.First, p.Second!) is null) is { First: { } column })
            {
                graph.Report(new RelationProblem(relation, TextFormat.Format(RelationText.KeyTypeNotComparable, relation.Name, column.DisplayType)));
                return [];
            }

            fromRows.Add(values!, row);
        }

        // A parent the graph holds by the primary key the relationship points to needs no read.
        var known = graph.Of(target);
        var toRead = fromRows.Keys.ToList();
        if (parents && known is not null && QueryBuilder.RowKeyOf(details) == RowKeyKind.PrimaryKey
            && details.PrimaryKey.Order(StringComparer.Ordinal).SequenceEqual(targetColumns.Order(StringComparer.Ordinal), StringComparer.Ordinal))
        {
            toRead = [];
            foreach (var key in fromRows.Keys)
            {
                var pk = new RowKey.PrimaryKey([.. details.PrimaryKey.Select(name => key[targetColumns.ToList().IndexOf(name)])]);
                if (known.Find(pk) is { } parent)
                {
                    LinkAll(graph, relation, parents, fromRows.RowsOf(key), parent);
                }
                else
                {
                    toRead.Add(key);
                }
            }
        }

        if (toRead.Count == 0)
        {
            return [];
        }

        // Read no more than the limit lets in, and add nothing if it is passed: the record diagram (WP-60) goes on with the
        // graph as it was, so one relationship is followed whole or not at all. Rows the graph has already come back too and
        // take no room (review: a relationship to mostly known rows was refused, a nearly full graph still read 2 001 rows).
        var before = graph.Of(target);
        var fits = maxRows - graph.RowCount + (before?.Rows.Count ?? 0);
        var found = await data.ReadByKeysAsync(details, targetIndexes, toRead, wholeLobs, fits + 1, cancellationToken);
        if (found.Count > fits || graph.RowCount + found.Count(r => before?.Contains(r.Key) != true) > maxRows)
        {
            throw new TooManyRowsException(maxRows);
        }

        var added = new List<RowData>();
        foreach (var row in found)
        {
            if (graph.Add(details, row))
            {
                added.Add(row);
            }

            if (fromRows.RowsOf(targetIndexes.Select(i => row.Values[i])) is { Count: > 0 } matching)
            {
                LinkAll(graph, relation, parents, matching, graph.Of(target)!.Find(row.Key) ?? row);
            }
        }

        return added;
    }

    private static void LinkAll(RowGraph graph, ForeignKeyInfo relation, bool parents, IReadOnlyList<RowData> rows, RowData other)
    {
        foreach (var row in rows)
        {
            graph.Link(parents ? new RowLink(relation, row.Key, other.Key) : new RowLink(relation, other.Key, row.Key));
        }
    }

    private static void AddAll(RowGraph graph, TableDetails table, IReadOnlyList<RowData> rows)
    {
        foreach (var row in rows)
        {
            graph.Add(table, row);
        }
    }

    private static (string Column, TableRef Table)? Missing(TableDetails table, IReadOnlyList<string> columns) =>
        columns.FirstOrDefault(c => table.IndexOf(c) < 0) is { } column ? (column, table.Table.Ref) : null;

    /// <summary>
    /// A key as text that is the same for equal values read from different columns: 5 from NUMBER(10) and 5.00 from
    /// NUMBER(10,2), a CHAR value with or without its padding (<paramref name="trim"/>), RAW by content.
    /// </summary>
    internal static string KeyText(IReadOnlyList<object> values, bool trim) =>
        string.Join("\u0001", values.Select(v => v switch
        {
            decimal d => d.ToString("G29", CultureInfo.InvariantCulture),
            string s => trim ? s.TrimEnd(' ') : s,
            byte[] bytes => Convert.ToHexString(bytes),
            DateTime dt => dt.ToString("O", CultureInfo.InvariantCulture),
            DateTimeOffset dto => dto.ToString("O", CultureInfo.InvariantCulture),
            IConvertible convertible => convertible.ToString(CultureInfo.InvariantCulture),
            _ => v.ToString() ?? "",
        }));
}

/// <summary>Where a traversal is: the table it reads next and the rows found so far.</summary>
/// <param name="Level">The level of a record diagram's expansion over several (WP-62); null otherwise.</param>
public sealed record TraversalProgress(TableRef Table, int Rows, TraversalLevel? Level = null);

/// <summary>Level <paramref name="Number"/> of <paramref name="Of"/>, from 1.</summary>
public sealed record TraversalLevel(int Number, int Of);
