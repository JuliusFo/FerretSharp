using FerretSharp.Core.Relations;
using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Tests.Relations;

public class RelationPathsTests
{
    private const string Owner = "APP";
    private static readonly TableRef Kunde = new(Owner, "KUNDE");
    private static readonly TableRef Auftrag = new(Owner, "AUFTRAG");
    private static readonly TableRef Position = new(Owner, "POSITION");
    private static readonly TableRef Artikel = new(Owner, "ARTIKEL");
    private static readonly TableRef Lieferant = new(Owner, "LIEFERANT");
    private static readonly TableRef ArtikelLieferant = new(Owner, "ARTIKEL_LIEFERANT");
    private static readonly TableRef Benutzer = new(Owner, "BENUTZER");
    private static readonly TableRef Insel = new(Owner, "INSEL");

    private static ForeignKeyInfo Fk(string name, TableRef from, TableRef to, FkSource source = FkSource.Declared) =>
        new(name, from, [to.Name + "_ID"], to, ["ID"], source);

    private static readonly ForeignKeyInfo AuftragKunde = Fk("FK_AUFTRAG_KUNDE", Auftrag, Kunde);
    private static readonly ForeignKeyInfo PositionAuftrag = Fk("FK_POSITION_AUFTRAG", Position, Auftrag);
    private static readonly ForeignKeyInfo PositionArtikel = Fk("FK_POSITION_ARTIKEL", Position, Artikel);
    private static readonly ForeignKeyInfo ArtikelHersteller = Fk("Artikel.Hersteller", Artikel, Lieferant, FkSource.ClrModel);
    private static readonly ForeignKeyInfo AlArtikel = Fk("FK_AL_ARTIKEL", ArtikelLieferant, Artikel);
    private static readonly ForeignKeyInfo AlLieferant = Fk("FK_AL_LIEFERANT", ArtikelLieferant, Lieferant);
    private static readonly ForeignKeyInfo AuftragAngelegt = Fk("FK_AUFTRAG_BENUTZER", Auftrag, Benutzer);
    private static readonly ForeignKeyInfo LieferantAngelegt = Fk("FK_LIEFERANT_BENUTZER", Lieferant, Benutzer);
    private static readonly ForeignKeyInfo KundeWerber = Fk("FK_KUNDE_WERBER", Kunde, Kunde);

    private static readonly RelationGraph Graph = new(
        [AuftragKunde, PositionAuftrag, PositionArtikel, ArtikelHersteller, AlArtikel, AlLieferant, AuftragAngelegt, LieferantAngelegt, KundeWerber]);

    private static List<RelationPath> All(RelationGraph graph, TableRef from, TableRef to, IReadOnlySet<TableRef>? avoid = null) =>
        [.. new RelationPathSearch(graph, from, to, avoid).Next(int.MaxValue, TestContext.Current.CancellationToken)];

    private static string Show(RelationPath path) =>
        path.Start.Name + string.Concat(path.Steps.Select(s => (s.ToChild ? " v " : " ^ ") + s.To.Name));

    [Fact]
    public void Finds_every_way_shortest_first_and_ends_by_itself()
    {
        var search = new RelationPathSearch(Graph, Position, Lieferant);

        var paths = search.Next(int.MaxValue, TestContext.Current.CancellationToken);

        Assert.True(search.Done);
        Assert.Equal(
            [
                "POSITION ^ ARTIKEL ^ LIEFERANT",
                "POSITION ^ ARTIKEL v ARTIKEL_LIEFERANT ^ LIEFERANT",
                "POSITION ^ AUFTRAG ^ BENUTZER v LIEFERANT",
            ],
            paths.Select(Show));
        Assert.Equal(3, search.Found);
        Assert.Equal([2, 3, 3], paths.Select(p => p.Length));
    }

    [Fact]
    public void Follows_relationships_both_ways_but_never_a_table_twice()
    {
        var paths = All(Graph, Kunde, Artikel);

        Assert.Contains("KUNDE v AUFTRAG v POSITION ^ ARTIKEL", paths.Select(Show));
        Assert.All(paths, p => Assert.Equal(p.Tables.Count(), p.Tables.Distinct().Count()));
    }

