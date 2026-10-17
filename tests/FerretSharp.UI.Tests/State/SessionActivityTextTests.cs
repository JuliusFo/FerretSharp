using FerretSharp.Core.Data;
using FerretSharp.Core.Schema;
using FerretSharp.Core.Settings;
using FerretSharp.UI.State;

namespace FerretSharp.UI.Tests.State;

/// <summary>WP-52: what the run progress says about a waiting statement and a long operation, in both languages.</summary>
public sealed class SessionActivityTextTests : IAsyncDisposable
{
    private static readonly TableRef Kunden = new("APP", "KUNDEN");
    private static readonly LockHolder Scott = new(123, "SCOTT", "scott", "PC-17", "sqldeveloper64W.exe", "SQL Developer", null, null);

    private readonly TestApp _app = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static SessionWait Wait(LockHolder? blocker, TableRef? table = null, int seconds = 12) =>
        new("enq: TX - row lock contention", TimeSpan.FromSeconds(seconds), blocker?.Sid ?? 55, blocker, table);

    [Fact]
    public void A_lock_of_an_own_workspace_is_named_with_how_long_it_waits()
    {
        var own = new OwnWorkspace(Guid.NewGuid(), Guid.NewGuid(), "Bug 3711", null);

        Assert.Equal("Wartet auf eine Sperre auf KUNDEN, gehalten von deinem Workspace „Bug 3711“ · 0:12",
            SessionActivityText.Wait(Wait(null, Kunden), own));
        Assert.Equal("Wartet auf eine Sperre, gehalten von deinem Workspace „Bug 3711“ in Kunde A › TEST · 1:05",
            SessionActivityText.Wait(Wait(null, seconds: 65), own with { Connection = "Kunde A › TEST" }));
        using (UiCulture.Use("en"))
        {
            Assert.Equal("Waits for a lock on KUNDEN held by your workspace “Bug 3711” · 0:12",
                SessionActivityText.Wait(Wait(null, Kunden), own));
        }
    }

    [Fact]
    public void A_lock_of_another_session_names_user_machine_and_program()
    {
        Assert.Equal("Wartet auf eine Sperre auf KUNDEN, gehalten von Session 123 (SCOTT, PC-17, SQL Developer) · 0:12",
            SessionActivityText.Wait(Wait(Scott, Kunden), null));
        var colleague = new LockHolder(7, "MARIA", null, "PC-3", "FerretSharp.exe", "FerretSharp", "Test", null, "FerretSharp 0123456789ab");
        Assert.Equal("Wartet auf eine Sperre, gehalten von Session 7 (MARIA, PC-3, FerretSharp · Workspace „Test“) · 0:12",
            SessionActivityText.Wait(Wait(colleague), null));
        Assert.Equal("Wartet auf eine Sperre, gehalten von Session 55 · 0:12", SessionActivityText.Wait(Wait(null), null)); // other RAC instance
        Assert.Null(SessionActivityText.Wait(null, null));
        using (UiCulture.Use("en"))
        {
            Assert.Equal("Waits for a lock on KUNDEN held by session 123 (SCOTT, PC-17, SQL Developer) · 0:12",
                SessionActivityText.Wait(Wait(Scott, Kunden), null));
        }
    }

    [Fact]
    public void A_long_operation_shows_percent_and_the_time_left()
    {
        var scan = new LongOperation("Table Scan", "APP.KUNDEN", 430, 1000, "Blocks", TimeSpan.FromSeconds(20));
        var left = TimeText.Duration(TimeSpan.FromSeconds(20));

        Assert.Equal($"Table Scan APP.KUNDEN: 43 % · noch etwa {left}", SessionActivityText.Progress(scan));
        Assert.Equal("Hash Join: 10 %", SessionActivityText.Progress(new LongOperation("Hash Join", null, 1, 10, null, null)));
        Assert.Null(SessionActivityText.Progress(null));
        using (UiCulture.Use("en"))
        {
            Assert.Equal($"Table Scan APP.KUNDEN: 43 % · about {left} left", SessionActivityText.Progress(scan));
        }
    }

    [Fact]
    public void Without_rights_the_workspaces_with_uncommitted_changes_are_named()
    {
        Assert.Null(SessionActivityText.OpenElsewhere([]));
        Assert.Equal("Falls es auf eine Sperre wartet: „Bug 3711“ hat nicht committete Änderungen – dort Commit oder Rollback.",
            SessionActivityText.OpenElsewhere(["Bug 3711"]));
        Assert.Equal("Falls es auf eine Sperre wartet: „A“, „B“ haben nicht committete Änderungen – dort Commit oder Rollback.",
            SessionActivityText.OpenElsewhere(["A", "B"]));
        using (UiCulture.Use("en"))
        {
            Assert.Equal("If it waits for a lock: “A”, “B” have uncommitted changes – commit or roll back there.",
                SessionActivityText.OpenElsewhere(["A", "B"]));
        }
    }

    [Fact]
    public async Task A_session_tag_finds_the_workspace_also_in_another_connection()
    {
        var test = await _app.OpenAsync(TestApp.Profile("TEST") with { Group = "Kunde A" });
        var dev = await _app.OpenAsync(TestApp.Profile("DEV"));
        var holding = test.Workspaces.Active!;
        await test.Workspaces.GetDataAsync(holding.Id, Ct); // opens its session
        var tag = test.Workspaces.SessionTagOf(holding.Id);
        var settings = new AppSettings { ShowConnectionGroup = true };

        Assert.NotNull(tag);
        Assert.Equal(new OwnWorkspace(test.Id, holding.Id, holding.Name, null), SessionActivityText.OwnOf(_app.Hub, tag, test.Id, settings));
        Assert.Equal(new OwnWorkspace(test.Id, holding.Id, holding.Name, "Kunde A › TEST"), SessionActivityText.OwnOf(_app.Hub, tag, dev.Id, settings));
        Assert.Null(SessionActivityText.OwnOf(_app.Hub, "FerretSharp 000000000000", test.Id, settings));
        Assert.Null(SessionActivityText.OwnOf(_app.Hub, null, test.Id, settings));

        _app.Hub.ShowWorkspace(test, holding.Id);
        Assert.Same(test, _app.Hub.Current);
        Assert.Equal(holding.Id, test.Workspaces.Active!.Id);
    }

    public ValueTask DisposeAsync() => _app.DisposeAsync();
}
