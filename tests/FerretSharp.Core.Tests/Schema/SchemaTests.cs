using FerretSharp.Core.Schema;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace FerretSharp.Core.Tests.Schema;

public class ColumnInfoTests
{
    private static ColumnInfo Column(string type, int? length = null, bool charSemantics = false, int? precision = null, int? scale = null) =>
        new("C", type, length, charSemantics, precision, scale, true, false, null, 1);

    [Theory]
    [InlineData("VARCHAR2", 50, true, null, null, "VARCHAR2(50 CHAR)")]
    [InlineData("VARCHAR2", 50, false, null, null, "VARCHAR2(50)")]
    [InlineData("NVARCHAR2", 20, true, null, null, "NVARCHAR2(20)")]
    [InlineData("RAW", 16, false, null, null, "RAW(16)")]
    [InlineData("NUMBER", null, false, 10, 0, "NUMBER(10)")]
    [InlineData("NUMBER", null, false, 12, 2, "NUMBER(12,2)")]
    [InlineData("NUMBER", null, false, null, 0, "INTEGER")]
    [InlineData("NUMBER", null, false, null, null, "NUMBER")]
    [InlineData("TIMESTAMP(6)", null, false, null, 6, "TIMESTAMP(6)")]
    [InlineData("DATE", null, false, null, null, "DATE")]
    public void Display_type_matches_ddl_notation(string type, int? length, bool charSemantics, int? precision, int? scale, string expected)
    {
        Assert.Equal(expected, Column(type, length, charSemantics, precision, scale).DisplayType);
    }
}

public class SchemaCacheTests
{
    private const string Owner = "APP";
    private static readonly TableSummary Kunden = new(Owner, "KUNDEN", TableKind.Table);
    private static readonly TableSummary Auftrag = new(Owner, "AUFTRAG", TableKind.Table);
    private static readonly ForeignKeyInfo AuftragKunde = new(
        "FK_AUFTRAG_KUNDE", Auftrag.Ref, ["KUNDE_ID"], Kunden.Ref, ["KUNDE_ID"], FkSource.Declared);

    private readonly ISchemaReader _reader = Substitute.For<ISchemaReader>();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public SchemaCacheTests()
    {
        _reader.GetTablesAsync(Owner, Arg.Any<CancellationToken>()).Returns([Auftrag, Kunden]);
        _reader.GetSynonymTargetsAsync(Owner, Arg.Any<CancellationToken>()).Returns([]);
        _reader.GetForeignKeysAsync(Owner, Arg.Any<CancellationToken>()).Returns([AuftragKunde]);
        _reader.GetDetailsAsync(Arg.Any<TableSummary>(), Arg.Any<CancellationToken>())
            .Returns(ci => new TableDetails(ci.Arg<TableSummary>(), [], [], [], false));
    }

    [Fact]
    public async Task Load_exposes_tables_and_relationship_lookups()
    {
        var cache = new SchemaCache(_reader, Owner);

        await cache.LoadAsync(Ct);

        Assert.Equal([Auftrag, Kunden], cache.Tables);
        Assert.Equal([AuftragKunde], cache.IncomingOf(Kunden.Ref));
        Assert.Equal([AuftragKunde], cache.OutgoingOf(Auftrag.Ref));
        Assert.Empty(cache.OutgoingOf(Kunden.Ref));
        Assert.Same(Kunden, cache.Find(Kunden.Ref));
        Assert.NotNull(cache.LoadedAt);
    }

