using FerretSharp.Core.Audit;
using FerretSharp.Core.Schema;
using NSubstitute;

namespace FerretSharp.Core.Tests.Schema;

/// <summary>The relationships an audit table gets from its base table (WP-63): outgoing only, with a link to its base row.</summary>
public sealed class SchemaCacheAuditTests
{
    private const string Owner = "APP";
    private static readonly TableRef Kunden = new(Owner, "KUNDEN");
    private static readonly TableRef Auftrag = new(Owner, "AUFTRAG");
    private static readonly TableRef AuditAuftrag = new(Owner, "AUDIT_AUFTRAG");
    private static readonly TableRef AuditKunden = new(Owner, "AUDIT_KUNDEN");
    private static readonly TableRef Position = new(Owner, "POSITION");

    private static readonly ForeignKeyInfo AuftragKunde = new("FK_AUFTRAG_KUNDE", Auftrag, ["KUNDE_ID"], Kunden, ["KUNDE_ID"], FkSource.Declared);
    private static readonly ForeignKeyInfo AuftragVorgaenger = new("FK_AUFTRAG_VORG", Auftrag, ["VORG_ID"], Auftrag, ["AUFTRAG_ID"], FkSource.Declared);
    private static readonly ForeignKeyInfo PositionAuftrag = new("FK_POS_AUFTRAG", Position, ["AUFTRAG_ID"], Auftrag, ["AUFTRAG_ID"], FkSource.Declared);

    private readonly ISchemaReader _reader = Substitute.For<ISchemaReader>();
    private IReadOnlyList<TableSummary> _tables =
    [
        new(Owner, "AUDIT_AUFTRAG", TableKind.Table), new(Owner, "AUFTRAG", TableKind.Table), new(Owner, "KUNDEN", TableKind.Table),
        new(Owner, "POSITION", TableKind.Table),
    ];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public SchemaCacheAuditTests()
    {
        _reader.GetTablesAsync(Owner, Arg.Any<CancellationToken>()).Returns(_ => _tables);
        _reader.GetSynonymTargetsAsync(Owner, Arg.Any<CancellationToken>()).Returns(SynonymTargets.None);
        _reader.GetForeignKeysAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns([AuftragKunde, AuftragVorgaenger, PositionAuftrag]);
        _reader.GetPrimaryKeysAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns([new PrimaryKeyInfo(Auftrag, ["AUFTRAG_ID"]), new PrimaryKeyInfo(Kunden, ["KUNDE_ID"])]);
    }

    private async Task<SchemaCache> LoadAsync(AuditTemplate? template)
    {
        var cache = new SchemaCache(_reader, Owner);
        cache.UseAuditTemplate(() => template);
        await cache.LoadAsync(Ct);
        return cache;
    }

    private static string Text(ForeignKeyInfo fk) =>
        $"{fk.Name} {fk.From}({string.Join(", ", fk.FromColumns)}) → {fk.To}({string.Join(", ", fk.ToColumns)}) {fk.Source} {fk.Audit}";

    [Fact]
    public async Task An_audit_table_references_what_its_base_table_does_and_its_base_row()
    {
        var cache = await LoadAsync(AuditTemplates.Default);

        Assert.Equal(Auftrag, cache.AuditBaseOf(AuditAuftrag));
        Assert.Equal(
        [
            "AUFTRAG APP.AUDIT_AUFTRAG(AUFTRAG_ID) → APP.AUFTRAG(AUFTRAG_ID) Convention BaseRow",
            "FK_AUFTRAG_KUNDE APP.AUDIT_AUFTRAG(KUNDE_ID) → APP.KUNDEN(KUNDE_ID) Declared Copied",
            // The base table's relationship to itself still leads to the base table, not to another audit row.
            "FK_AUFTRAG_VORG APP.AUDIT_AUFTRAG(VORG_ID) → APP.AUFTRAG(AUFTRAG_ID) Declared Copied",
        ], cache.LinksOf(AuditAuftrag).Select(Text));
    }

    [Fact]
    public async Task Nothing_leads_into_an_audit_table()
    {
        var cache = await LoadAsync(AuditTemplates.Default);
        _ = cache.OutgoingOf(AuditAuftrag);

        Assert.Equal([AuftragVorgaenger, PositionAuftrag], cache.IncomingOf(Auftrag));
        Assert.Equal([AuftragKunde], cache.IncomingOf(Kunden));
        Assert.Empty(cache.IncomingOf(AuditAuftrag));
        Assert.Empty(cache.OutgoingOf(AuditAuftrag)); // the database's view: the copies are no constraints
        Assert.DoesNotContain(cache.ForeignKeys, f => f.From == AuditAuftrag || f.To == AuditAuftrag);
    }

