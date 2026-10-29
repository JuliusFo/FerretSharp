using FerretSharp.Core.Schema;
using FerretSharp.UI.State;

namespace FerretSharp.UI.Tests.State;

/// <summary>WP-63 (review): an audit table's columns are named by its base table, also when the app builds the services.</summary>
public sealed class PresentationServiceAuditTests
{
    [Fact]
    public async Task An_audit_table_takes_its_base_tables_model_and_others_their_own()
    {
        await using var app = new TestApp { Tables = [TestApp.Kunden, new("APP_USER", "AUDIT_KUNDEN", TableKind.Table)] };
        var scope = await app.OpenAsync(TestApp.Profile("DEV"));

        Assert.Equal(TestApp.Kunden.Ref, scope.Presentations.ModelTableOf(new TableRef("APP_USER", "AUDIT_KUNDEN")));
        Assert.Equal(TestApp.Kunden.Ref, scope.Presentations.ModelTableOf(TestApp.Kunden.Ref));
        Assert.Same(FerretSharp.Core.Audit.AuditTemplates.Default, scope.Active.Schema!.AuditTemplate); // registered as in the app
    }
}