    [Fact]
    public void Leaves_out_self_references()
    {
        var paths = All(Graph, Kunde, Auftrag);

        Assert.Equal("KUNDE v AUFTRAG", Show(paths[0]));
        Assert.DoesNotContain(paths, p => p.Steps.Any(s => s.Relation == KundeWerber));
    }

    [Fact]
    public void Avoided_tables_are_not_passed_through()
    {
        var paths = All(Graph, Position, Lieferant, new HashSet<TableRef> { Benutzer });

        Assert.Equal(2, paths.Count);
        Assert.DoesNotContain(paths, p => p.Tables.Contains(Benutzer));
    }

    [Fact]
    public void An_avoided_start_or_target_still_counts()
    {
        var paths = All(Graph, Auftrag, Benutzer, new HashSet<TableRef> { Auftrag, Benutzer });

        Assert.Equal("AUFTRAG ^ BENUTZER", Show(paths[0]));
    }

    [Fact]
    public void Two_relationships_between_the_same_tables_make_two_ways()
    {
        var rechnung = Fk("FK_AUFTRAG_RECHNUNGSKUNDE", Auftrag, Kunde);
        var paths = All(new RelationGraph([AuftragKunde, rechnung]), Auftrag, Kunde);

        Assert.Equal(2, paths.Count);
        Assert.NotEqual(paths[0].Key, paths[1].Key);
    }

    /// <summary>Review of WP-74: two relationships of the C# model with the same name on the same table had the same key (Blazor @key).</summary>
    [Fact]
    public void Two_relationships_with_the_same_key_make_two_ways_with_their_own_keys()
    {
        var current = new ForeignKeyInfo("Position.Auftrag", Position, ["AUFTRAG_ID"], Auftrag, ["ID"], FkSource.ClrModel);
        var former = new ForeignKeyInfo("Position.Auftrag", Position, ["ALT_AUFTRAG_ID"], Auftrag, ["ID"], FkSource.ClrModel);
        Assert.Equal(RelationGraph.KeyOf(current), RelationGraph.KeyOf(former));

        var paths = All(new RelationGraph([current, former]), Position, Auftrag);

        Assert.Equal(2, paths.Count);
        Assert.NotEqual(paths[0].Key, paths[1].Key);

        // The same columns to two keys of the table (second review pass).
        var byNumber = new ForeignKeyInfo("Position.Auftrag", Position, ["AUFTRAG_ID"], Auftrag, ["NR"], FkSource.ClrModel);
        var both = All(new RelationGraph([current, byNumber]), Position, Auftrag);
        Assert.Equal(2, both.Count);
        Assert.NotEqual(both[0].Key, both[1].Key);
    }

    /// <summary>
    /// Review of WP-74: the distances ran through the start, so a table hanging only on the start was entered at every
    /// length – the search went on lengths after the last way before it saw that all were found.
    /// </summary>
    [Fact]
    public void A_table_only_behind_the_start_does_not_keep_the_search_going()
    {
        var behind = new TableRef(Owner, "NOTIZ");
        var below = new TableRef(Owner, "NOTIZ_ANHANG");
        var graph = new RelationGraph([AuftragKunde, Fk("FK_NOTIZ_AUFTRAG", behind, Auftrag), Fk("FK_ANHANG_NOTIZ", below, behind)]);
        var search = new RelationPathSearch(graph, Auftrag, Kunde);

        Assert.Single(search.Next(10, TestContext.Current.CancellationToken));
        Assert.True(search.Done);
        Assert.Equal(1, search.Length);
    }

    [Fact]
    public void No_way_to_an_unconnected_table_or_to_itself()
    {
        Assert.Empty(All(Graph, Kunde, Insel));
        Assert.Empty(All(Graph, Kunde, Kunde));
        var search = new RelationPathSearch(Graph, Kunde, Insel);
        search.Next(10, TestContext.Current.CancellationToken);
        Assert.True(search.Done);
    }

    [Fact]
    public void Hands_out_blocks_and_goes_on_where_it_stopped()
    {
        var search = new RelationPathSearch(Graph, Position, Lieferant);

        var first = search.Next(1, TestContext.Current.CancellationToken);
        Assert.False(search.Done);
        var rest = search.Next(10, TestContext.Current.CancellationToken);

        Assert.Single(first);
        Assert.Equal(2, rest.Count);
        Assert.True(search.Done);
        Assert.Equal(All(Graph, Position, Lieferant).Select(p => p.Key), first.Concat(rest).Select(p => p.Key));
    }

