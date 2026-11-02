using FerretSharp.Core.ClrModel;
using FerretSharp.Core.Query;
using FerretSharp.Core.Relations;
using FerretSharp.Core.Schema;

namespace FerretSharp.UI.State;

/// <summary>
/// The mode "Abfrage bauen" of a table tab's relationship view (WP-69): the query of the tab (<see cref="TableTab.Design"/>,
/// saved with it), resolved against the schema's relationships, the node the side panel shows, and the columns of the
/// query's tables as far as they are loaded. Every change goes into the tab at once – a query grows over days.
/// </summary>
public sealed class QueryDesignEditor(TableTab tab)
{
    private readonly Dictionary<TableRef, TableDetails> _details = [];
    private RelationGraph? _graph;
    private IReadOnlyList<QueryStep> _selected = [];

    public TableRef Root => tab.Table.Ref;

    public QueryDesignState State => tab.Design ?? QueryDesignState.Empty;

    /// <summary>The query as the schema has it now; null before <see cref="Load"/>.</summary>
    public QueryDesign? Design { get; private set; }

    /// <summary>What fell out of the saved query since (a relationship, a column) – shown until the next change.</summary>
    public IReadOnlyList<string> Notes { get; private set; } = [];

    public bool Active => State.Active;

    /// <summary>The node the side panel shows: the one chosen last, the root if it is gone.</summary>
    public QueryNode? Selected => Design?.Find(_selected) ?? Design?.Nodes[0];

    /// <summary>Resolves the tab's query against the schema's relationships (the schema reloaded, a new C# model).</summary>
    public void Load(RelationGraph graph)
    {
        _graph = graph;
        Resolve(keepNotes: false);
    }

    public void SetActive(bool active) => Set(State with { Active = active });

    public void Select(QueryNode node) => _selected = node.Path;

    /// <summary>
    /// A click on an edge of the diagram: from the selected node if the relationship touches its table, else from the first
    /// node that it touches. A step the query has already is taken out (with what hangs on it), a new one is added and selected.
    /// </summary>
    /// <returns>Whether the query changed (the relationship touches none of its tables otherwise).</returns>
    public bool ToggleEdge(ForeignKeyInfo relation)
    {
        if (Design is not { } design)
        {
            return false;
        }

        var from = Selected is { } selected && QueryDesign.StepFrom(selected.Table, relation) is not null
            ? selected
            : design.Nodes.FirstOrDefault(n => QueryDesign.StepFrom(n.Table, relation) is not null);
        if (from is null || QueryDesign.StepFrom(from.Table, relation) is not { } step)
        {
            return false;
        }

        // A self reference from a node that reached its table through it would only lead back.
        var existing = design.ChildrenOf(from).FirstOrDefault(n => n.Relation is { } r && RelationGraph.KeyOf(r) == step.Relation);
        if (existing is not null)
        {
            Remove(existing);
        }
        else
        {
            Add(from, step);
        }

        return true;
    }

    public void Add(QueryNode from, QueryStep step)
    {
        Set(State.Add(from.Path, step));
        _selected = [.. from.Path, step];
    }

    public void Remove(QueryNode node)
    {
        if (QueryStep.StartsWith(_selected, node.Path))
        {
            _selected = node.From?.Path ?? [];
        }

        Set(State.Remove(node.Path));
    }

    /// <summary>The steps the panel offers from a node (<see cref="QueryDesign.NextSteps"/>).</summary>
    public IReadOnlyList<(QueryStep Step, ForeignKeyInfo Relation)> NextSteps(QueryNode node) =>
        Design is { } design && _graph is { } graph ? design.NextSteps(node, graph) : [];

    public bool HasColumn(QueryNode node, string column) => node.State.Columns.Any(c => c.Column == column);

    public void ToggleColumn(QueryNode node, string column) =>
        Update(node, n => n with
        {
            Columns = HasColumn(node, column)
                ? [.. n.Columns.Where(c => c.Column != column)]
                : [.. n.Columns, new QueryColumn(column)],
        });

    /// <summary>The name of a value (DTO property); empty for the one from the path.</summary>
    public void RenameColumn(QueryNode node, string column, string name) =>
        Update(node, n => n with
        {
            Columns = [.. n.Columns.Select(c => c.Column == column ? c with { Name = name.Trim().Length > 0 ? name.Trim() : null } : c)],
        });

    public void SetFilters(QueryNode node, IReadOnlyList<QueryFilter> filters) => Update(node, n => n with { Filters = filters });

    public void SetSorts(QueryNode node, IReadOnlyList<SortSpec> sorts) => Update(node, n => n with { Sorts = sorts });

