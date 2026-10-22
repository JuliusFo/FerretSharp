using FerretSharp.Core.Data;
using FerretSharp.Core.Relations;
using FerretSharp.Core.Schema;
using static FerretSharp.Core.Tests.Relations.MemoryTables;

namespace FerretSharp.Core.Tests.Relations;

public class RowTraversalTests
{
    private readonly MemoryTables _db = new();
    private readonly TableDetails _kunde;
    private readonly TableDetails _auftrag;
    private readonly TableDetails _position;
    private readonly TableDetails _artikel;
    private readonly TableDetails _mitarbeiter;

    private readonly ForeignKeyInfo _auftragKunde;
    private readonly ForeignKeyInfo _auftragBearbeiter;
    private readonly ForeignKeyInfo _positionAuftrag;
    private readonly ForeignKeyInfo _positionArtikel;
    private readonly ForeignKeyInfo _vorgesetzter;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public RowTraversalTests()
    {
        _kunde = _db.Add(Table("KUNDE", ["ID"], Col("ID", nullable: false), Col("NAME", "VARCHAR2")),
            [1m, "Peters Pizza"], [2m, "Anna Apfel"]);
        _mitarbeiter = _db.Add(Table("MITARBEITER", ["ID"], Col("ID", nullable: false), Col("VORGESETZTER_ID")),
            [10m, null], [11m, 10m], [12m, 11m], [13m, 10m]);
        _auftrag = _db.Add(Table("AUFTRAG", ["ID"], Col("ID", nullable: false), Col("KUNDE_ID"), Col("BEARBEITER_ID")),
            [100m, 1m, 12m], [101m, 1m, null], [102m, 2m, 13m]);
        _artikel = _db.Add(Table("ARTIKEL", ["NR"], Col("NR", "CHAR", nullable: false, length: 6)),
            ["A-1   "], ["A-2   "], ["A-3   "]);
        _position = _db.Add(Table("POSITION", ["AUFTRAG_ID", "POS_NR"], Col("AUFTRAG_ID", nullable: false), Col("POS_NR", nullable: false), Col("ARTIKEL_NR", "VARCHAR2")),
            [100m, 1m, "A-1"], [100m, 2m, "A-2"], [101m, 1m, "A-1"], [102m, 1m, "A-3"]);

        _auftragKunde = new("FK_AUFTRAG_KUNDE", _auftrag.Table.Ref, ["KUNDE_ID"], _kunde.Table.Ref, ["ID"], FkSource.Declared);
        _auftragBearbeiter = new("Auftrag.Bearbeiter", _auftrag.Table.Ref, ["BEARBEITER_ID"], _mitarbeiter.Table.Ref, ["ID"], FkSource.ClrModel);
        _positionAuftrag = new("FK_POSITION_AUFTRAG", _position.Table.Ref, ["AUFTRAG_ID"], _auftrag.Table.Ref, ["ID"], FkSource.Declared);
        _positionArtikel = new("Position.Artikel", _position.Table.Ref, ["ARTIKEL_NR"], _artikel.Table.Ref, ["NR"], FkSource.ClrModel);
        _vorgesetzter = new("FK_MA_VORGESETZTER", _mitarbeiter.Table.Ref, ["VORGESETZTER_ID"], _mitarbeiter.Table.Ref, ["ID"], FkSource.Declared);
    }

    private RelationGraph Relations => new([_auftragKunde, _auftragBearbeiter, _positionAuftrag, _positionArtikel, _vorgesetzter]);

    private RowTraversal Traversal(int maxRows = RowTraversal.DefaultMaxRows) => new(_db.Data, Relations, _db.DetailsOf, wholeLobs: true, maxRows);

    private static RowKey Key(params object[] values) => new RowKey.PrimaryKey(values);

    private static IEnumerable<object?> Ids(RowGraph graph, TableDetails table) => graph.Of(table.Table.Ref)?.Rows.Select(r => r.Values[0]) ?? [];

