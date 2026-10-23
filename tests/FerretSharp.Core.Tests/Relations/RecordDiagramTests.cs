using FerretSharp.Core.Data;
using FerretSharp.Core.Relations;
using FerretSharp.Core.Schema;
using NSubstitute;
using static FerretSharp.Core.Tests.Relations.MemoryTables;

namespace FerretSharp.Core.Tests.Relations;

public class RecordDiagramTests
{
    private readonly MemoryTables _db = new();
    private readonly TableDetails _kunde;
    private readonly TableDetails _auftrag;
    private readonly TableDetails _position;
    private readonly TableDetails _mitarbeiter;

    private readonly ForeignKeyInfo _auftragKunde;
    private readonly ForeignKeyInfo _auftragBearbeiter;
    private readonly ForeignKeyInfo _positionAuftrag;
    private readonly ForeignKeyInfo _vorgesetzter;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public RecordDiagramTests()
    {
        _kunde = _db.Add(Table("KUNDE", ["ID"], Col("ID", nullable: false), Col("NAME", "VARCHAR2")),
            [1m, "Peters Pizza"], [2m, "Anna Apfel"]);
        _mitarbeiter = _db.Add(Table("MITARBEITER", ["ID"], Col("ID", nullable: false), Col("VORGESETZTER_ID")),
            [10m, null], [11m, 10m], [12m, 11m], [13m, 10m]);
        _auftrag = _db.Add(Table("AUFTRAG", ["ID"], Col("ID", nullable: false), Col("KUNDE_ID"), Col("BEARBEITER_ID")),
            [100m, 1m, 12m], [101m, 1m, null], [102m, 2m, 13m]);
        _position = _db.Add(Table("POSITION", ["AUFTRAG_ID", "POS_NR"], Col("AUFTRAG_ID", nullable: false), Col("POS_NR", nullable: false)),
            [100m, 1m], [100m, 2m], [101m, 1m]);

        _auftragKunde = new("FK_AUFTRAG_KUNDE", _auftrag.Table.Ref, ["KUNDE_ID"], _kunde.Table.Ref, ["ID"], FkSource.Declared);
        _auftragBearbeiter = new("Auftrag.Bearbeiter", _auftrag.Table.Ref, ["BEARBEITER_ID"], _mitarbeiter.Table.Ref, ["ID"], FkSource.ClrModel);
        _positionAuftrag = new("FK_POSITION_AUFTRAG", _position.Table.Ref, ["AUFTRAG_ID"], _auftrag.Table.Ref, ["ID"], FkSource.Declared);
        _vorgesetzter = new("FK_MA_VORGESETZTER", _mitarbeiter.Table.Ref, ["VORGESETZTER_ID"], _mitarbeiter.Table.Ref, ["ID"], FkSource.Declared);
    }

    private RelationGraph Relations => new([_auftragKunde, _auftragBearbeiter, _positionAuftrag, _vorgesetzter]);

    private Task<RecordDiagram> StartAsync(TableDetails table, object[][] keys, int maxRows = RecordDiagram.DefaultMaxRows, int groupLimit = RecordDiagram.DefaultGroupLimit) =>
        RecordDiagram.StartAsync(new RowTraversal(_db.Data, Relations, _db.DetailsOf, wholeLobs: false, maxRows), Relations, table,
            [.. keys.Select(k => (RowKey)new RowKey.PrimaryKey(k))], Ct, groupLimit);

    private static RecordNode Node(TableDetails table, params object[] key) => new(table.Table.Ref, new RowKey.PrimaryKey(key));

    private static int LayerOf(RecordDiagram diagram, RecordNode node) => diagram.Rows.Single(r => r.Node == node).Layer;

