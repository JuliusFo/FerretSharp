using FerretSharp.Core.Relations;
using FerretSharp.Core.Schema;
using FerretSharp.UI.State;

namespace FerretSharp.UI.Tests.State;

/// <summary>WP-74: the ways of the relationship view – blocks in the background, a new search for each change.</summary>
public sealed class RelationPathsStateTests
{
    private const string Owner = "APP";
    private static readonly TableRef Kunde = new(Owner, "KUNDE");
    private static readonly TableRef Auftrag = new(Owner, "AUFTRAG");
    private static readonly TableRef Position = new(Owner, "POSITION");
    private static readonly TableRef Artikel = new(Owner, "ARTIKEL");
    private static readonly TableRef Benutzer = new(Owner, "BENUTZER");

    private static ForeignKeyInfo Fk(string name, TableRef from, TableRef to, FkSource source = FkSource.Declared) =>
        new(name, from, [to.Name + "_ID"], to, ["ID"], source);

    private static readonly RelationGraph Graph = new([
        Fk("FK_AUFTRAG_KUNDE", Auftrag, Kunde),
        Fk("FK_POSITION_AUFTRAG", Position, Auftrag),
        Fk("FK_POSITION_ARTIKEL", Position, Artikel),
        Fk("FK_AUFTRAG_BENUTZER", Auftrag, Benutzer),
        Fk("FK_ARTIKEL_BENUTZER", Artikel, Benutzer),
        Fk("FK_KUNDE_BENUTZER", Kunde, Benutzer),
    ]);

    private static RelationPathsState Loaded(TableRef? target = null)
    {
        var state = new RelationPathsState(Artikel);
        state.Load(Graph);
        state.SetTarget(target);
        return state;
    }

    private static async Task RunAsync(RelationPathsState state)
    {
        await state.MoreAsync();
        state.Collect();
    }

    private static string Show(RelationPath path) => string.Join(" ", path.Tables.Select(t => t.Name));

    [Fact]
    public async Task A_target_searches_a_block_and_lays_out_the_ways()
    {
        var state = Loaded(Kunde);
        Assert.Empty(state.Paths);
        Assert.NotNull(state.Placement); // start and target before the first way

        await RunAsync(state);

        Assert.Equal(
            ["ARTIKEL BENUTZER KUNDE", "ARTIKEL BENUTZER AUFTRAG KUNDE", "ARTIKEL POSITION AUFTRAG KUNDE", "ARTIKEL POSITION AUFTRAG BENUTZER KUNDE"],
            state.Paths.Select(Show));
        Assert.True(state.Done);
        Assert.False(state.Running);
        Assert.Equal(5, state.Diagram!.Tables.Count);
        Assert.Equal(5, state.Placement!.Nodes.Count);
    }

    [Fact]
    public async Task Avoided_tables_start_a_new_search_without_them()
    {
        var state = Loaded(Kunde);
        await RunAsync(state);
        state.Select(state.Paths[0]);

        state.SetAvoided(new HashSet<TableRef> { Benutzer });

        Assert.Empty(state.Paths);
        Assert.Null(state.Selected);
        await RunAsync(state);
        Assert.Equal(["ARTIKEL POSITION AUFTRAG KUNDE"], state.Paths.Select(Show));
    }

    [Fact]
    public async Task The_same_relationships_again_keep_the_ways()
    {
        var state = Loaded(Kunde);
        await RunAsync(state);

        state.Load(new RelationGraph(Graph.Relations)); // reloaded schema, nothing changed
        state.SetTarget(Kunde);
        state.SetAvoided(new HashSet<TableRef>());

        Assert.Equal(4, state.Paths.Count);
        state.Load(new RelationGraph(Graph.Relations.Where(r => r.Name != "FK_KUNDE_BENUTZER")));
        Assert.Empty(state.Paths);
    }

    [Fact]
    public async Task Without_a_target_or_to_itself_nothing_is_searched()
    {
        var state = Loaded(Artikel);

        Assert.Null(state.Target);
        Assert.Null(state.Placement);
        await state.MoreAsync();
        Assert.False(state.Running);
        Assert.Empty(state.Paths);
    }

