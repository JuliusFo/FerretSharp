using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;
using NSubstitute;

namespace FerretSharp.Core.Tests.Schema;

public class PlSqlSchemaCacheTests
{
    private const string Owner = "APP";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly PlSqlObjectSummary Rechnung = new(Owner, "PKG_RECHNUNG", PlSqlKind.Package, "VALID", "VALID");
    private static readonly PlSqlObjectSummary Trigger = new(Owner, "KUNDEN", PlSqlKind.Trigger, "VALID");

    [Fact]
    public async Task Loads_own_units_and_synonym_targets_one_entry_each()
    {
        var reader = Substitute.For<ISchemaReader>();
        reader.GetTablesAsync(Owner, Arg.Any<CancellationToken>()).Returns([new TableSummary(Owner, "KUNDEN", TableKind.Table)]);
        reader.GetPlSqlObjectsAsync(Owner, Arg.Any<CancellationToken>()).Returns([Rechnung, Trigger]);
        var viaPublic = new PlSqlObjectSummary("ERP", "PKG_DRUCK", PlSqlKind.Package, "VALID", null, new SynonymInfo("PUBLIC", "PKG_DRUCK"));
        var viaPrivate = viaPublic with { Synonym = new SynonymInfo(Owner, "DRUCK") };
        var toOwn = Rechnung with { Synonym = new SynonymInfo("PUBLIC", "RECHNUNG") };
        reader.GetSynonymTargetsAsync(Owner, Arg.Any<CancellationToken>()).Returns(new SynonymTargets([], [viaPublic, viaPrivate, toOwn]));
        reader.GetForeignKeysAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns([]);
        var cache = new SchemaCache(reader, Owner);

        await cache.LoadAsync(Ct);

        Assert.Equal(["DRUCK", "KUNDEN", "PKG_RECHNUNG"], cache.PlSqlObjects.Select(o => o.DisplayName));
        Assert.Same(Trigger, cache.FindPlSql(Trigger.Ref));
        Assert.Equal("KUNDEN", Assert.Single(cache.Tables).Name); // a trigger may share its name with a table
        Assert.Same(Rechnung, cache.FindPlSql(Owner, "PKG_RECHNUNG", "PACKAGE BODY"));
        Assert.Equal("DRUCK", cache.FindPlSql("ERP", "PKG_DRUCK", "PACKAGE")!.DisplayName);
        Assert.Null(cache.FindPlSql(Owner, "KUNDEN", "TABLE"));
    }
}