    public void SetNames(QueryNames names) => Set(State with { Names = names });

    // --- Columns of the query's tables ------------------------------------------------------------------------------------

    /// <summary>The query's tables whose columns are not loaded yet.</summary>
    public IReadOnlyList<TableRef> Missing => Design is { } design ? [.. design.Nodes.Select(n => n.Table).Distinct().Where(t => !_details.ContainsKey(t))] : [];

    public TableDetails? DetailsOf(TableRef table) => _details.GetValueOrDefault(table);

    /// <summary>Columns of a table came: what the saved query names and the table no longer has falls out (with a note).</summary>
    public void Loaded(TableDetails details)
    {
        _details[details.Table.Ref] = details;
        Resolve(keepNotes: true);
    }

    /// <summary>Every table of the query has its columns – the SQL and the C# can be written.</summary>
    public bool Complete => Design is not null && Missing.Count == 0;

    /// <summary>Whether a column can be a value of the query: not a LOB, LONG or unknown type (the first stage).</summary>
    public static bool Selectable(ColumnInfo column) => QueryBuilder.ProjectionOf(column) == Projection.Value;

    public bool HasColumns => Design?.Nodes.Any(n => n.State.Columns.Count > 0) == true;

    /// <summary>
    /// Why the query cannot be written: a condition the grid would refuse (a value that is no number, a missing date),
    /// named with its node; null if none. The panel marks the row itself.
    /// </summary>
    public string? Problem =>
        Design?.Nodes.Select(n => DetailsOf(n.Table) is { } details
                ? QueryBuilder.Validate(details, [.. n.State.Filters.Select(f => f.Condition)]).Values.FirstOrDefault() is { } error
                    ? $"{QueryDesign.Label(n)}: {error}"
                    : null
                : null)
            .FirstOrDefault(p => p is not null);

    /// <summary>The query's SQL; null while columns are missing, none is chosen or a condition is invalid.</summary>
    public DesignSql? Sql(DesignSqlMode mode) =>
        Complete && HasColumns && Problem is null ? QueryBuilder.BuildDesign(Design!, t => _details[t], mode) : null;

    /// <summary>The query's C#; null while columns are missing, none is chosen, a condition is invalid or the root has no entity.</summary>
    public QueryCode? Code(ClrModelMapping? mapping, QueryCodeStyle style)
    {
        if (!Complete || !HasColumns || Problem is not null || mapping is null || QueryCodeBuilder.Unavailable(Design!, mapping) is not null)
        {
            return null;
        }

        var entity = ClrModelMapping.ShortName(mapping.EntityOf(Root)!.Entity.ClrType);
        var defaults = QueryCodeOptions.For(entity);
        var names = State.Names;
        var options = defaults with
        {
            ClassName = Named(names.Class, defaults.ClassName),
            PredicateName = Named(names.Predicate, defaults.PredicateName),
            ProjectionName = Named(names.Projection, defaults.ProjectionName),
            DtoName = Named(names.Dto, defaults.DtoName),
        };
        return QueryCodeBuilder.Build(Design!, mapping, t => _details[t], style.Options(options));
    }

    /// <summary>Relationships the query takes (their keys), for the diagram.</summary>
    public IReadOnlySet<string> Edges =>
        Design is { } design ? design.Nodes.Where(n => n.Relation is not null).Select(n => RelationGraph.KeyOf(n.Relation!)).ToHashSet() : new HashSet<string>();

    /// <summary>Tables in the query, for the diagram.</summary>
    public IReadOnlySet<TableRef> Tables => Design is { } design ? design.Nodes.Select(n => n.Table).ToHashSet() : new HashSet<TableRef>();

    private static string Named(string? name, string fallback) => name is { Length: > 0 } ? name : fallback;

    private void Update(QueryNode node, Func<QueryNodeState, QueryNodeState> change) => Set(State.Update(node.Path, change));

    private void Set(QueryDesignState state)
    {
        tab.Design = state;
        Resolve(keepNotes: false);
    }

    private void Resolve(bool keepNotes)
    {
        if (_graph is null)
        {
            return;
        }

        var design = QueryDesign.Resolve(Root, State, _graph).WithColumnsOf(DetailsOf);
        if (!ReferenceEquals(tab.Design, null) || design.Notes.Count > 0)
        {
            tab.Design = design.State with { Active = State.Active, Names = State.Names };
        }

        Notes = keepNotes ? [.. Notes.Concat(design.Notes).Distinct()] : design.Notes;
        Design = design;
    }
}
