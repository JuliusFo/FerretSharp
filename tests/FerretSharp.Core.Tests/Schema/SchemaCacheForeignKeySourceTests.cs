using FerretSharp.Core.Schema;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace FerretSharp.Core.Tests.Schema;

public class SchemaCacheForeignKeySourceTests
{
    private const string Owner = "APP";
    private static readonly TableRef Kunden = new(Owner, "KUNDEN");
    private static readonly TableRef Auftrag = new(Owner, "AUFTRAG");
    private static readonly TableRef Mitarbeiter = new(Owner, "MITARBEITER");

    private readonly ISchemaReader _reader = Substitute.For<ISchemaReader>();
    private IReadOnlyList<ForeignKeyInfo> _declared = [new("FK_AUFTRAG_KUNDE", Auftrag, ["KUNDE_ID"], Kunden, ["KUNDE_ID"], FkSource.Declared)];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public SchemaCacheForeignKeySourceTests()
    {
        _reader.GetTablesAsync(Owner, Arg.Any<CancellationToken>()).Returns(
            [new TableSummary(Owner, "AUFTRAG", TableKind.Table), new TableSummary(Owner, "KUNDEN", TableKind.Table), new TableSummary(Owner, "MITARBEITER", TableKind.Table)]);
        _reader.GetSynonymTargetsAsync(Owner, Arg.Any<CancellationToken>()).Returns(SynonymTargets.None);
        _reader.GetForeignKeysAsync(Owner, Arg.Any<CancellationToken>()).Returns(_ => _declared);
    }

    private static ForeignKeyInfo Model(string name, TableRef from, string[] fromColumns, TableRef to, string[] toColumns) =>
        new(name, from, fromColumns, to, toColumns, FkSource.ClrModel);

    [Fact]
    public async Task Model_relationships_join_the_declared_ones_unless_a_declared_one_covers_them()
    {
        var cache = new SchemaCache(_reader, Owner);
        await cache.LoadAsync(Ct);
        var bearbeiter = Model("Auftrag.Bearbeiter", Auftrag, ["BEARBEITER_ID"], Mitarbeiter, ["ID"]);

        cache.SetForeignKeys(FkSource.ClrModel, [Model("Auftrag.Kunde", Auftrag, ["KUNDE_ID"], Kunden, ["KUNDE_ID"]), bearbeiter]);

        Assert.Equal(["FK_AUFTRAG_KUNDE", "Auftrag.Bearbeiter"], cache.OutgoingOf(Auftrag).Select(f => f.Name));
        Assert.Equal([bearbeiter], cache.IncomingOf(Mitarbeiter));
        Assert.Equal(2, cache.ForeignKeys.Count);
    }

    [Fact]
    public async Task Composite_relationships_are_the_same_whatever_the_column_order()
    {
        var position = new TableRef(Owner, "POSITION");
        _declared = [new("FK_LIEF_POS", Auftrag, ["AUFTRAG_ID", "POS_NR"], position, ["AUFTRAG_ID", "POS_NR"], FkSource.Declared)];
        var cache = new SchemaCache(_reader, Owner);
        await cache.LoadAsync(Ct);

        cache.SetForeignKeys(FkSource.ClrModel, [Model("Lieferung.Position", Auftrag, ["POS_NR", "AUFTRAG_ID"], position, ["POS_NR", "AUFTRAG_ID"])]);

        Assert.Equal(["FK_LIEF_POS"], cache.OutgoingOf(Auftrag).Select(f => f.Name));
    }

    [Fact]
    public async Task Model_relationships_survive_a_reload_and_give_way_to_a_constraint_added_since()
    {
        var cache = new SchemaCache(_reader, Owner);
        await cache.LoadAsync(Ct);
        cache.SetForeignKeys(FkSource.ClrModel, [Model("Auftrag.Bearbeiter", Auftrag, ["BEARBEITER_ID"], Mitarbeiter, ["ID"])]);

        await cache.RefreshAsync(Ct);
        Assert.Contains(cache.OutgoingOf(Auftrag), f => f.Source == FkSource.ClrModel);

        _declared = [.. _declared, new("FK_AUFTRAG_BEARBEITER", Auftrag, ["BEARBEITER_ID"], Mitarbeiter, ["ID"], FkSource.Declared)];
        await cache.RefreshAsync(Ct);
        Assert.All(cache.OutgoingOf(Auftrag), f => Assert.Equal(FkSource.Declared, f.Source));

        cache.SetForeignKeys(FkSource.ClrModel, []);
        Assert.Equal(2, cache.ForeignKeys.Count);
    }

    [Fact]
    public void Declared_foreign_keys_cannot_be_replaced() =>
        Assert.Throws<ArgumentException>(() => new SchemaCache(_reader, Owner).SetForeignKeys(FkSource.Declared, []));
}