    [Fact]
    public async Task Details_are_loaded_once_per_table()
    {
        var cache = new SchemaCache(_reader, Owner);
        await cache.LoadAsync(Ct);

        await cache.GetDetailsAsync(Kunden, Ct);
        await cache.GetDetailsAsync(Kunden, Ct);
        await cache.GetDetailsAsync(Auftrag, Ct);

        await _reader.Received(1).GetDetailsAsync(Kunden, Arg.Any<CancellationToken>());
        await _reader.Received(1).GetDetailsAsync(Auftrag, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Failed_detail_loads_are_retried()
    {
        _reader.GetDetailsAsync(Kunden, Arg.Any<CancellationToken>())
            .Returns(
                _ => Task.FromException<TableDetails>(new InvalidOperationException("boom")),
                ci => Task.FromResult(new TableDetails(Kunden, [], [], [], false)));
        var cache = new SchemaCache(_reader, Owner);
        await cache.LoadAsync(Ct);

        await Assert.ThrowsAsync<InvalidOperationException>(() => cache.GetDetailsAsync(Kunden, Ct));
        var details = await cache.GetDetailsAsync(Kunden, Ct);

        Assert.Same(Kunden, details.Table);
    }

    [Fact]
    public async Task Cancelled_caller_does_not_cancel_the_shared_detail_load()
    {
        var loaded = new TaskCompletionSource<TableDetails>();
        _reader.GetDetailsAsync(Kunden, Arg.Any<CancellationToken>()).Returns(loaded.Task);
        var cache = new SchemaCache(_reader, Owner);
        await cache.LoadAsync(Ct);
        using var completion = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var abandoned = cache.GetDetailsAsync(Kunden, completion.Token);
        var grid = cache.GetDetailsAsync(Kunden, Ct);
        await completion.CancelAsync();
        loaded.SetResult(new TableDetails(Kunden, [], [], [], false));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned);
        Assert.Same(Kunden, (await grid).Table);
        Assert.Same(Kunden, (await cache.GetDetailsAsync(Kunden, Ct)).Table); // still cached
        await _reader.Received(1).GetDetailsAsync(Kunden, CancellationToken.None);
    }

    [Fact]
    public async Task Synonym_targets_are_listed_and_their_schemas_foreign_keys_loaded()
    {
        var produkt = new TableSummary("ERP", "PRODUKT", TableKind.Table, new SynonymInfo(Owner, "S_PRODUKT"));
        var kategorie = new TableSummary("ERP", "KATEGORIE", TableKind.Table, new SynonymInfo(SynonymInfo.PublicOwner, "KATEGORIE"));
        var produktKategorie = new ForeignKeyInfo("FK_PK", produkt.Ref, ["KAT_ID"], kategorie.Ref, ["ID"], FkSource.Declared);
        _reader.GetSynonymTargetsAsync(Owner, Arg.Any<CancellationToken>()).Returns([produkt, kategorie]);
        _reader.GetForeignKeysAsync("ERP", Arg.Any<CancellationToken>()).Returns([produktKategorie]);
        var cache = new SchemaCache(_reader, Owner);

        await cache.LoadAsync(Ct);

        Assert.Equal(["AUFTRAG", "KATEGORIE", "KUNDEN", "S_PRODUKT"], cache.Tables.Select(t => t.DisplayName));
        Assert.Same(produkt, cache.Find(new TableRef("ERP", "PRODUKT")));
        Assert.Equal([produktKategorie], cache.IncomingOf(kategorie.Ref));
        await _reader.Received(1).GetForeignKeysAsync("ERP", Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Merge_prefers_own_objects_and_private_over_public_synonyms()
    {
        var ownKunden = new TableSummary(Owner, "KUNDEN", TableKind.Table);
        var publicToOwn = new TableSummary(Owner, "KUNDEN", TableKind.Table, new SynonymInfo(SynonymInfo.PublicOwner, "P_KUNDEN"));
        var publicToOther = new TableSummary("ERP", "PRODUKT", TableKind.Table, new SynonymInfo(SynonymInfo.PublicOwner, "A_PRODUKT"));
        var privateToOther = new TableSummary("ERP", "PRODUKT", TableKind.Table, new SynonymInfo(Owner, "Z_PRODUKT"));

        var merged = SchemaCache.Merge([ownKunden], [publicToOwn, publicToOther, privateToOther]);

        Assert.Equal([ownKunden, privateToOther], merged);
    }

    [Fact]
    public async Task Refresh_reloads_and_drops_cached_details()
    {
        var cache = new SchemaCache(_reader, Owner);
        await cache.LoadAsync(Ct);
        await cache.GetDetailsAsync(Kunden, Ct);

        await cache.RefreshAsync(Ct);
        await cache.GetDetailsAsync(Kunden, Ct);

        await _reader.Received(2).GetTablesAsync(Owner, Arg.Any<CancellationToken>());
        await _reader.Received(2).GetDetailsAsync(Kunden, Arg.Any<CancellationToken>());
    }
}

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
        _reader.GetSynonymTargetsAsync(Owner, Arg.Any<CancellationToken>()).Returns([]);
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