    [Fact]
    public async Task The_start_rows_stand_in_the_middle_with_their_relationships_on_the_handles()
    {
        var diagram = await StartAsync(_auftrag, [[100m], [101m], [999m]]);

        Assert.Equal([Node(_auftrag, 100m), Node(_auftrag, 101m)], diagram.Rows.Select(r => r.Node)); // 999 does not exist
        Assert.All(diagram.Rows, r => Assert.Equal(0, r.Layer));
        Assert.Equal([_auftragBearbeiter, _auftragKunde], diagram.Pending(Node(_auftrag, 100m), DiagramSide.Parents).OrderBy(r => r.Name, StringComparer.Ordinal));
        Assert.Equal([_auftragKunde], diagram.Pending(Node(_auftrag, 101m), DiagramSide.Parents)); // no clerk: NULL references nothing
        Assert.Equal([_positionAuftrag], diagram.Pending(Node(_auftrag, 100m), DiagramSide.Children));
        Assert.Empty(diagram.Edges);
    }

    [Fact]
    public async Task Expanding_children_shows_them_one_column_to_the_right_with_their_parent_known()
    {
        var diagram = await StartAsync(_kunde, [[1m]]);

        var shown = await diagram.ExpandAsync([Node(_kunde, 1m)], DiagramSide.Children, null, Ct);

        Assert.Equal([Node(_auftrag, 100m), Node(_auftrag, 101m)], shown);
        Assert.Equal(1, LayerOf(diagram, Node(_auftrag, 100m)));
        Assert.Equal(2, diagram.Edges.Count);
        Assert.All(diagram.Edges, e => Assert.Equal(Node(_kunde, 1m), e.Parent));
        Assert.Empty(diagram.Pending(Node(_kunde, 1m), DiagramSide.Children));
        // The order's customer is the row it was reached from; the clerk is still to be read.
        Assert.Equal([_auftragBearbeiter], diagram.Pending(Node(_auftrag, 100m), DiagramSide.Parents));
    }

    [Fact]
    public async Task Expanding_parents_links_a_shown_row_without_reading_it_and_puts_new_ones_to_the_left()
    {
        var diagram = await StartAsync(_kunde, [[1m]]);
        await diagram.ExpandAsync([Node(_kunde, 1m)], DiagramSide.Children, null, Ct);
        var reads = _db.Reads.Count;

        var shown = await diagram.ExpandAsync([Node(_auftrag, 100m)], DiagramSide.Parents, null, Ct);

        Assert.Equal([Node(_mitarbeiter, 12m)], shown);
        Assert.Equal(0, LayerOf(diagram, Node(_mitarbeiter, 12m))); // one column left of the order
        Assert.Equal(reads + 1, _db.Reads.Count); // the clerk only
        Assert.Contains(diagram.Edges, e => e.Relation == _auftragBearbeiter && e.Parent == Node(_mitarbeiter, 12m));
    }

    [Fact]
    public async Task One_relationship_can_be_followed_alone()
    {
        var diagram = await StartAsync(_auftrag, [[100m]]);

        await diagram.ExpandAsync(Node(_auftrag, 100m), _auftragKunde, DiagramSide.Parents, null, Ct);

        Assert.Equal([Node(_auftrag, 100m), Node(_kunde, 1m)], diagram.Rows.Select(r => r.Node));
        Assert.Equal([_auftragBearbeiter], diagram.Pending(Node(_auftrag, 100m), DiagramSide.Parents));
        Assert.Equal([new RecordStep(RecordStepKind.Expand, [Node(_auftrag, 100m).Id], DiagramSide.Parents, RelationGraph.KeyOf(_auftragKunde))],
            diagram.Steps, StepComparer.Instance);
    }

    [Fact]
    public async Task A_self_reference_puts_the_manager_to_the_left_and_the_reports_to_the_right()
    {
        var diagram = await StartAsync(_mitarbeiter, [[11m]]);

        await diagram.ExpandAsync([Node(_mitarbeiter, 11m)], DiagramSide.Parents, null, Ct);
        await diagram.ExpandAsync([Node(_mitarbeiter, 11m)], DiagramSide.Children, null, Ct);

        Assert.Equal(-1, LayerOf(diagram, Node(_mitarbeiter, 10m)));
        Assert.Equal(1, LayerOf(diagram, Node(_mitarbeiter, 12m)));
        Assert.Equal(2, diagram.Edges.Count);
    }