    [Fact]
    public async Task Other_tables_and_a_connection_without_template_keep_their_own()
    {
        var with = await LoadAsync(AuditTemplates.Default);
        var without = await LoadAsync(null);

        Assert.Equal([AuftragKunde, AuftragVorgaenger], with.LinksOf(Auftrag));
        Assert.Null(with.AuditBaseOf(Auftrag));
        Assert.Null(with.AuditBaseOf(AuditKunden)); // not in the schema
        Assert.Null(without.AuditBaseOf(AuditAuftrag));
        Assert.Empty(without.LinksOf(AuditAuftrag));
    }

    [Fact]
    public async Task A_base_table_without_primary_key_gives_no_base_row()
    {
        _tables = [.. _tables, new(Owner, "AUDIT_POSITION", TableKind.Table)];
        var cache = await LoadAsync(AuditTemplates.Default);
        var auditPosition = new TableRef(Owner, "AUDIT_POSITION");

        Assert.Equal([PositionAuftrag with { From = auditPosition, Audit = AuditRelation.Copied }], cache.LinksOf(auditPosition));
    }

    [Fact]
    public async Task An_audit_table_of_an_audit_table_is_not_paired()
    {
        _tables = [.. _tables, new(Owner, "AUDIT_AUDIT_AUFTRAG", TableKind.Table)];
        var cache = await LoadAsync(AuditTemplates.Default);

        Assert.Equal(Auftrag, cache.AuditBaseOf(AuditAuftrag));
        Assert.Null(cache.AuditBaseOf(new TableRef(Owner, "AUDIT_AUDIT_AUFTRAG")));
    }

    [Fact]
    public async Task Views_and_synonyms_are_no_audit_tables()
    {
        _tables = [new(Owner, "AUDIT_AUFTRAG", TableKind.View), new(Owner, "AUFTRAG", TableKind.Table)];
        var cache = await LoadAsync(AuditTemplates.Default);

        Assert.Null(cache.AuditBaseOf(AuditAuftrag));
    }

    [Fact]
    public async Task A_template_without_the_table_in_the_name_pairs_nothing()
    {
        _tables = [.. _tables, new(Owner, "HISTORY", TableKind.Table)];
        var template = AuditTemplates.Parse("-- @audit-table HISTORY\n-- @trigger T_{table}\n-- @base-columns nullable\n-- @trigger-source\nX");
        var cache = await LoadAsync(template);

        Assert.Null(cache.AuditBaseOf(new TableRef(Owner, "HISTORY")));
    }

    [Fact]
    public async Task Another_template_pairs_anew()
    {
        _tables = [.. _tables, new(Owner, "AUFTRAG_H", TableKind.Table)];
        AuditTemplate? template = AuditTemplates.Default;
        var cache = new SchemaCache(_reader, Owner);
        cache.UseAuditTemplate(() => template);
        await cache.LoadAsync(Ct);
        Assert.Equal(Auftrag, cache.AuditBaseOf(AuditAuftrag));

        template = AuditTemplates.Parse("-- @audit-table {table}_H\n-- @trigger T_{table}\n-- @base-columns nullable\n-- @trigger-source\nX");
        Assert.Equal(Auftrag, cache.AuditBaseOf(AuditAuftrag)); // asked again only when told
        cache.RefreshAuditTemplate();

        Assert.Same(template, cache.AuditTemplate);
        Assert.Null(cache.AuditBaseOf(AuditAuftrag));
        Assert.Equal(Auftrag, cache.AuditBaseOf(new TableRef(Owner, "AUFTRAG_H")));
    }

    /// <summary>Review of WP-63: the template may sit on a share over the VPN – lookups while the grid renders never ask for it.</summary>
    [Fact]
    public async Task Lookups_do_not_ask_for_the_template()
    {
        var asked = 0;
        var cache = new SchemaCache(_reader, Owner);
        cache.UseAuditTemplate(() =>
        {
            asked++;
            return AuditTemplates.Default;
        });
        await cache.LoadAsync(Ct);

        for (var i = 0; i < 10; i++)
        {
            _ = cache.LinksOf(AuditAuftrag).ToList();
            _ = cache.AuditBaseOf(Auftrag);
        }

        Assert.Equal(1, asked);
        await cache.RefreshAsync(Ct);
        Assert.Equal(2, asked);
    }
}
