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

        Assert.Equal(new PasswordRequest(profile, ShowWhenConnected: true), _app.Shell.PendingPassword);
        Assert.Equal(ShellPage.Connections, _app.Shell.Page);
        Assert.Null(_app.Hub.Find(profile.Id));
    }

    /// <summary>
    /// Review of WP-35: a page that connects on its own (app statements, schema comparison) stays – before, the shell jumped
    /// to the page of an earlier <c>Connect</c>, and the page that asked was gone.
    /// </summary>
    [Fact]
    public async Task A_page_that_connects_on_its_own_stays_when_the_password_is_asked_for()
    {
        var earlier = TestApp.Profile("Früher");
        _app.Connections.SetPassword(earlier.Id, "pw", remember: false);
        _app.Shell.Connect(earlier); // from the connections page, long ago
        await _app.Lifecycle.ConnectAsync(earlier);
        _app.Shell.ShowAppStatements();
        var imported = TestApp.Profile("Importiert");

        await _app.Lifecycle.ConnectAsync(imported); // "look it up in the database"

        Assert.Equal(new PasswordRequest(imported, ShowWhenConnected: false), _app.Shell.PendingPassword);
        Assert.Equal(ShellPage.AppStatements, _app.Shell.Page);
    }

    [Fact]
    public void The_page_a_connect_came_from_is_taken_once_and_only_for_that_connection()
    {
        var profile = TestApp.Profile("A");
        _app.Shell.ShowSettings();
        _app.Shell.Connect(profile);

        Assert.Null(_app.Shell.TakeConnectOrigin(Guid.NewGuid())); // another connection: nothing, and gone
        _app.Shell.ShowSettings();
        _app.Shell.Connect(profile);
        Assert.Equal<ShellPage?>(ShellPage.Settings, _app.Shell.TakeConnectOrigin(profile.Id));
        Assert.Null(_app.Shell.TakeConnectOrigin(profile.Id));
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

    /// <summary>Review 3.31.0, A8: an open connection edited to Prod kept writing without the Prod protection until disconnected.</summary>
    [Fact]
    public async Task Reconnecting_after_an_edit_applies_the_saved_profile()
    {
        var profile = TestApp.Profile("ERP", ConnectionKind.Dev);
        await _app.OpenAsync(profile);
        var prod = profile with { Kind = ConnectionKind.Prod, ReadOnly = true };
        Assert.True(profile.SessionDiffers(prod));
        Assert.False(profile.SessionDiffers(profile with { Name = "ERP neu", Group = "Kunde" }));

        _app.Shell.OfferReconnect(prod);
        await _app.Lifecycle.ReconnectChangedAsync(prod);

        Assert.Null(_app.Shell.ReconnectOffer);
        var scope = _app.Hub.Find(profile.Id)!;
        await ConnectedAsync(scope);
        Assert.Equal(ConnectionKind.Prod, scope.Profile!.Kind);
        Assert.False(scope.Workspaces.IsWritable(scope.Workspaces.Active!.Id));
    }

    [Fact]
    public async Task Reconnecting_after_an_edit_asks_about_uncommitted_work_first()
    {
        var profile = TestApp.Profile("ERP", ConnectionKind.Dev);
        var scope = await _app.OpenAsync(profile);
        _app.AddPendingWork(scope);

        await _app.Lifecycle.ReconnectChangedAsync(profile with { ReadOnly = true });

        Assert.NotNull(_app.Lifecycle.PendingLeave);
        Assert.Same(scope, _app.Hub.Find(profile.Id));
        Assert.False(scope.Profile!.ReadOnly);
    }

    private static async Task ConnectedAsync(ConnectionScope scope)
    {
        var connected = new TaskCompletionSource();
        void Check()
        {
            if (scope.Active.IsConnected)
            {
                connected.TrySetResult();
            }
        }

        scope.Active.Changed += Check;
        Check();
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        scope.Active.Changed -= Check;
    }

    public ValueTask DisposeAsync() => _app.DisposeAsync();
}
