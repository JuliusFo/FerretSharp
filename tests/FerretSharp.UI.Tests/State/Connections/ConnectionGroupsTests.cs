using FerretSharp.Core.Connections;
using FerretSharp.Core.Settings;
using FerretSharp.UI.State;

namespace FerretSharp.UI.Tests.State;

/// <summary>WP-66: groups in the user's order, one collapsed state, renaming and merging, moving connections and groups.</summary>
public sealed class ConnectionGroupsTests : IAsyncDisposable
{
    private readonly TestApp _app = new();
    private readonly TestFolder _folder = new();

    private static readonly ConnectionProfile ErpDev = TestApp.Profile("ERP Dev", ConnectionKind.Dev) with { Group = "ERP" };
    private static readonly ConnectionProfile ErpTest = TestApp.Profile("ERP Test") with { Group = "erp " };
    private static readonly ConnectionProfile Kasse = TestApp.Profile("Kasse") with { Group = "Kasse" };
    private static readonly ConnectionProfile Zoll = TestApp.Profile("Zoll") with { Group = "Zoll" };
    private static readonly ConnectionProfile Loose = TestApp.Profile("Lose");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ConnectionGroups Groups => _app.Groups;

    /// <summary>The service first, as the shell injects it at start: it follows the list from then on.</summary>
    private async Task SaveAllAsync()
    {
        _ = Groups.Order;
        await _app.Connections.SaveManyAsync([ErpDev, ErpTest, Kasse, Zoll, Loose], Ct);
    }

    [Fact]
    public async Task Groups_move_up_and_down_and_keep_the_order()
    {
        await SaveAllAsync();
        Assert.Equal(["ERP", "Kasse", "Zoll"], Groups.Names);
        Assert.Null(Groups.Previous("ERP"));
        Assert.False(Groups.HasNext("Zoll"));

        Assert.True(await Groups.MoveDownAsync("ERP"));
        Assert.Equal(["Kasse", "ERP", "Zoll"], Groups.Names);
        await Groups.MoveUpAsync("Zoll");
        Assert.Equal(["Kasse", "Zoll", "ERP"], Groups.Names);
        await Groups.MoveAsync("ERP", "Kasse");
        Assert.Equal(["ERP", "Kasse", "Zoll"], Groups.Names);
        Assert.Null(Groups.All()[^1].Name); // without group stays last
    }

    [Fact]
    public async Task A_rename_takes_the_connections_template_work_clone_and_state_along()
    {
        await SaveAllAsync();
        await _app.Settings.UpdateAsync(s => s with { AuditTemplateGroups = SettingsMap.From([new("ERP", @"C:\erp.sql")]) }, Ct);
        var clone = Directory.CreateDirectory(_folder.Combine("erp-work")).FullName;
        await _app.WorkClones.SetActiveAsync("ERP", clone, Ct);
        await Groups.ToggleAsync("ERP");
        await Groups.MoveDownAsync("ERP");

        var notSaved = await Groups.RenameAsync(Groups.PlanRename("ERP", "Warenwirtschaft")!);

        Assert.Empty(notSaved);
        Assert.Equal(["Kasse", "Warenwirtschaft", "Zoll"], Groups.Names);
        Assert.Equal(2, Groups.All().Single(g => g.Name == "Warenwirtschaft").Profiles.Count);
        Assert.True(Groups.IsCollapsed("Warenwirtschaft"));
        Assert.Equal(@"C:\erp.sql", _app.Settings.Current.AuditTemplateGroups["Warenwirtschaft"]);
        Assert.Equal(clone, _app.WorkClones.ActiveFor("Warenwirtschaft"));
    }

    [Fact]
    public async Task A_name_another_group_has_merges_them()
    {
        await SaveAllAsync();

        var rename = Groups.PlanRename("Zoll", "kasse")!;
        await Groups.RenameAsync(rename);

        Assert.True(rename.Merges);
        Assert.Equal(["ERP", "Kasse"], Groups.Names);
        Assert.Equal(["Kasse", "Zoll"], Groups.All().Single(g => g.Name == "Kasse").Profiles.Select(p => p.Name));
        Assert.Null(Groups.PlanRename("Zoll", "  "));
    }

    [Fact]
    public async Task A_connection_moves_into_another_group_or_none()
    {
        await SaveAllAsync();

        await Groups.MoveConnectionAsync(Loose, " Kasse ");
        await Groups.MoveConnectionAsync(_app.Connections.Profiles.Single(p => p.Id == Kasse.Id), null);

        Assert.Equal("Kasse", _app.Connections.Profiles.Single(p => p.Id == Loose.Id).Group);
        Assert.Null(_app.Connections.Profiles.Single(p => p.Id == Kasse.Id).Group);
    }

