using FerretSharp.Core.Connections;
using FerretSharp.Core.Resources;
using FerretSharp.Core.Schema;
using FerretSharp.Testing;
using FerretSharp.Core.Workspaces;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Microsoft.Extensions.Time.Testing;

namespace FerretSharp.Core.Tests.Connections;

public sealed class ActiveConnectionTests : IDisposable
{
    private readonly TestFolder _folder = new();
    private readonly InMemorySecretStore _secrets = new();
    private readonly ISchemaReader _reader = FakeDatabase.Reader(new TableSummary("APP_USER", "KUNDEN", TableKind.Table));
    private readonly IDatabaseConnection _connection;
    private readonly IDatabaseConnector _connector;
    private readonly RecentConnections _recent;
    private readonly InMemoryWorkspaceStore _workspaceStore = new();
    private readonly WorkspaceManager _workspaces;
    private readonly ActiveConnection _active;
    private readonly ConnectionProfile _profile = TestProfiles.HostPort();

    public ActiveConnectionTests()
    {
        _connection = FakeDatabase.Connection(_reader, serverVersion: "23.26.3.0.0");
        _connector = FakeDatabase.Connector(_connection);
        _recent = new RecentConnections(_folder.Combine("recent.json"));
        var connections = new ConnectionManager(Substitute.For<IConnectionStore>(), _secrets);
        _workspaces = new WorkspaceManager(_workspaceStore, connections, _connector);
        _active = new ActiveConnection(connections, _connector, _recent, _workspaces);

        _secrets.SetPassword(_profile.Id, "pw");
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Connect_opens_session_loads_schema_of_effective_owner_and_records_usage()
    {
        var statuses = new List<ConnectionStatus>();
        _active.Changed += () => statuses.Add(_active.Status);

        await _active.ConnectAsync(_profile, Ct);

        Assert.Equal([ConnectionStatus.Connecting, ConnectionStatus.Connected], statuses);
        Assert.Equal("APP_USER", _active.Schema!.Owner);
        Assert.Single(_active.Schema.Tables);
        Assert.Equal("23.26.3.0.0", _active.ServerVersion);
        await _connector.Received(1).OpenAsync(_profile, "pw", ActiveConnection.ExplorerAction, Arg.Any<CancellationToken>());
        Assert.True(_recent.LastUsed.ContainsKey(_profile.Id));
    }

    /// <summary>WP-63 (review): the schema knows the audit tables by the template of the connection (built-in here).</summary>
    [Fact]
    public async Task The_schema_pairs_audit_tables_by_the_connections_template()
    {
        var reader = FakeDatabase.Reader(new TableSummary("APP_USER", "KUNDEN", TableKind.Table), new TableSummary("APP_USER", "AUDIT_KUNDEN", TableKind.Table));
        var connector = FakeDatabase.Connector(FakeDatabase.Connection(reader));
        var settings = new Core.Settings.AppSettingsService(new Core.Settings.SettingsStore(_folder.Combine("settings.json")), Core.Settings.AppSettings.Default);
        var connections = new ConnectionManager(Substitute.For<IConnectionStore>(), _secrets);
        await using var active = new ActiveConnection(connections, connector, _recent, new WorkspaceManager(_workspaceStore, connections, connector),
            auditTemplates: new Core.Audit.AuditTemplateSource(settings));

        await active.ConnectAsync(_profile, Ct);

        Assert.Same(Core.Audit.AuditTemplates.Default, active.Schema!.AuditTemplate);
        Assert.Equal(new TableRef("APP_USER", "KUNDEN"), active.Schema.AuditBaseOf(new TableRef("APP_USER", "AUDIT_KUNDEN")));
    }

    /// <summary>A colleague's connect seemed to hang (2026-10-14): each step is shown while it runs and kept with its duration.</summary>
    [Fact]
    public async Task Connect_reports_each_step_and_keeps_them_with_their_durations()
    {
        var shown = new List<string?>();
        _active.StepChanged += () => shown.Add(_active.Step);

        await _active.ConnectAsync(_profile, Ct);

        string[] steps =
        [
            ConnectionText.Step_OpeningSession, ConnectionText.Step_ReadingTables, ConnectionText.Step_ReadingSynonyms,
            ConnectionText.Step_ReadingPlSql, TextFormat.Format(ConnectionText.Step_ReadingKeys, "APP_USER", 1, 1),
            ConnectionText.Step_RestoringWorkspaces,
        ];
        Assert.Equal([.. steps, null], shown);
        Assert.Null(_active.Step);
        Assert.Equal(steps, _active.Steps.Select(s => s.Name));
        Assert.All(_active.Steps, s => Assert.True(s.Duration >= TimeSpan.Zero));
        Assert.NotNull(_active.ConnectStartedAt);
    }

    [Fact]
    public async Task Connecting_again_first_closes_the_previous_sessions_as_a_step()
    {
        await _active.ConnectAsync(_profile, Ct);

        await _active.ConnectAsync(_profile, Ct);

        Assert.Equal(ConnectionText.Step_ClosingPrevious, _active.Steps[0].Name);
        Assert.Equal(ConnectionText.Step_OpeningSession, _active.Steps[1].Name);
    }

    /// <summary>ODP.NET runs the TCP connect to its timeout (15 s) whatever the token says: Cancel did nothing until then.</summary>
    [Fact]
    public async Task Cancel_returns_while_opening_and_a_session_that_opens_later_is_closed()
    {
        var opening = new TaskCompletionSource<IDatabaseConnection>();
        _connector.OpenAsync(Arg.Any<ConnectionProfile>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(opening.Task);
        var connecting = _active.ConnectAsync(_profile, Ct);
        await Wait.UntilAsync(() => _connector.ReceivedCalls().Any()); // the attempt runs on the thread pool

        _active.CancelPendingConnect();
        await connecting.WaitAsync(TimeSpan.FromSeconds(5), Ct);

        Assert.Equal(ConnectionStatus.Disconnected, _active.Status);
        opening.SetResult(_connection);
        await Wait.UntilAsync(() => _connection.ReceivedCalls().Any(c => c.GetMethodInfo().Name == nameof(IAsyncDisposable.DisposeAsync)));
    }

    /// <summary>
    /// After sleep (2026-10-21) the log stood at "Opening the session" for 20 s: the open hanging, or the UI thread holding up
    /// the next step? The end of the open is reported from the thread pool, whatever the caller's context does.
    /// </summary>
    [Fact]
    public async Task The_end_of_opening_the_session_is_reported_with_its_duration()
    {
        var ended = new List<SessionOpenOutcome>();
        _active.SessionOpenEnded += o => { lock (ended) { ended.Add(o); } };

        await _active.ConnectAsync(_profile, Ct);

        await Wait.UntilAsync(() => { lock (ended) { return ended.Count == 1; } });
        Assert.Null(ended[0].Error);
        Assert.True(ended[0].Synchronous); // the fake returns a completed task
        Assert.True(ended[0].Took >= TimeSpan.Zero);
    }

    /// <summary>
    /// Over the VPN opening sometimes hung for 20 s while the next attempt opened at once (log 2026-10-21 to 10-28): an attempt
    /// past its limit makes way for the next, shown as a step of its own; the late session is closed.
    /// </summary>
    [Fact]
    public async Task An_open_past_its_limit_is_tried_again_as_a_step_of_its_own()
    {
        var time = new CountingTimeProvider();
        var active = new ActiveConnection(new ConnectionManager(Substitute.For<IConnectionStore>(), _secrets), _connector, _recent, _workspaces, time);
        var hanging = new TaskCompletionSource<IDatabaseConnection>();
        var second = new TaskCompletionSource<IDatabaseConnection>();
        var late = FakeDatabase.Connection(_reader);
        _connector.OpenAsync(Arg.Any<ConnectionProfile>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(hanging.Task, second.Task);
        var connecting = active.ConnectAsync(_profile, Ct);
        await Wait.UntilAsync(() => time.Timers == 1);

        time.Advance(SessionOpening.AttemptLimits[0]);
        await Wait.UntilAsync(() => _connector.ReceivedCalls().Count() == 2);
        second.SetResult(_connection);
        await connecting.WaitAsync(TimeSpan.FromSeconds(5), Ct);

        Assert.Equal(ConnectionStatus.Connected, active.Status);
        Assert.Equal(
            [ConnectionText.Step_OpeningSession, TextFormat.Format(ConnectionText.Step_OpeningSessionAgain, 2, SessionOpening.AttemptLimits.Count)],
            active.Steps.Take(2).Select(s => s.Name));
        hanging.SetResult(late);
        await Wait.UntilAsync(() => late.ReceivedCalls().Any(c => c.GetMethodInfo().Name == nameof(IAsyncDisposable.DisposeAsync)));
    }

    /// <summary>After sleep it hung beyond ODP.NET's 15 s (2026-10-21): reconnecting by itself must not show "Connecting" for ever.</summary>
    [Fact]
    public async Task When_every_attempt_runs_past_its_limit_the_connect_fails_as_timed_out()
    {
        var time = new CountingTimeProvider();
        var active = new ActiveConnection(new ConnectionManager(Substitute.For<IConnectionStore>(), _secrets), _connector, _recent, _workspaces, time);
        _connector.OpenAsync(Arg.Any<ConnectionProfile>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new TaskCompletionSource<IDatabaseConnection>().Task);
        var connecting = active.ConnectAsync(_profile, Ct);
        for (var attempt = 1; attempt <= SessionOpening.AttemptLimits.Count; attempt++)
        {
            await Wait.UntilAsync(() => time.Timers == attempt);
            time.Advance(SessionOpening.AttemptLimits[attempt - 1]);
        }

        await connecting.WaitAsync(TimeSpan.FromSeconds(5), Ct);

        Assert.Equal(ConnectionStatus.Failed, active.Status);
        Assert.True(active.Error!.TimedOut);
        Assert.Null(active.Error.ErrorCode);
        Assert.True(ReconnectPolicy.IsWorthRetrying(active.Error));
    }

    /// <summary>Review of the fix (7a): cancelling while a later attempt runs disconnects and starts no further one.</summary>
    [Fact]
    public async Task Cancelling_during_a_later_attempt_disconnects_without_another_one()
    {
        var time = new CountingTimeProvider();
        var active = new ActiveConnection(new ConnectionManager(Substitute.For<IConnectionStore>(), _secrets), _connector, _recent, _workspaces, time);
        var hanging = new TaskCompletionSource<IDatabaseConnection>();
        _connector.OpenAsync(Arg.Any<ConnectionProfile>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(hanging.Task);
        var connecting = active.ConnectAsync(_profile, Ct);
        await Wait.UntilAsync(() => time.Timers == 1);
        time.Advance(SessionOpening.AttemptLimits[0]);
        await Wait.UntilAsync(() => time.Timers == 2 && _connector.ReceivedCalls().Count() == 2);

        active.CancelPendingConnect();
        await connecting.WaitAsync(TimeSpan.FromSeconds(5), Ct);

        Assert.Equal(ConnectionStatus.Disconnected, active.Status);
        time.Advance(SessionOpening.AttemptLimits.Aggregate(TimeSpan.Zero, (sum, l) => sum + l));
        Assert.Equal(2, _connector.ReceivedCalls().Count());
        hanging.SetResult(_connection);
        await Wait.UntilAsync(() => _connection.ReceivedCalls().Any(c => c.GetMethodInfo().Name == nameof(IAsyncDisposable.DisposeAsync)));
    }

    [Fact]
    public async Task An_open_that_ends_after_a_cancel_is_still_reported()
    {
        var opening = new TaskCompletionSource<IDatabaseConnection>();
        _connector.OpenAsync(Arg.Any<ConnectionProfile>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(opening.Task);
        SessionOpenOutcome? ended = null;
        _active.SessionOpenEnded += o => ended = o;
        var connecting = _active.ConnectAsync(_profile, Ct);
        await Wait.UntilAsync(() => _connector.ReceivedCalls().Any()); // the attempt runs on the thread pool

        _active.CancelPendingConnect();
        await connecting.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.Null(ended);
        opening.SetException(new DatabaseException("timeout", "ORA-12170"));

        await Wait.UntilAsync(() => ended is not null);
        Assert.False(ended!.Synchronous);
        Assert.Contains("timeout", ended.Error);
    }

    [Fact]
    public async Task A_failed_connect_keeps_the_steps_up_to_the_failure()
    {
        _reader.GetSynonymTargetsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new DatabaseException("timeout", "ORA-12170"));

        await _active.ConnectAsync(_profile, Ct);

        Assert.Equal(ConnectionStatus.Failed, _active.Status);
        Assert.Null(_active.Step);
        Assert.Equal([ConnectionText.Step_OpeningSession, ConnectionText.Step_ReadingTables, ConnectionText.Step_ReadingSynonyms],
            _active.Steps.Select(s => s.Name));
    }

    [Fact]
    public async Task Oracle_failure_ends_in_failed_status_with_error_code()
    {
        _connector.OpenAsync(Arg.Any<ConnectionProfile>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Throws(new DatabaseException("invalid credential", "ORA-01017"));

        await _active.ConnectAsync(_profile, Ct);

        Assert.Equal(ConnectionStatus.Failed, _active.Status);
        Assert.Equal(new ConnectionError("invalid credential", "ORA-01017"), _active.Error);
        Assert.Same(_profile, _active.Profile);
        Assert.Null(_active.Schema);
    }

    /// <summary>WP-35: a wrong password typed for this run only is asked for again; a saved one stays (changed under "Edit").</summary>
    [Fact]
    public async Task Wrong_password_for_this_run_is_forgotten_a_saved_one_is_kept()
    {
        var connections = new ConnectionManager(Substitute.For<IConnectionStore>(), _secrets);
        var active = new ActiveConnection(connections, _connector, _recent, _workspaces);
        var imported = TestProfiles.HostPort("Importiert");
        connections.SetPassword(imported.Id, "falsch", remember: false);
        _connector.OpenAsync(Arg.Any<ConnectionProfile>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Throws(new DatabaseException("invalid credential", ConnectionManager.WrongPasswordCode));

        await active.ConnectAsync(imported, Ct);
        await active.ConnectAsync(_profile, Ct);

        Assert.False(connections.HasPassword(imported.Id));
        Assert.Equal("pw", connections.GetPassword(_profile.Id));
    }

    /// <summary>Review 3.31.0, A11: a mistyped password with "save" reached the Credential Manager and was never asked for again.</summary>
    [Fact]
    public async Task A_password_to_save_is_saved_only_when_it_worked()
    {
        var connections = new ConnectionManager(Substitute.For<IConnectionStore>(), _secrets);
        var active = new ActiveConnection(connections, _connector, _recent, _workspaces);
        var imported = TestProfiles.HostPort("Importiert");
        connections.SetPassword(imported.Id, "falsch", remember: true);
        _connector.OpenAsync(Arg.Any<ConnectionProfile>(), "falsch", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Throws(new DatabaseException("invalid credential", ConnectionManager.WrongPasswordCode));

        await active.ConnectAsync(imported, Ct);
        Assert.False(connections.HasPassword(imported.Id)); // asked for again
        Assert.Null(_secrets.GetPassword(imported.Id));

        connections.SetPassword(imported.Id, "richtig", remember: true);
        await active.ConnectAsync(imported, Ct);

        Assert.Equal(ConnectionStatus.Connected, active.Status);
        Assert.Equal("richtig", _secrets.GetPassword(imported.Id));
    }

    [Fact]
    public async Task Missing_password_fails_without_contacting_the_database()
    {
        _secrets.DeletePassword(_profile.Id);

        await _active.ConnectAsync(_profile, Ct);

        Assert.Equal(ConnectionStatus.Failed, _active.Status);
        Assert.Contains("Passwort", _active.Error!.Message);
        await _connector.DidNotReceiveWithAnyArgs().OpenAsync(default!, default!, default!, Ct);
    }

    [Fact]
    public async Task Schema_load_failure_closes_the_session()
    {
        _reader.GetTablesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Throws(new DatabaseException("no access", "ORA-00942"));

        await _active.ConnectAsync(_profile, Ct);

        Assert.Equal(ConnectionStatus.Failed, _active.Status);
        await _connection.Received(1).DisposeAsync();
    }

    [Fact]
    public async Task Disconnect_closes_session_and_clears_state()
    {
        await _active.ConnectAsync(_profile, Ct);

        await _active.DisconnectAsync();

        Assert.Equal(ConnectionStatus.Disconnected, _active.Status);
        Assert.Null(_active.Profile);
        Assert.Null(_active.Schema);
        await _connection.Received(1).DisposeAsync();
    }

    [Fact]
    public async Task Connect_attaches_the_workspaces_and_disconnect_detaches_them()
    {
        await _active.ConnectAsync(_profile, Ct);

        Assert.Same(_profile, _workspaces.Profile);
        Assert.Single(_workspaces.Open);

        await _active.DisconnectAsync();

        Assert.Null(_workspaces.Profile);
        Assert.Empty(_workspaces.Open);
        Assert.Single(_workspaceStore.Saved);
    }

    [Fact]
    public async Task Connecting_to_another_profile_switches_the_workspaces()
    {
        var other = TestProfiles.HostPort("Other");
        _secrets.SetPassword(other.Id, "pw2");
        await _active.ConnectAsync(_profile, Ct);

        await _active.ConnectAsync(other, Ct);

        Assert.Same(other, _workspaces.Profile);
        Assert.Equal(other.Id, Assert.Single(_workspaces.Open).ConnectionId);
    }

    [Fact]
    public async Task Connecting_to_another_profile_closes_the_previous_session()
    {
        var other = TestProfiles.HostPort("Other");
        _secrets.SetPassword(other.Id, "pw2");
        await _active.ConnectAsync(_profile, Ct);

        await _active.ConnectAsync(other, Ct);

        Assert.Same(other, _active.Profile);
        await _connection.Received(1).DisposeAsync();
    }

    [Fact]
    public async Task Lost_session_is_recorded_once_and_cleared_by_connecting_again()
    {
        await _active.ConnectAsync(_profile, Ct);
        var first = new DatabaseException("connection lost", "ORA-03113");
        var changes = 0;
        _active.Changed += () => changes++;

        Assert.False(_active.ReportLost(new DatabaseException("no such table", "ORA-00942")));
        Assert.True(_active.ReportLost(first));
        Assert.False(_active.ReportLost(new DatabaseException("again", "ORA-03135")));

        Assert.Same(first, _active.Lost);
        Assert.Equal(1, changes);

        await _active.ConnectAsync(_profile, Ct);

        Assert.Null(_active.Lost);
    }

    [Fact]
    public void Lost_session_is_ignored_while_not_connected()
    {
        Assert.False(_active.ReportLost(new DatabaseException("connection lost", "ORA-03113")));
        Assert.Null(_active.Lost);
    }

    [Fact]
    public async Task Clear_lost_removes_the_banner_state_right_away()
    {
        await _active.ConnectAsync(_profile, Ct);
        _active.ReportLost(new DatabaseException("connection lost", "ORA-03113"));

        _active.ClearLost();

        Assert.Null(_active.Lost);
        Assert.True(_active.IsConnected);
    }

    public void Dispose()
    {
        _folder.Dispose();
    }

    /// <summary>
    /// Review of WP-66: the profile saved while connecting (a renamed group) was replaced again with the one the connect
    /// started with once it was done. What the session works with still comes from the start.
    /// </summary>
    [Fact]
    public async Task A_profile_saved_while_connecting_stays_once_connected()
    {
        var opening = new TaskCompletionSource<IDatabaseConnection>();
        _connector.OpenAsync(Arg.Any<ConnectionProfile>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(opening.Task);
        var connecting = _active.ConnectAsync(_profile, Ct);
        await Wait.UntilAsync(() => _active.Step == ConnectionText.Step_OpeningSession);

        _active.UseSaved(_profile with { Group = "Kasse-Neu" });
        opening.SetResult(_connection);
        await connecting.WaitAsync(TimeSpan.FromSeconds(5), Ct);

        Assert.Equal(ConnectionStatus.Connected, _active.Status);
        Assert.Equal("Kasse-Neu", _active.Profile!.Group);
    }
}