    [Fact]
    public async Task By_default_the_children_one_level_deep_and_all_their_parents_up_the_chain_are_collected()
    {
        var graph = await Traversal().CollectAsync(_kunde, [Key(1m)], TraversalPlan.Default(Relations, _kunde.Table.Ref), null, Ct);

        Assert.Equal([1m], Ids(graph, _kunde));
        Assert.Equal([100m, 101m], Ids(graph, _auftrag));
        Assert.Empty(Ids(graph, _position)); // grandchildren only along a longer path
        Assert.Equal([12m, 11m, 10m], Ids(graph, _mitarbeiter)); // the clerk, the clerk's manager, the manager's
        Assert.Equal(6, graph.RowCount);
        Assert.Contains(new RowLink(_auftragKunde, Key(100m), Key(1m)), graph.Links);
        Assert.Contains(new RowLink(_vorgesetzter, Key(11m), Key(10m)), graph.Links);
        Assert.DoesNotContain(graph.Links, l => l.Child.Equals(Key(101m)) && l.Relation == _auftragBearbeiter); // NULL references nothing
        Assert.Empty(graph.Problems);
    }

    [Fact]
    public async Task A_parent_the_graph_holds_already_is_linked_without_reading_it_again()
    {
        await Traversal().CollectAsync(_kunde, [Key(1m)], TraversalPlan.Default(Relations, _kunde.Table.Ref), null, Ct);

        Assert.Single(_db.Reads, r => r.Table == _kunde.Table.Ref); // the start row only
    }

    [Fact]
    public async Task A_longer_path_reaches_grandchildren_and_brings_their_parents_compared_with_and_without_char_padding()
    {
        var plan = new TraversalPlan([[_auftragKunde, _positionAuftrag]], new HashSet<string>());

        var graph = await Traversal().CollectAsync(_kunde, [Key(1m)], plan, null, Ct);

        Assert.Equal([100m, 101m], Ids(graph, _auftrag)); // on the way
        Assert.Equal(3, graph.Of(_position.Table.Ref)!.Rows.Count);
        Assert.Equal(["A-1   ", "A-2   "], Ids(graph, _artikel));
        Assert.Equal(2, graph.Links.Count(l => l.Relation == _positionArtikel && l.Parent.Equals(Key("A-1   "))));
    }

    [Fact]
    public async Task Parents_along_a_skipped_relationship_are_left_out()
    {
        var plan = TraversalPlan.Default(Relations, _kunde.Table.Ref) with { SkippedParents = new HashSet<string> { RelationGraph.KeyOf(_auftragBearbeiter) } };

        var graph = await Traversal().CollectAsync(_kunde, [Key(1m)], plan, null, Ct);

        Assert.Null(graph.Of(_mitarbeiter.Table.Ref));
    }

    [Fact]
    public async Task Following_one_relationship_links_rows_the_graph_had_and_returns_only_new_ones()
    {
        var traversal = Traversal();
        var graph = await traversal.StartAsync(_auftrag, [Key(100m), Key(102m)], Ct);
        var orders = graph.Of(_auftrag.Table.Ref)!.Rows;

        var customers = await traversal.FollowAsync(graph, _auftragKunde, LinkDirection.Parents, orders, Ct);
        var again = await traversal.FollowAsync(graph, _auftragKunde, LinkDirection.Parents, orders, Ct);

        Assert.Equal([1m, 2m], customers.Select(r => r.Values[0]).Order());
        Assert.Empty(again);
        Assert.Equal(2, graph.Links.Count);
    }

    [Fact]
    public async Task Children_of_a_self_reference_are_the_rows_below()
    {
        var traversal = Traversal();
        var graph = await traversal.StartAsync(_mitarbeiter, [Key(10m)], Ct);

        var reports = await traversal.FollowAsync(graph, _vorgesetzter, LinkDirection.Children, graph.Of(_mitarbeiter.Table.Ref)!.Rows, Ct);

        Assert.Equal([11m, 13m], reports.Select(r => r.Values[0]));
        Assert.Contains(new RowLink(_vorgesetzter, Key(13m), Key(10m)), graph.Links);
    }