    [Fact]
    public async Task Collapsing_is_one_state_and_announced()
    {
        await SaveAllAsync();
        var changed = 0;
        Groups.Changed += () => changed++;

        await Groups.ToggleAsync(null);

        Assert.True(Groups.IsCollapsed(null));
        Assert.False(Groups.IsCollapsed("ERP"));
        Assert.Equal(1, changed);
    }

    /// <summary>The key of a deleted connection would stay in the settings and could not be given to another one.</summary>
    [Fact]
    public async Task A_deleted_connections_key_leaves_the_settings()
    {
        await SaveAllAsync();
        await _app.Shortcuts.SetConnectionAsync(Kasse.Id, KeyChord.Parse("alt+1"));
        await _app.Shortcuts.SetConnectionAsync(Zoll.Id, KeyChord.Parse("alt+2"));

        await _app.Connections.DeleteAsync(Kasse.Id, Ct);

        await WaitUntilAsync(() => !_app.Settings.Current.ConnectionShortcuts.ContainsKey(Kasse.Id.ToString()));
        Assert.Equal("alt+2", _app.Settings.Current.ConnectionShortcuts[Zoll.Id.ToString()]);
    }

    /// <summary>The settings are written after the list changed, not awaited by it.</summary>
    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10, Ct);
        }

        Assert.True(condition());
    }

    public async ValueTask DisposeAsync()
    {
        _folder.Dispose();
        await _app.DisposeAsync();
    }

    /// <summary>Review of WP-66: a connection moved into a group of a name an earlier, collapsed group had was hidden.</summary>
    [Fact]
    public async Task A_connection_moved_into_a_new_group_is_seen()
    {
        await SaveAllAsync();
        await Groups.ToggleAsync("Zoll");
        await Groups.MoveConnectionAsync(Zoll, "Kasse"); // Zoll has no connection now, its entry stays

        await Groups.MoveConnectionAsync(Loose, "zoll");

        Assert.False(Groups.IsCollapsed("zoll"));
    }

    /// <summary>Review of WP-66: the profile of an open menu may be older than the saved one, or deleted meanwhile.</summary>
    [Fact]
    public async Task Moving_saves_the_current_profile_and_never_brings_back_a_deleted_one()
    {
        await SaveAllAsync();
        await _app.Connections.SaveAsync(Kasse with { ScriptFolder = @"C:\scripts" }, null, Ct);

        await Groups.MoveConnectionAsync(Kasse, "ERP"); // the menu's older profile

        Assert.Equal(@"C:\scripts", _app.Connections.Profiles.Single(p => p.Id == Kasse.Id).ScriptFolder);

        await _app.Connections.DeleteAsync(Zoll.Id, Ct);
        await Groups.MoveConnectionAsync(Zoll, "ERP");

        Assert.DoesNotContain(_app.Connections.Profiles, p => p.Id == Zoll.Id);
    }

    /// <summary>Review of WP-66: a connections file missing for a moment lists none – that is no reason to drop every key.</summary>
    [Fact]
    public async Task Keys_stay_while_no_connection_is_listed()
    {
        await SaveAllAsync();
        await _app.Shortcuts.SetConnectionAsync(Kasse.Id, KeyChord.Parse("alt+1"));

        await _app.Connections.LoadAsync(Ct); // the store of the test app has no file: no connections, no load error
        Assert.Empty(_app.Connections.Profiles);

        await Task.Delay(50, Ct);
        Assert.True(_app.Settings.Current.ConnectionShortcuts.ContainsKey(Kasse.Id.ToString()));
    }

    /// <summary>Review of WP-66: merging drops the source's work clone when the target has one – the dialog says so.</summary>
    [Fact]
    public async Task Merging_names_the_work_clone_it_drops()
    {
        await SaveAllAsync();
        var zoll = Directory.CreateDirectory(_folder.Combine("zoll-work")).FullName;
        var kasse = Directory.CreateDirectory(_folder.Combine("kasse-work")).FullName;
        await _app.WorkClones.SetActiveAsync("Zoll", zoll, Ct);

        Assert.Null(Groups.DroppedWorkClone(Groups.PlanRename("Zoll", "Kasse")!)); // the target has none: it moves

        await _app.WorkClones.SetActiveAsync("Kasse", kasse, Ct);

        Assert.Equal(zoll, Groups.DroppedWorkClone(Groups.PlanRename("Zoll", "Kasse")!));
        Assert.Null(Groups.DroppedWorkClone(Groups.PlanRename("Zoll", "Lager")!)); // no merge
    }
}