    [Fact]
    public async Task A_cancelled_block_keeps_its_ways_and_goes_on_with_more()
    {
        // A complete graph of 8 tables has 1 957 ways between two of them: more than a block.
        var tables = Enumerable.Range(0, 8).Select(i => new TableRef(Owner, $"K{i}")).ToList();
        var dense = new RelationGraph(from a in tables from b in tables where string.CompareOrdinal(a.Name, b.Name) < 0 select Fk($"FK_{a.Name}_{b.Name}", a, b));
        var state = new RelationPathsState(tables[0]);
        state.Load(dense);
        state.SetTarget(tables[7]);

        await RunAsync(state);
        Assert.Equal(RelationPathsState.BlockSize, state.Paths.Count);
        Assert.False(state.Done);

        var run = state.MoreAsync();
        state.Cancel();
        await run;
        state.Collect();
        Assert.True(state.Cancelled || state.Paths.Count == 2 * RelationPathsState.BlockSize);
        var before = state.Paths.Count;

        await RunAsync(state);
        Assert.False(state.Cancelled);
        Assert.True(state.Paths.Count > before);
        Assert.Equal(state.Paths.Count, state.Paths.Select(p => p.Key).Distinct().Count());
        Assert.Equal(state.Paths.Select(p => p.Length).Order(), state.Paths.Select(p => p.Length));
    }

    [Fact]
    public async Task A_block_of_a_replaced_search_ends_unseen()
    {
        var tables = Enumerable.Range(0, 8).Select(i => new TableRef(Owner, $"K{i}")).ToList();
        var dense = new RelationGraph(from a in tables from b in tables where string.CompareOrdinal(a.Name, b.Name) < 0 select Fk($"FK_{a.Name}_{b.Name}", a, b));
        var state = new RelationPathsState(tables[0]);
        state.Load(dense);
        state.SetTarget(tables[7]);

        var old = state.MoreAsync();
        state.SetTarget(tables[1]);
        await old;
        state.Collect();

        Assert.DoesNotContain(state.Paths, p => p.Target == tables[7]);
        Assert.False(state.Cancelled);
        Assert.False(state.Running);
    }

    [Fact]
    public void Candidates_are_found_by_table_and_entity_name_without_the_start()
    {
        TableSummary[] tables =
        [
            new(Owner, "KUNDE", TableKind.Table),
            new(Owner, "KUNDE_ADRESSE", TableKind.Table),
            new(Owner, "AUFTRAG", TableKind.Table),
            new(Owner, "ARTIKEL", TableKind.Table),
        ];
        string? Entity(TableRef t) => t.Name == "AUFTRAG" ? "Bestellung" : null;

        Assert.Equal(["KUNDE", "KUNDE_ADRESSE"], RelationPathsState.Candidates(tables, Entity, "kunde", Artikel).Select(t => t.Name));
        Assert.Equal(["KUNDE_ADRESSE"], RelationPathsState.Candidates(tables, Entity, "kundeadresse", Artikel).Select(t => t.Name));
        Assert.Equal(["AUFTRAG"], RelationPathsState.Candidates(tables, Entity, "bestell", Artikel).Select(t => t.Name));
        Assert.Equal(["AUFTRAG", "KUNDE"], RelationPathsState.Candidates(tables, Entity, "", Artikel, limit: 2).Select(t => t.Name));
    }

    [Fact]
    public async Task A_way_opens_the_neighbours_diagram_along_it_and_becomes_the_query()
    {
        var state = Loaded(Kunde);
        await RunAsync(state);
        var path = state.Paths.Single(p => Show(p) == "ARTIKEL POSITION AUFTRAG KUNDE"); // ↓ ↑ ↑
        var diagram = new RelationsDiagramState(Artikel);
        diagram.Load(Graph);
        diagram.Recenter(Benutzer);
        var tab = new TableTab(Guid.NewGuid(), new TableSummary(Owner, "ARTIKEL", TableKind.Table));
        var query = new QueryDesignEditor(tab);
        query.Load(Graph);

        diagram.ShowPath(path.Steps);
        query.TakePath(path, replace: true);

        Assert.Equal(Artikel, diagram.Focus);
        Assert.All(path.Tables, t => Assert.True(diagram.Diagram!.IsShown(t)));
        Assert.True(query.Active);
        Assert.Equal(Kunde, query.Selected!.Table);
        Assert.Equal(4, query.Design!.Nodes.Count);
        Assert.True(query.HasSteps);

        query.TakePath(state.Paths[0], replace: false);
        Assert.Equal(6, query.Design!.Nodes.Count);
        query.TakePath(state.Paths[0], replace: true);
        Assert.Equal(3, query.Design!.Nodes.Count);
    }
}
