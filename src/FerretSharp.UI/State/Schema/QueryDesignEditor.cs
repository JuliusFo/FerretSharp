using FerretSharp.Core.ClrModel;
using FerretSharp.Core.Query;
using FerretSharp.Core.Relations;
using FerretSharp.Core.Resources;
using FerretSharp.Core.Schema;

namespace FerretSharp.UI.State;

/// <summary>
/// The mode "Abfrage bauen" of a table tab's relationship view (WP-69): the query of the tab (<see cref="TableTab.Design"/>,
/// saved with it), resolved against the schema's relationships, the node the side panel shows, and the columns of the
/// query's tables as far as they are loaded. Every change goes into the tab at once – a query grows over days. What the
/// schema does not have (now) stays in the tab and is only left out: the C# model brings its relationships later, and a
/// query restored before must not lose its steps over them (review). <see cref="DropMissing"/> takes it out.
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

    /// <summary>What of the saved query the schema does not have (a relationship, a column) and is left out.</summary>
    public IReadOnlyList<string> Notes => Design?.Notes ?? [];

    public bool Active => State.Active;

    /// <summary>The node the side panel shows: the one chosen last, the root if it is gone.</summary>
    public QueryNode? Selected => Design?.Find(_selected) ?? Design?.Nodes[0];

    /// <summary>Resolves the tab's query against the schema's relationships (the schema reloaded, a new C# model).</summary>
    public void Load(RelationGraph graph)
    {
        _graph = graph;
        Resolve();
    }

    public void SetActive(bool active) => Set(State with { Active = active });

    /// <summary>A way between two tables as the query (WP-74), replacing it or added to it (<see cref="RelationPath.ToQuery"/>); its target is selected.</summary>
    public void TakePath(RelationPath path, bool replace)
    {
        Set(path.ToQuery(replace ? null : State));
        _selected = path.QueryPath;
    }

    /// <summary>The query has more than its root: taking a way asks whether to replace it.</summary>
    public bool HasSteps => State.Nodes.Count > 1;

    public void Select(QueryNode node) => _selected = node.Path;

    /// <summary>
    /// A click on an edge of the diagram: the selected node goes out if it came over it; otherwise the step from the
    /// selected node if the relationship touches its table, else from the first node that it touches – never back over the
    /// relationship a node came over. A step the query has already is taken out (with what hangs on it), a new one is added
    /// and selected.
    /// </summary>
    /// <returns>Whether the query changed (the relationship touches none of its tables otherwise).</returns>
    public bool ToggleEdge(ForeignKeyInfo relation)
    {
        if (Design is not { } design)
        {
            return false;
        }

        // The selected node came over this edge: a second click takes it out again (review: it added the way back).
        var key = RelationGraph.KeyOf(relation);
        if (Selected is { Step: { } incoming } current && incoming.Relation == key)
        {
            Remove(current);
            return true;
        }

        // From a node the edge touches, but never back over the relationship that led to it (as NextSteps).
        bool From(QueryNode node) =>
            QueryDesign.StepFrom(node.Table, relation) is { } s && !(node.Step is { } back && back.Relation == key && back.ToChild != s.ToChild);
        var from = Selected is { } selected && From(selected) ? selected : design.Nodes.FirstOrDefault(From);
        if (from is null || QueryDesign.StepFrom(from.Table, relation) is not { } step)
        {
            return false;
        }

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

    /// <summary>
    /// The node's conditions as the panel shows them; those on a column the table does not have (now) stay in the tab
    /// (review: editing another condition dropped them, without "Remove from the query").
    /// </summary>
    public void SetFilters(QueryNode node, IReadOnlyList<QueryFilter> filters) =>
        Update(node, n => n with { Filters = [.. filters, .. n.Filters.Where(f => IsMissing(node, f.Condition.Column))] });

    /// <summary>The order of a chosen child as the panel shows it; entries on a missing column stay (as <see cref="SetFilters"/>).</summary>
    public void SetSorts(QueryNode node, IReadOnlyList<SortSpec> sorts) =>
        Update(node, n => n with { Sorts = [.. sorts, .. n.Sorts.Where(s => IsMissing(node, s.Column))] });

    private bool IsMissing(QueryNode node, string column) => DetailsOf(node.Table) is { } details && details.IndexOf(column) < 0;

    public void SetNames(QueryNames names) => Set(State with { Names = names });

    /// <summary>
    /// A parameter named after the column (<c>KUNDE_ID</c> → <c>kundeId</c>), with a digit if a condition of the query has
    /// that name already – two conditions on one column are a range (review: <c>Filter(DateTime datum, DateTime datum)</c>).
    /// </summary>
    public string DefaultParameter(string column)
    {
        var parts = column.Split('_', StringSplitOptions.RemoveEmptyEntries).Select(p => p.ToLowerInvariant()).ToList();
        var name = string.Concat(parts.Select((p, i) => i == 0 ? p : char.ToUpperInvariant(p[0]) + p[1..]));
        name = QueryCodeBuilder.IsName(name) ? name : "value";
        var taken = State.Nodes.SelectMany(n => n.Filters).Select(f => f.Parameter).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var unique = name;
        for (var n = 2; taken.Contains(unique); n++)
        {
            unique = name + n.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return unique;
    }

    // --- Columns of the query's tables ------------------------------------------------------------------------------------

    /// <summary>The query's tables whose columns are not loaded yet.</summary>
    public IReadOnlyList<TableRef> Missing => Design is { } design ? [.. design.Nodes.Select(n => n.Table).Distinct().Where(t => !_details.ContainsKey(t))] : [];

    public TableDetails? DetailsOf(TableRef table) => _details.GetValueOrDefault(table);

    /// <summary>Columns of a table came: what the saved query names and the table does not have is left out (with a note).</summary>
    public void Loaded(TableDetails details)
    {
        _details[details.Table.Ref] = details;
        Resolve();
    }

    /// <summary>Takes what is left out (<see cref="Notes"/>) out of the tab's query.</summary>
    public void DropMissing()
    {
        if (Design is { Notes.Count: > 0 } design)
        {
            Set(design.State with { Active = State.Active, Names = State.Names });
        }
    }

    /// <summary>Reading the columns failed (the view sets it): the panel offers to try again instead of waiting (review).</summary>
    public bool ColumnsFailed { get; set; }

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
        Design?.Nodes.Select(n => DetailsOf(n.Table) is { } details && NodeProblem(n, details) is { } error
                ? TextFormat.Format(QueryText.DesignNodeProblem, QueryDesign.Label(n), error)
                : null)
            .FirstOrDefault(p => p is not null);

    /// <summary>
    /// A condition the grid would refuse, or a value or order the column no longer allows – its type changed since, or the
    /// workspace file was edited (review: the SQL threw while the panel rendered, and the tab with it).
    /// </summary>
    private static string? NodeProblem(QueryNode node, TableDetails details)
    {
        if (QueryBuilder.Validate(details, [.. node.State.Filters.Select(f => f.Condition)]).Values.FirstOrDefault() is { } error)
        {
            return error;
        }

        ColumnInfo? Column(string name) => details.Columns.FirstOrDefault(c => c.Name == name);
        if (node.State.Columns.Select(c => Column(c.Column)).FirstOrDefault(c => c is not null && !Selectable(c)) is { } value)
        {
            return TextFormat.Format(QueryText.DesignColumnNoValue, value.Name, OracleTypes.DisplayType(value));
        }

        return node.State.Sorts.FirstOrDefault(s => Column(s.Column) is { } c && !ColumnCategories.IsSortable(ColumnCategories.Of(c))) is { } sort
            ? TextFormat.Format(QueryText.ColumnNotSortable, sort.Column)
            : null;
    }

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
        Resolve();
    }

    private void Resolve()
    {
        if (_graph is not null)
        {
            Design = QueryDesign.Resolve(Root, State, _graph).WithColumnsOf(DetailsOf);
        }
    }
}