    [Fact]
    public async Task Numbers_of_another_scale_still_match()
    {
        var traversal = Traversal();
        _db.Add(_kunde, [5.00m, "Scale"]);
        _db.Add(_auftrag, [200m, 5m, null]);
        var graph = await traversal.StartAsync(_auftrag, [Key(200m)], Ct);

        await traversal.FollowAsync(graph, _auftragKunde, LinkDirection.Parents, graph.Of(_auftrag.Table.Ref)!.Rows, Ct);

        Assert.Single(graph.Links);
    }

    [Fact]
    public async Task A_table_the_connection_does_not_know_is_reported_not_followed()
    {
        var elsewhere = new ForeignKeyInfo("FK_AUFTRAG_LAND", _auftrag.Table.Ref, ["KUNDE_ID"], new TableRef("STAMM", "LAND"), ["ID"], FkSource.Declared);
        var traversal = new RowTraversal(_db.Data, new RelationGraph([elsewhere]), _db.DetailsOf, wholeLobs: false);

        var graph = await traversal.CollectAsync(_auftrag, [Key(100m)], TraversalPlan.ParentsOnly, null, Ct);

        var problem = Assert.Single(graph.Problems);
        Assert.Same(elsewhere, problem.Relation);
        Assert.Equal("Beziehung FK_AUFTRAG_LAND wird nicht verfolgt: Die Verbindung kennt die Tabelle STAMM.LAND nicht.", problem.Message);
    }

    [Fact]
    public async Task A_column_missing_on_one_side_is_reported()
    {
        var broken = _auftragKunde with { FromColumns = ["KUNDEN_NR"] };
        var traversal = new RowTraversal(_db.Data, new RelationGraph([broken]), _db.DetailsOf, wholeLobs: false);

        var graph = await traversal.CollectAsync(_auftrag, [Key(100m)], TraversalPlan.ParentsOnly, null, Ct);

        Assert.Contains("KUNDEN_NR", Assert.Single(graph.Problems).Message);
    }

    [Fact]
    public async Task Reading_more_rows_than_the_limit_stops()
    {
        var ex = await Assert.ThrowsAsync<TooManyRowsException>(() =>
            Traversal(maxRows: 3).CollectAsync(_kunde, [Key(1m)], TraversalPlan.Default(Relations, _kunde.Table.Ref), null, Ct));

        Assert.Equal(3, ex.Limit);
    }

    [Fact]
    public async Task Start_rows_are_read_by_rowid_for_a_table_without_key_and_a_deleted_one_is_left_out()
    {
        var log = _db.Add(Table("LOG", [], Col("TEXT", "VARCHAR2")), ["a"], ["b"]);

        var graph = await Traversal().StartAsync(log, [new RowKey.RowId("ROWID-b"), new RowKey.RowId("ROWID-gone")], Ct);

        Assert.Equal(["b"], Ids(graph, log));
    }

    [Fact]
    public async Task Paths_longer_than_three_levels_are_refused()
    {
        var plan = new TraversalPlan([[_auftragKunde, _positionAuftrag, _positionAuftrag, _positionAuftrag]], new HashSet<string>());

        await Assert.ThrowsAsync<ArgumentException>(() => Traversal().CollectAsync(_kunde, [Key(1m)], plan, null, Ct));
    }

    [Fact]
    public void Key_texts_are_equal_for_equal_values_of_different_columns()
    {
        Assert.Equal(RowTraversal.KeyText([5m], trim: false), RowTraversal.KeyText([5.00m], trim: false));
        Assert.Equal(RowTraversal.KeyText(["AB  "], trim: true), RowTraversal.KeyText(["AB"], trim: true));
        Assert.NotEqual(RowTraversal.KeyText(["AB  "], trim: false), RowTraversal.KeyText(["AB"], trim: false));
        Assert.Equal(RowTraversal.KeyText([new byte[] { 1, 2 }], trim: false), RowTraversal.KeyText([new byte[] { 1, 2 }], trim: false));
        Assert.NotEqual(RowTraversal.KeyText([1m, 23m], trim: false), RowTraversal.KeyText([12m, 3m], trim: false));
    }
}
