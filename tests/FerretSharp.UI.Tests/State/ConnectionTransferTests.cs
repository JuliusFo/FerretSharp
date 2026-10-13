using FerretSharp.Core.Connections;
using FerretSharp.UI.State;

namespace FerretSharp.UI.Tests.State;

/// <summary>WP-35: the export selection and asking for the password of a connection without one (imported).</summary>
public sealed class ConnectionTransferTests : IAsyncDisposable
{
    private readonly TestApp _app = new();

    private static readonly ConnectionProfile ErpDev = TestApp.Profile("ERP Dev", ConnectionKind.Dev) with { Group = "ERP" };
    private static readonly ConnectionProfile ErpTest = TestApp.Profile("ERP Test") with { Group = "erp " };
    private static readonly ConnectionProfile Kasse = TestApp.Profile("Kasse") with { Group = "Kasse" };
    private static readonly ConnectionProfile Loose = TestApp.Profile("Lose");

    [Fact]
    public void Export_starts_with_all_connections_or_only_the_requested_group()
    {
        var all = new ConnectionExportSelection([ErpDev, ErpTest, Kasse, Loose], new ConnectionExportRequest());
        var erp = new ConnectionExportSelection([ErpDev, ErpTest, Kasse, Loose], new ConnectionExportRequest(OnlyGroup: true, "ERP"));
        var ungrouped = new ConnectionExportSelection([ErpDev, ErpTest, Kasse, Loose], new ConnectionExportRequest(OnlyGroup: true, null));

        Assert.Equal(4, all.SelectedCount);
        Assert.Equal(["ERP Dev", "ERP Test"], erp.Selected.Select(p => p.Name));
        Assert.Equal(["Lose"], ungrouped.Selected.Select(p => p.Name));
    }

    [Fact]
    public void Group_check_box_selects_all_of_the_group_unless_all_are_selected()
    {
        var selection = new ConnectionExportSelection([ErpDev, ErpTest, Kasse], new ConnectionExportRequest(OnlyGroup: true, "Kasse"));
        var erp = selection.Groups.Single(g => g.Name == "ERP");

        Assert.Equal(0, selection.SelectedIn(erp));
        selection.Set(ErpDev, true);
        Assert.Equal(1, selection.SelectedIn(erp));

        selection.ToggleGroup(erp);
        Assert.Equal(2, selection.SelectedIn(erp));

        selection.ToggleGroup(erp);
        Assert.Equal(0, selection.SelectedIn(erp));
        Assert.Equal(["Kasse"], selection.Selected.Select(p => p.Name));
    }

    [Fact]
    public void All_and_none_act_on_the_shown_connections_and_hidden_ones_keep_their_selection()
    {
        var selection = new ConnectionExportSelection([ErpDev, ErpTest, Kasse], new ConnectionExportRequest()) { Query = "kasse" };

        selection.SetShown(false);

        Assert.Equal(["ERP Dev", "ERP Test"], selection.Selected.Select(p => p.Name));
        Assert.Equal(2, selection.CountOf(new ConnectionGroup("ERP", [])));
    }

    [Fact]
    public async Task Connecting_without_a_password_asks_for_it_and_stays_on_the_page()
    {
        var profile = TestApp.Profile("Importiert");

        _app.Shell.Connect(profile);
        await _app.Lifecycle.ConnectAsync(profile);

        Assert.Same(profile, _app.Shell.PendingPassword);
        Assert.Equal(ShellPage.Connections, _app.Shell.Page);
        Assert.Null(_app.Hub.Find(profile.Id));
    }

    [Fact]
    public async Task With_a_password_for_this_run_the_connection_opens_and_nothing_is_saved()
    {
        var profile = TestApp.Profile("Importiert");
        _app.Connections.SetPassword(profile.Id, "pw", remember: false);
        _app.Shell.Connect(profile);
        await _app.Lifecycle.ConnectAsync(profile);

        Assert.Null(_app.Shell.PendingPassword);
        Assert.Equal(ShellPage.Explorer, _app.Shell.Page);
        Assert.NotNull(_app.Hub.Find(profile.Id));
        Assert.Empty(_app.Secrets.Passwords);
    }

    public ValueTask DisposeAsync() => _app.DisposeAsync();
}
