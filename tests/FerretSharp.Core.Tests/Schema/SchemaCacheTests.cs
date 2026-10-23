using FerretSharp.Core.Schema;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace FerretSharp.Core.Tests.Schema;

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
        _reader.GetSynonymTargetsAsync(Owner, Arg.Any<CancellationToken>()).Returns(SynonymTargets.None);
        _reader.GetForeignKeysAsync(Arg.Is<IReadOnlyList<string>>(o => o.Contains(Owner)), Arg.Any<CancellationToken>()).Returns([AuftragKunde]);
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
        _reader.GetSynonymTargetsAsync(Owner, Arg.Any<CancellationToken>()).Returns(new SynonymTargets([produkt, kategorie], []));
        _reader.GetForeignKeysAsync(Arg.Is<IReadOnlyList<string>>(o => o.Contains("ERP")), Arg.Any<CancellationToken>()).Returns([produktKategorie]);
        var cache = new SchemaCache(_reader, Owner);

        await cache.LoadAsync(Ct);

        Assert.Equal(["AUFTRAG", "KATEGORIE", "KUNDEN", "S_PRODUKT"], cache.Tables.Select(t => t.DisplayName));
        Assert.Same(produkt, cache.Find(new TableRef("ERP", "PRODUKT")));
        Assert.Equal([produktKategorie], cache.IncomingOf(kategorie.Ref));
        await _reader.Received(1).GetForeignKeysAsync(Arg.Is<IReadOnlyList<string>>(o => o.Contains("ERP")), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_table_is_labelled_by_its_synonym_its_own_name_or_owner_and_name()
    {
        var produkt = new TableSummary("ERP", "PRODUKT", TableKind.Table, new SynonymInfo(Owner, "S_PRODUKT"));
        _reader.GetSynonymTargetsAsync(Owner, Arg.Any<CancellationToken>()).Returns(new SynonymTargets([produkt], []));
        var cache = new SchemaCache(_reader, Owner);

        await cache.LoadAsync(Ct);

        Assert.Equal("S_PRODUKT", cache.LabelOf(produkt.Ref));
        Assert.Equal("KUNDEN", cache.LabelOf(Kunden.Ref));
        Assert.Equal("ERP.LAGER", cache.LabelOf(new TableRef("ERP", "LAGER")));
        Assert.Equal("NEU", cache.LabelOf(new TableRef(Owner, "NEU")));
    }

    [Fact]
    public async Task Primary_keys_are_loaded_and_relationships_into_a_base_table_reach_its_extension()
    {
        // KUNDE_EXT extends KUNDEN by a relationship of the C# model; AUFTRAG references KUNDEN.
        var extension = new TableSummary(Owner, "KUNDE_EXT", TableKind.Table);
        _reader.GetTablesAsync(Owner, Arg.Any<CancellationToken>()).Returns([Auftrag, Kunden, extension]);
        _reader.GetPrimaryKeysAsync(Arg.Is<IReadOnlyList<string>>(o => o.Contains(Owner)), Arg.Any<CancellationToken>()).Returns(
        [
            new PrimaryKeyInfo(Kunden.Ref, ["KUNDE_ID"]), new PrimaryKeyInfo(extension.Ref, ["KUNDE_ID"]), new PrimaryKeyInfo(Auftrag.Ref, ["AUFTRAG_ID"]),
        ]);
        var cache = new SchemaCache(_reader, Owner);
        await cache.LoadAsync(Ct);
        Assert.Equal(["KUNDE_ID"], cache.PrimaryKeyOf(extension.Ref));
        Assert.Empty(cache.PrimaryKeyOf(new TableRef(Owner, "V_KUNDEN")));
        Assert.Empty(cache.InheritedIncomingOf(extension.Ref));

        cache.SetForeignKeys(FkSource.ClrModel, [new ForeignKeyInfo("KundeExt", extension.Ref, ["KUNDE_ID"], Kunden.Ref, ["KUNDE_ID"], FkSource.ClrModel)]);

        var reference = Assert.Single(cache.InheritedIncomingOf(extension.Ref));
        Assert.Equal((AuftragKunde, Kunden.Ref), (reference.ForeignKey, reference.Via));
        await _reader.Received(1).GetPrimaryKeysAsync(Arg.Is<IReadOnlyList<string>>(o => o.Contains(Owner)), Arg.Any<CancellationToken>());
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
