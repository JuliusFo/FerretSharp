using FerretSharp.Core.Connections;
using FerretSharp.Core.Data;
using FerretSharp.Core.Schema;
using FerretSharp.Core.Tests.Connections;
using FerretSharp.Core.Tests.Fakes;
using FerretSharp.Core.Workspaces;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace FerretSharp.Core.Tests.Workspaces;

public sealed class WorkspaceManagerTests
{
    private readonly InMemoryWorkspaceStore _store = new();
    private readonly InMemorySecretStore _secrets = new();
    private readonly IDatabaseConnector _connector = Substitute.For<IDatabaseConnector>();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.Zero));
    private readonly List<(string Action, IDatabaseConnection Connection)> _opened = [];
    private readonly ConnectionProfile _profile = TestProfiles.HostPort();
    private readonly WorkspaceManager _manager;

    public WorkspaceManagerTests()
    {
        _secrets.SetPassword(_profile.Id, "pw");
        _connector.OpenAsync(Arg.Any<ConnectionProfile>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var connection = Substitute.For<IDatabaseConnection>();
                connection.Data.Returns(Substitute.For<IDataAccess>());
                _opened.Add((call.ArgAt<string>(2), connection));
                return connection;
            });
        _manager = new WorkspaceManager(_store, new ConnectionManager(Substitute.For<IConnectionStore>(), _secrets), _connector, _time);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Workspace Stored(string name, bool isOpen = true, int order = 0, int minutesAgo = 0)
    {
        var workspace = new Workspace(Guid.NewGuid(), _profile.Id, name)
        {
            IsOpen = isOpen,
            Order = order,
            LastActive = _time.GetUtcNow().AddMinutes(-minutesAgo),
        };
        _store.Saved[workspace.Id] = workspace;
        return workspace;
    }

    private static TabState Tab(string table) => new(new TableRef("APP", table), TabMode.Data, [], [], []);

    [Fact]
    public async Task First_attach_creates_and_saves_workspace_1()
    {
        await _manager.AttachAsync(_profile, Ct);

        var workspace = Assert.Single(_manager.Open);
        Assert.Equal("Workspace 1", workspace.Name);
        Assert.Equal(_profile.Id, workspace.ConnectionId);
        Assert.Same(workspace, _manager.Active);
        Assert.Equal(workspace, _store.Saved[workspace.Id]);
        Assert.Empty(_opened); // sessions open on first use
    }

    [Fact]
    public async Task Attach_activates_the_most_recently_active_open_workspace_and_orders_the_bar()
    {
        var older = Stored("A", order: 0, minutesAgo: 10);
        var recent = Stored("B", order: 1, minutesAgo: 1);
        Stored("Closed", isOpen: false, minutesAgo: 0);
        var foreign = new Workspace(Guid.NewGuid(), Guid.NewGuid(), "Other connection");
        _store.Saved[foreign.Id] = foreign;

        await _manager.AttachAsync(_profile, Ct);

        Assert.Equal(["A", "B"], _manager.Open.Select(w => w.Name));
        Assert.Equal(recent.Id, _manager.Active!.Id);
        Assert.Equal(["Closed"], _manager.Closed.Select(w => w.Name));
        Assert.NotEqual(older.Id, _manager.Active.Id);
    }

    [Fact]
    public async Task Attach_without_open_workspace_reopens_the_most_recent_one()
    {
        Stored("Old", isOpen: false, minutesAgo: 60);
        var recent = Stored("Recent", isOpen: false, minutesAgo: 5);

        await _manager.AttachAsync(_profile, Ct);

        Assert.Equal(recent.Id, Assert.Single(_manager.Open).Id);
        Assert.True(_store.Saved[recent.Id].IsOpen);
        Assert.Equal(["Old"], _manager.Closed.Select(w => w.Name));
    }

    [Fact]
    public async Task Attach_reports_unreadable_files()
    {
        _store.LoadErrors.Add("x.json ist kein gültiges Workspace-JSON");

        await _manager.AttachAsync(_profile, Ct);

        Assert.Single(_manager.LoadErrors);
    }

    [Fact]
    public async Task Create_picks_the_next_free_name_activates_and_saves()
    {
        await _manager.AttachAsync(_profile, Ct);

        var second = await _manager.CreateAsync();
        var named = await _manager.CreateAsync("  Bug 3711 ");

        Assert.Equal("Workspace 2", second.Name);
        Assert.Equal("Bug 3711", named.Name);
        Assert.Equal(["Workspace 1", "Workspace 2", "Bug 3711"], _manager.Open.Select(w => w.Name));
        Assert.Equal(named.Id, _manager.Active!.Id);
        Assert.True(_store.Saved.ContainsKey(named.Id));
        await Assert.ThrowsAsync<ArgumentException>(() => _manager.CreateAsync("   "));
    }

    [Fact]
    public async Task Each_workspace_gets_its_own_session_named_after_it()
    {
        await _manager.AttachAsync(_profile, Ct);
        var first = _manager.Active!;
        var second = await _manager.CreateAsync("Bug 3711");

        var data1 = await _manager.GetDataAsync(first.Id, Ct);
        var data1Again = await _manager.GetDataAsync(first.Id, Ct);
        var data2 = await _manager.GetDataAsync(second.Id, Ct);

        Assert.Same(data1, data1Again);
        Assert.NotSame(data1, data2);
        Assert.Equal(["Workspace 1", "Bug 3711"], _opened.Select(o => o.Action));
        await _connector.Received(2).OpenAsync(_profile, "pw", Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Sessions_of_a_read_only_profile_are_locked_others_not()
    {
        var locked = _profile with { Id = Guid.NewGuid(), ReadOnly = true };
        _secrets.SetPassword(locked.Id, "pw");

        await _manager.AttachAsync(_profile, Ct);
        await _manager.GetDataAsync(_manager.Active!.Id, Ct);
        await _manager.AttachAsync(locked, Ct);
        await _manager.GetDataAsync(_manager.Active!.Id, Ct);

        await _opened[0].Connection.DidNotReceive().UseReadOnlySnapshotsAsync(Arg.Any<CancellationToken>());
        await _opened[1].Connection.Received(1).UseReadOnlySnapshotsAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Unlocking_affects_only_that_workspace_and_its_open_session()
    {
        var locked = _profile with { ReadOnly = true };
        await _manager.AttachAsync(locked, Ct);
        var first = _manager.Active!.Id;
        var second = (await _manager.CreateAsync()).Id;
        await _manager.GetDataAsync(first, Ct);

        await _manager.UnlockAsync(first, Ct);

        Assert.True(_manager.IsWritable(first));
        Assert.True(_manager.IsUnlocked(first));
        Assert.False(_manager.IsWritable(second));
        await _opened[0].Connection.Received(1).StopReadOnlySnapshotsAsync(Arg.Any<CancellationToken>());

        await _manager.GetDataAsync(second, Ct);
        await _opened[1].Connection.Received(1).UseReadOnlySnapshotsAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_unlocked_workspace_opens_its_session_without_the_lock()
    {
        await _manager.AttachAsync(_profile with { ReadOnly = true }, Ct);
        var id = _manager.Active!.Id;

        await _manager.UnlockAsync(id, Ct); // opens the session

        var connection = Assert.Single(_opened).Connection;
        await connection.DidNotReceive().UseReadOnlySnapshotsAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Locking_again_locks_the_session_but_not_with_a_writing_transaction_open()
    {
        await _manager.AttachAsync(_profile with { ReadOnly = true }, Ct);
        var id = _manager.Active!.Id;
        await _manager.UnlockAsync(id, Ct);
        var connection = Assert.Single(_opened).Connection;
        connection.Transaction.Returns(new TransactionInfo(TransactionMode.ReadWrite, _time.GetUtcNow()));

        await Assert.ThrowsAsync<InvalidOperationException>(() => _manager.LockAsync(id, Ct));
        Assert.True(_manager.IsWritable(id));

        connection.Transaction.Returns(TransactionInfo.None);
        await _manager.LockAsync(id, Ct);

        Assert.False(_manager.IsWritable(id));
        await connection.Received(1).UseReadOnlySnapshotsAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Disconnecting_or_closing_the_workspace_locks_it_again()
    {
        var locked = _profile with { ReadOnly = true };
        await _manager.AttachAsync(locked, Ct);
        var first = _manager.Active!.Id;
        var second = (await _manager.CreateAsync()).Id;
        await _manager.UnlockAsync(first, Ct);
        await _manager.UnlockAsync(second, Ct);

        await _manager.CloseAsync(second);
        await _manager.ReopenAsync(second);
        Assert.False(_manager.IsWritable(second));

        await _manager.AttachAsync(locked, Ct); // reconnect
        Assert.False(_manager.IsWritable(first));
    }

    [Fact]
    public async Task Profiles_without_the_lock_are_writable_and_never_unlocked()
    {
        await _manager.AttachAsync(_profile, Ct);
        var id = _manager.Active!.Id;

        await _manager.UnlockAsync(id, Ct);

        Assert.True(_manager.IsWritable(id));
        Assert.False(_manager.IsUnlocked(id));
        Assert.Empty(_opened);
    }

    [Fact]
    public async Task A_failed_unlock_leaves_the_workspace_locked()
    {
        await _manager.AttachAsync(_profile with { ReadOnly = true }, Ct);
        var id = _manager.Active!.Id;
        await _manager.GetDataAsync(id, Ct);
        _opened[0].Connection.StopReadOnlySnapshotsAsync(Arg.Any<CancellationToken>()).Throws(new DatabaseException("weg", "ORA-03113"));

        await Assert.ThrowsAsync<DatabaseException>(() => _manager.UnlockAsync(id, Ct));

        Assert.False(_manager.IsWritable(id));
    }

    [Fact]
    public async Task A_session_that_cannot_be_locked_is_closed_and_not_used()
    {
        var locked = _profile with { ReadOnly = true };
        await _manager.AttachAsync(locked, Ct);
        _connector.OpenAsync(Arg.Any<ConnectionProfile>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                var connection = Substitute.For<IDatabaseConnection>();
                connection.UseReadOnlySnapshotsAsync(Arg.Any<CancellationToken>()).Throws(new DatabaseException("no", "ORA-01031"));
                _opened.Add(("locked", connection));
                return connection;
            });

        var error = await Assert.ThrowsAsync<DatabaseException>(() => _manager.GetDataAsync(_manager.Active!.Id, Ct));

        Assert.Equal("ORA-01031", error.ErrorCode);
        await _opened.Single().Connection.Received(1).DisposeAsync();
        Assert.Null(_manager.TransactionOf(_manager.Active!.Id));
    }

    [Fact]
    public async Task A_failed_session_open_is_retried_on_next_use()
    {
        await _manager.AttachAsync(_profile, Ct);
        var id = _manager.Active!.Id;
        _connector.OpenAsync(Arg.Any<ConnectionProfile>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(
                _ => Task.FromException<IDatabaseConnection>(new DatabaseException("listener down", "ORA-12541")),
                _ => Task.FromResult(Substitute.For<IDatabaseConnection>()));

        var error = await Assert.ThrowsAsync<DatabaseException>(() => _manager.GetDataAsync(id, Ct));
        await _manager.GetDataAsync(id, Ct);

        Assert.Equal("ORA-12541", error.ErrorCode);
        await _connector.Received(2).OpenAsync(Arg.Any<ConnectionProfile>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Missing_password_fails_the_session_open()
    {
        await _manager.AttachAsync(_profile, Ct);
        _secrets.DeletePassword(_profile.Id);

        var error = await Assert.ThrowsAsync<DatabaseException>(() => _manager.GetDataAsync(_manager.Active!.Id, Ct));

        Assert.Contains("Passwort", error.Message);
    }

    [Fact]
    public async Task Rename_updates_the_session_action_and_is_saved_after_the_delay()
    {
        await _manager.AttachAsync(_profile, Ct);
        var id = _manager.Active!.Id;
        await _manager.GetDataAsync(id, Ct);

        _manager.Rename(id, " Bug 3711 ");

        Assert.Equal("Bug 3711", _manager.Active!.Name);
        await _opened[0].Connection.Received(1).SetActionAsync("Bug 3711", Arg.Any<CancellationToken>());
        Assert.Equal("Workspace 1", _store.Saved[id].Name);
        _time.Advance(WorkspaceManager.SaveDelay);
        Assert.Equal("Bug 3711", _store.Saved[id].Name);
        Assert.Throws<ArgumentException>(() => _manager.Rename(id, ""));
    }

    [Fact]
    public async Task Tab_updates_are_debounced_into_one_save()
    {
        await _manager.AttachAsync(_profile, Ct);
        var id = _manager.Active!.Id;
        var saves = _store.SaveCount;

        _manager.UpdateTabs(id, [Tab("KUNDEN")], 0);
        _manager.UpdateTabs(id, [Tab("KUNDEN"), Tab("AUFTRAG")], 1);
        Assert.Equal(saves, _store.SaveCount);

        _time.Advance(WorkspaceManager.SaveDelay);

        Assert.Equal(saves + 1, _store.SaveCount);
        Assert.Equal(["KUNDEN", "AUFTRAG"], _store.Saved[id].Tabs.Select(t => t.Table.Name));
        Assert.Equal(1, _store.Saved[id].ActiveTabIndex);
    }

    [Fact]
    public async Task Unchanged_tabs_are_not_saved_again()
    {
        await _manager.AttachAsync(_profile, Ct);
        var id = _manager.Active!.Id;
        _manager.UpdateTabs(id, [Tab("KUNDEN")], 0);
        _time.Advance(WorkspaceManager.SaveDelay);
        var saves = _store.SaveCount;

        _manager.UpdateTabs(id, [Tab("KUNDEN")], 0);
        _time.Advance(WorkspaceManager.SaveDelay);

        Assert.Equal(saves, _store.SaveCount);
    }

    [Fact]
    public async Task Close_saves_disposes_the_session_and_activates_the_neighbor()
    {
        await _manager.AttachAsync(_profile, Ct);
        var first = _manager.Active!;
        var second = await _manager.CreateAsync();
        var third = await _manager.CreateAsync();
        _manager.Activate(second.Id);
        await _manager.GetDataAsync(second.Id, Ct);
        _manager.UpdateTabs(second.Id, [Tab("KUNDEN")], 0);

        await _manager.CloseAsync(second.Id);

        Assert.Equal([first.Id, third.Id], _manager.Open.Select(w => w.Id));
        Assert.Equal(third.Id, _manager.Active!.Id);
        Assert.False(_store.Saved[second.Id].IsOpen);
        Assert.Single(_store.Saved[second.Id].Tabs);
        await _opened[0].Connection.Received(1).DisposeAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => _manager.GetDataAsync(second.Id, Ct));
    }

    [Fact]
    public async Task Closing_the_last_open_workspace_is_refused()
    {
        await _manager.AttachAsync(_profile, Ct);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _manager.CloseAsync(_manager.Active!.Id));
    }

    [Fact]
    public async Task Reopen_appends_to_the_bar_and_activates()
    {
        await _manager.AttachAsync(_profile, Ct);
        var second = await _manager.CreateAsync();
        await _manager.CloseAsync(_manager.Open[0].Id);
        var closed = Assert.Single(_manager.Closed);

        await _manager.ReopenAsync(closed.Id);

        Assert.Equal([second.Id, closed.Id], _manager.Open.Select(w => w.Id));
        Assert.Equal(closed.Id, _manager.Active!.Id);
        Assert.True(_store.Saved[closed.Id].IsOpen);
    }

    [Fact]
    public async Task Only_closed_workspaces_can_be_deleted()
    {
        await _manager.AttachAsync(_profile, Ct);
        var first = _manager.Active!;
        await _manager.CreateAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => _manager.DeleteAsync(first.Id));
        await _manager.CloseAsync(first.Id);
        await _manager.DeleteAsync(first.Id);

        Assert.Empty(_manager.Closed);
        Assert.False(_store.Saved.ContainsKey(first.Id));
    }

    [Fact]
    public async Task Detach_flushes_pending_changes_and_closes_all_sessions()
    {
        await _manager.AttachAsync(_profile, Ct);
        var first = _manager.Active!;
        var second = await _manager.CreateAsync();
        await _manager.GetDataAsync(first.Id, Ct);
        await _manager.GetDataAsync(second.Id, Ct);
        _manager.UpdateTabs(first.Id, [Tab("KUNDEN")], 0);

        await _manager.DetachAsync();

        Assert.Single(_store.Saved[first.Id].Tabs);
        await _opened[0].Connection.Received(1).DisposeAsync();
        await _opened[1].Connection.Received(1).DisposeAsync();
        Assert.Empty(_manager.Open);
        Assert.Null(_manager.Active);
        Assert.Null(_manager.Profile);
    }

    [Fact]
    public async Task Save_failure_is_reported_and_retried()
    {
        await _manager.AttachAsync(_profile, Ct);
        var id = _manager.Active!.Id;
        _store.FailNextSave = new IOException("Datei gesperrt");

        _manager.UpdateTabs(id, [Tab("KUNDEN")], 0);
        await _manager.FlushAsync();

        Assert.Contains("Datei gesperrt", _manager.SaveError);
        Assert.Empty(_store.Saved[id].Tabs);

        await _manager.FlushAsync();

        Assert.Null(_manager.SaveError);
        Assert.Single(_store.Saved[id].Tabs);
    }

    [Fact]
    public async Task Delete_for_connection_removes_its_stored_workspaces()
    {
        Stored("A");
        Stored("B", isOpen: false);

        await _manager.DeleteForConnectionAsync(_profile.Id);

        Assert.Empty(_store.Saved);
    }
}