    [Fact]
    public void A_cancelled_block_can_be_continued()
    {
        var search = new RelationPathSearch(Graph, Position, Lieferant);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => search.Next(10, cancelled.Token));
        Assert.Equal(3, search.Next(10, TestContext.Current.CancellationToken).Count);
    }

    [Fact]
    public void Long_ways_have_no_limit()
    {
        // A chain of 30 tables: the only way has 29 steps.
        var tables = Enumerable.Range(0, 30).Select(i => new TableRef(Owner, $"T{i:00}")).ToList();
        var chain = new RelationGraph(tables.Skip(1).Select((t, i) => Fk($"FK_{i}", t, tables[i])));

        var paths = All(chain, tables[0], tables[^1]);

        Assert.Equal(29, Assert.Single(paths).Length);
    }

    [Fact]
    public void Counts_every_way_of_a_dense_graph()
    {
        // A complete graph of 6 tables: from one to another 1 + 4 + 4·3 + 4·3·2 + 4·3·2·1 = 65 simple ways.
        var tables = Enumerable.Range(0, 6).Select(i => new TableRef(Owner, $"K{i}")).ToList();
        var relations = from a in tables from b in tables where string.CompareOrdinal(a.Name, b.Name) < 0 select Fk($"FK_{a.Name}_{b.Name}", a, b);
        var graph = new RelationGraph(relations);

        var paths = All(graph, tables[0], tables[5]);

        Assert.Equal(65, paths.Count);
        Assert.Equal(paths.Count, paths.Select(p => p.Key).Distinct().Count());
        Assert.Equal(paths.Select(p => p.Length).Order(), paths.Select(p => p.Length));
    }

    [Fact]
    public void Orders_ways_of_one_length_by_rows_then_kind()
    {
        var paths = All(Graph, Position, Lieferant);
        var sorted = paths.OrderBy(p => p, Comparer<RelationPath>.Create(RelationPath.Compare)).ToList();

        Assert.True(paths[0].SingleRow);
        Assert.Equal(1, paths[0].SourceRank); // the C# model's relationship
        Assert.False(sorted[1].SingleRow);
        Assert.Equal(0, sorted[1].SourceRank);
    }

    [Fact]
    public void Steps_read_as_columns_from_the_table_they_start_from()
    {
        var path = All(Graph, Kunde, Position)[0];

        Assert.Equal("KUNDE.ID → AUFTRAG.KUNDE_ID\nAUFTRAG.ID → POSITION.AUFTRAG_ID", path.Text(t => t.Name));
        var multi = new PathStep(new ForeignKeyInfo("FK", Position, ["A", "B"], Auftrag, ["X", "Y"], FkSource.Declared), false, Position, Auftrag);
        Assert.Equal("POSITION.(A, B) → AUFTRAG.(X, Y)", multi.Text(t => t.Name));
    }

    [Fact]
    public void Becomes_a_query_with_a_node_for_each_part()
    {
        var path = All(Graph, Kunde, Position)[0];

        var query = path.ToQuery();

        Assert.True(query.Active);
        Assert.Equal([0, 1, 2], query.Nodes.Select(n => n.Path.Count));
        Assert.Equal(path.QueryPath, query.Nodes[2].Path);
        Assert.All(query.Nodes.Skip(1), n => Assert.True(n.Path[^1].ToChild));
        var design = QueryDesign.Resolve(Kunde, query, Graph);
        Assert.Empty(design.Notes);
        Assert.Equal(Position, design.Nodes[^1].Table);
    }

    [Fact]
    public void Added_to_a_query_keeps_what_it_has()
    {
        var path = All(Graph, Kunde, Position)[0];
        var existing = QueryDesignState.Empty.Add([], path.Steps[0].Query) is var state
            ? state.Update([path.Steps[0].Query], n => n with { Columns = [new QueryColumn("NR")] })
            : null;

        var query = path.ToQuery(existing);

        Assert.Equal(3, query.Nodes.Count);
        Assert.Equal("NR", Assert.Single(query.Nodes[1].Columns).Column);
    }
}
