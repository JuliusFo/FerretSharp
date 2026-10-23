using FerretSharp.Core.Schema;
using FerretSharp.Testing;
using FerretSharp.UI.State;

namespace FerretSharp.UI.Tests.State;

/// <summary>Where the columns view's script goes: another connection, and the matching table there.</summary>
public sealed class AddColumnsTargetsTests
{
    private static readonly TableRef Kunde = new("ERP", "KUNDE");

    [Fact]
    public void The_targets_are_the_other_connections_in_their_order()
    {
        var (dev, test, prod) = (TestApp.Profile("Dev"), TestApp.Profile("Test"), TestApp.Profile("Prod"));

        Assert.Equal([dev, prod], AddColumnsTargets.Of([dev, test, prod], test.Id));
    }

    [Fact]
    public async Task The_table_is_the_matching_one_on_the_target_else_the_name_in_the_target_s_schema()
    {
        var prod = TestApp.Profile("Prod") with { DefaultSchema = "erp_p" };
        var schema = new SchemaCache(FakeDatabase.Reader(
            new TableSummary("ERP_P", "KUNDE", TableKind.Table),
            new TableSummary("STAMM", "LIEFERANT_T", TableKind.Table, new SynonymInfo("ERP_P", "LIEFERANT"))), "ERP_P");
        await schema.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new TableRef("ERP_P", "KUNDE"), AddColumnsTargets.Table(prod, schema, Kunde));
        Assert.Equal(new TableRef("STAMM", "LIEFERANT_T"), AddColumnsTargets.Table(prod, schema, new TableRef("ERP", "LIEFERANT")));
        Assert.Equal(new TableRef("ERP_P", "AUFTRAG"), AddColumnsTargets.Table(prod, schema, new TableRef("ERP", "AUFTRAG")));

        // Not connected yet: the schema the connection shows.
        Assert.Equal(new TableRef("ERP_P", "KUNDE"), AddColumnsTargets.Table(prod, null, Kunde));
        Assert.Equal(new TableRef("APP_USER", "KUNDE"), AddColumnsTargets.Table(TestApp.Profile("Test"), null, Kunde));
    }
}