    [Fact]
    public async Task Many_rows_along_one_relationship_become_a_group_until_opened()
    {
        var diagram = await StartAsync(_kunde, [[1m]], groupLimit: 1);

        var shown = await diagram.ExpandAsync([Node(_kunde, 1m)], DiagramSide.Children, null, Ct);

        Assert.Empty(shown);
        var group = Assert.Single(diagram.Groups);
        Assert.Equal(_auftrag.Table.Ref, group.Table);
        Assert.Equal(2, diagram.Members(group).Count);
        var (nodes, edges) = diagram.ToLayout();
        Assert.Contains(nodes, n => n.Id == group.Id && n.Layer == 1);
        Assert.Contains(edges, e => e.From == Node(_kunde, 1m).Id && e.To == group.Id);

        Assert.Equal(2, diagram.Open(group).Count);
        Assert.Empty(diagram.Groups);
        Assert.Equal(1, LayerOf(diagram, Node(_auftrag, 101m)));
    }

    [Fact]
    public async Task A_relationship_past_the_limit_is_reported_and_leaves_the_diagram_as_it_was()
    {
        var diagram = await StartAsync(_kunde, [[1m]], maxRows: 2);

        var shown = await diagram.ExpandAsync([Node(_kunde, 1m)], DiagramSide.Children, null, Ct);

        Assert.Empty(shown);
        Assert.Equal(1, diagram.RowCount); // nothing of the orders went in
        var problem = Assert.Single(diagram.Problems);
        Assert.True(problem.TooMany);
        Assert.Equal(_auftragKunde, problem.Relation);
        Assert.Equal([Node(_kunde, 1m)], problem.From);
        Assert.Empty(diagram.Pending(Node(_kunde, 1m), DiagramSide.Children)); // not offered again
    }

    [Fact]
    public async Task Reading_stops_one_row_past_the_room_left()
    {
        var diagram = await StartAsync(_kunde, [[1m]], maxRows: 2);

        await diagram.ExpandAsync([Node(_kunde, 1m)], DiagramSide.Children, null, Ct);

        // One row of two is taken by the customer, no order is known yet: one fits, the second read says there are more.
        Assert.Equal(2, LastReadLimit());
    }

    /// <summary>Review: rows the graph has already come back along the relationship too, and take no room.</summary>
    [Fact]
    public async Task Rows_the_graph_has_already_do_not_count_against_the_limit()
    {
        var diagram = await StartAsync(_auftrag, [[100m], [101m]], maxRows: 3);
        await diagram.ExpandAsync(Node(_auftrag, 100m), _auftragKunde, DiagramSide.Parents, null, Ct); // the customer: 3 rows

        await diagram.ExpandAsync([Node(_kunde, 1m)], DiagramSide.Children, null, Ct); // orders 100 and 101, both known

        Assert.Empty(diagram.Problems);
        Assert.Equal(3, diagram.RowCount);
        Assert.Equal(3, LastReadLimit()); // no room left, but the two known orders may come back
    }

    /// <summary>Review: a replay went over the list the diagram kept adding to.</summary>
    [Fact]
    public async Task The_steps_are_a_snapshot_that_later_steps_leave_alone()
    {
        var diagram = await StartAsync(_kunde, [[1m]]);
        await diagram.ExpandAsync([Node(_kunde, 1m)], DiagramSide.Children, null, Ct);
        var steps = diagram.Steps;

        await diagram.ExpandAsync([Node(_auftrag, 100m)], DiagramSide.Children, null, Ct);

        Assert.Single(steps);
        Assert.Equal(2, diagram.Steps.Count);
        Assert.Same(diagram.Steps, diagram.Steps); // taken once until the next step
    }

