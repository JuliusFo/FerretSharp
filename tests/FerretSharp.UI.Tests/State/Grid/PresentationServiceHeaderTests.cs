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
