using FerretSharp.Core.Oracle;
using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;

namespace FerretSharp.Integration.Tests;

public class SynonymTests(OracleContainerFixture oracle) : IAsyncLifetime
{
    private const string Other = SynonymSchema.OtherOwner;

    private OracleSession? _session;
    private string _owner = "";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private OracleSchemaReader Reader => new(_session!);

    public async ValueTask InitializeAsync()
    {
        var (profile, password) = oracle.RequireProfile();
        var connectionString = oracle.RequireConnectionString();
        await SampleSchema.EnsureCreatedAsync(connectionString, Ct); // KUNDEN must exist for the public synonym
        await SynonymSchema.EnsureCreatedAsync(connectionString, Ct);
        Assert.SkipUnless(SynonymSchema.Available, SynonymSchema.UnavailableReason ?? "Synonym schema not created.");

        _owner = profile.EffectiveSchema;
        _session = await OracleSession.OpenAsync(
            OracleConnectionStringFactory.Create(profile, password), new SessionContext("FerretSharp", "Synonym tests"), Ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_session is not null)
        {
            await _session.DisposeAsync();
        }
    }

    [Fact]
    public async Task Lists_accessible_synonym_targets_in_other_schemas_only()
    {
        var targets = await Reader.GetSynonymTargetsAsync(_owner, Ct);
        var bySynonym = targets.Tables.ToDictionary(t => t.DisplayName);

        Assert.Equal(new TableSummary(Other, "PRODUKT", TableKind.Table, new SynonymInfo(_owner, "S_PRODUKT")), bySynonym["S_PRODUKT"]);
        Assert.Equal(new TableSummary(Other, "KATEGORIE", TableKind.Table, new SynonymInfo("PUBLIC", "FERRET_KATEGORIE")), bySynonym["FERRET_KATEGORIE"]);
        Assert.Equal(TableKind.View, bySynonym["S_V_PRODUKT"].Kind);

        Assert.DoesNotContain("FERRET_KUNDEN", bySynonym.Keys); // points into the own schema
        Assert.DoesNotContain("FERRET_GEHEIM", bySynonym.Keys); // no SELECT grant
        Assert.DoesNotContain("DUAL", bySynonym.Keys);          // Oracle-maintained target
        Assert.All(targets.Tables, t => Assert.NotEqual(_owner, t.Owner));
    }

    [Fact]
    public async Task Schema_cache_merges_synonyms_and_loads_foreign_keys_of_other_schemas()
    {
        var cache = new SchemaCache(Reader, _owner);

        await cache.LoadAsync(Ct);

        var view = Assert.Single(cache.Tables, t => t.Ref == new TableRef(Other, "V_PRODUKT"));
        Assert.Equal("S_V_PRODUKT", view.DisplayName); // private wins over FERRET_V_PRODUKT
        Assert.Single(cache.Tables, t => t.Ref == new TableRef(_owner, "KUNDEN"));
        Assert.Equal("KUNDEN", cache.Find(new TableRef(_owner, "KUNDEN"))!.DisplayName);

        var fk = Assert.Single(cache.OutgoingOf(new TableRef(Other, "PRODUKT")));
        Assert.Equal(new TableRef(Other, "KATEGORIE"), fk.To);
    }

    [Fact]
    public async Task Details_and_data_are_read_from_the_real_object()
    {
        var produkt = new TableSummary(Other, "PRODUKT", TableKind.Table, new SynonymInfo(_owner, "S_PRODUKT"));
        var details = await Reader.GetDetailsAsync(produkt, Ct);
        Assert.Equal(["ID", "NAME", "KATEGORIE_ID"], details.Columns.Select(c => c.Name));
        Assert.Equal(["ID"], details.PrimaryKey);

        var page = await new OracleDataAccess(_session!).ReadPageAsync(details, [FilterCondition.Of("NAME", FilterOperator.Equals, "Hammer")], [], new PageSpec(0, 10), Ct);
        Assert.Equal(10m, Assert.Single(page.Rows).Values[0]);
    }
}