    private int? LastReadLimit() =>
        _db.Data.ReceivedCalls().Where(c => c.GetMethodInfo().Name == nameof(IDataAccess.ReadByKeysAsync)).Select(c => (int?)c.GetArguments()[4]).Last();

    [Fact]
    public async Task The_steps_build_the_same_diagram_again_and_skip_what_is_gone()
    {
        var first = await StartAsync(_kunde, [[1m]], groupLimit: 1);
        await first.ExpandAsync([Node(_kunde, 1m)], DiagramSide.Children, null, Ct);
        first.Open(first.Groups[0]);
        await first.ExpandAsync([Node(_auftrag, 100m)], DiagramSide.Children, null, Ct);
        await first.ExpandAsync(Node(_auftrag, 100m), _auftragBearbeiter, DiagramSide.Parents, null, Ct);

        var again = await StartAsync(_kunde, [[1m]], groupLimit: 1);
        await again.ReplayAsync([.. first.Steps, new RecordStep(RecordStepKind.Expand, ["r:APP.GONE:1"], DiagramSide.Parents)], null, Ct);

        Assert.Equal(first.Rows, again.Rows);
        Assert.Equal(first.Edges.Select(e => e.Id), again.Edges.Select(e => e.Id));
        Assert.Equal(first.Steps, again.Steps, StepComparer.Instance);
    }

    [Fact]
    public void A_node_id_is_the_same_for_equal_numbers_of_another_scale()
    {
        Assert.Equal(Node(_kunde, 5m).Id, Node(_kunde, 5.00m).Id);
        Assert.Equal(Node(_kunde, 5m), Node(_kunde, 5.00m));
        Assert.DoesNotContain('\u0001', Node(_position, 100m, 1m).Id); // the layout marks its waypoints with it
    }

    [Fact]
    public async Task Mermaid_writes_rows_as_nodes_and_links_from_parent_to_child()
    {
        var diagram = await StartAsync(_auftrag, [[100m]]);
        await diagram.ExpandAsync([Node(_auftrag, 100m)], DiagramSide.Parents, null, Ct);

        var text = RecordMermaid.Write(diagram, n => $"{n.Table.Name} \"{((RowKey.PrimaryKey)n.Key).Values[0]}\"", t => t.Name);

        Assert.Equal("""
            flowchart LR
                r1["AUFTRAG #quot;100#quot;"]
                r2["MITARBEITER #quot;12#quot;"]
                r3["KUNDE #quot;1#quot;"]
                r2 -.->|"Auftrag.Bearbeiter"| r1
                r3 -->|"FK_AUFTRAG_KUNDE"| r1

            """.ReplaceLineEndings("\n"), text);
    }

    [Fact]
    public async Task Mermaid_writes_a_group_that_was_not_opened_as_one_node()
    {
        var diagram = await StartAsync(_kunde, [[1m]], groupLimit: 1);
        await diagram.ExpandAsync([Node(_kunde, 1m)], DiagramSide.Children, null, Ct);

        var text = RecordMermaid.Write(diagram, n => n.Table.Name, t => t.Name);

        Assert.Contains("    g2[\"AUFTRAG ×2\"]\n", text, StringComparison.Ordinal);
        Assert.Contains("    r1 -->|\"FK_AUFTRAG_KUNDE\"| g2\n", text, StringComparison.Ordinal);
    }

    /// <summary>Steps by value: the record compares its list by reference.</summary>
    private sealed class StepComparer : IEqualityComparer<RecordStep>
    {
        public static readonly StepComparer Instance = new();

        public bool Equals(RecordStep? x, RecordStep? y) =>
            x is not null && y is not null && x.Kind == y.Kind && x.Side == y.Side && x.Relation == y.Relation && x.Nodes.SequenceEqual(y.Nodes);

        public int GetHashCode(RecordStep step) => HashCode.Combine(step.Kind, step.Side, step.Relation);
    }
}
