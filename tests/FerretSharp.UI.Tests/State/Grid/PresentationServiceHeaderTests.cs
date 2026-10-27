using FerretSharp.Core.Schema;
using FerretSharp.UI.State;

namespace FerretSharp.UI.Tests.State;

/// <summary>
/// The header lines go through the presentation layer like the C# names, but on their own event: switching them relabels
/// the grids without a <see cref="PresentationService.Changed"/>, which would fetch the rows again.
/// </summary>
public sealed class PresentationServiceHeaderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Switching_type_or_not_null_reports_the_new_lines_and_nothing_else()
    {
        await using var app = new TestApp();
        var presentations = app.Hub.Show(TestApp.Profile("DEV")).Presentations;
        var header = 0;
        var changed = 0;
        presentations.HeaderChanged += () => header++;
        presentations.Changed += () => changed++;
        var version = presentations.Version;

        await app.Settings.UpdateAsync(s => s with { ShowColumnTypes = false }, Ct);
        await app.Settings.UpdateAsync(s => s with { KeepAlive = false }, Ct); // another setting: no header change
        await app.Settings.UpdateAsync(s => s with { ShowNotNull = false }, Ct);

        Assert.Equal(2, header);
        Assert.Equal(new ColumnHeaderLines(false, false), presentations.Header);
        Assert.Equal((0, version), (changed, presentations.Version));
    }
}

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
