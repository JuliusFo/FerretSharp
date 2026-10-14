using FerretSharp.Core.AppTrace;
using FerretSharp.Core.Connections;
using FerretSharp.Core.Resources;
using FerretSharp.UI.Resources;
using Microsoft.Extensions.Logging;

namespace FerretSharp.UI.State;

/// <summary>Where a trace stands.</summary>
public enum TraceStatus
{
    /// <summary>Not attached yet.</summary>
    Idle,

    Attaching,

    Recording,

    /// <summary>Detached by the user; the recording is still shown.</summary>
    Detached,

    /// <summary>The app ended; the recording is still shown.</summary>
    Ended,

    /// <summary>Attaching or reading failed (<see cref="AppTraceService.TraceError"/>).</summary>
    Failed,
}

/// <summary>How the statements of the page are sorted (all descending).</summary>
public enum StatementSort
{
    Time,
    Count,
    Average,
    Max,
    Rows,
}

/// <summary>What the database knows about the selected statement (<see cref="AppTraceService.Lookup"/>).</summary>
/// <param name="Cursor">Null with <paramref name="Loading"/> or <paramref name="Error"/> set, or when the statement is not in the shared pool.</param>
public sealed record CursorLookup(string Text, Guid ProfileId, bool Loading, Core.Query.CursorInfo? Cursor, string? Error);

/// <summary>
/// The "App statements" page's state (WP-33): the local .NET processes, the one attached to and its recording, and the
/// connection the app works on. One for the whole app (not per connection): a trace goes on while the user is elsewhere.
/// Events arrive on the source's thread; <see cref="Changed"/> reports them at most every <see cref="RenderInterval"/>
/// (an app runs thousands of statements a second – one render per event would stall the UI), everything else at once.
/// Attaching, detaching and disposing run one after the other: a slow stop (an app halted at a breakpoint) must not let a
/// second attach start a trace nothing stops (review of WP-33). The view state (selection, filter, sort) lives here too –
/// the page is built anew when another connection is shown, e.g. when a look-up connects.
/// </summary>
public sealed class AppTraceService(IAppTraceSource source, ConnectionManager connections, ILogger<AppTraceService> logger, TimeProvider? timeProvider = null) : IAsyncDisposable
{
    public static readonly TimeSpan RenderInterval = TimeSpan.FromMilliseconds(250);

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly Lock _lock = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private StatementRecording _recording = new();
    private CancellationTokenSource? _cts;
    private Task _pump = Task.CompletedTask;
    private ITimer? _renderTimer;
    private ConnectionProfile? _chosen;
    private RecordingSnapshot _snapshot = RecordingSnapshot.Empty;
    private int _switching;
    private bool _disposed;

    /// <summary>Something changed; may fire on a background thread.</summary>
    public event Action? Changed;

    public IReadOnlyList<TraceableProcess> Processes { get; private set; } = [];

    public bool ListingProcesses { get; private set; }

    /// <summary>Why the process list could not be read.</summary>
    public string? ListError { get; private set; }

    public TraceStatus Status { get; private set; } = TraceStatus.Idle;

    /// <summary>An attach or detach is under way (it waits for the trace before to stop): no second one now.</summary>
    public bool Switching => Volatile.Read(ref _switching) > 0;

    /// <summary>Attached, but new statements are left out (<see cref="SetPaused"/>); a new attach starts unpaused.</summary>
    public bool Paused { get; private set; }

    /// <summary>The process attached to (or last attached to); null before the first attach.</summary>
    public TraceableProcess? Process { get; private set; }

    /// <summary>Why attaching or reading failed.</summary>
    public string? TraceError { get; private set; }

    /// <summary>The recording so far, as of the last report.</summary>
    public RecordingSnapshot Snapshot
    {
        get
        {
            lock (_lock)
            {
                return _snapshot.Version == _recording.Version ? _snapshot : _snapshot = _recording.Snapshot();
            }
        }
    }

    private (long Version, StatementOrigin? Filter, StatementSort Sort, IReadOnlyList<StatementTotals> Statements)? _shown;

    /// <summary>
    /// The statements the page lists: those of <see cref="OriginFilter"/>, ordered by <see cref="Sort"/> – computed once
    /// per recording, filter and order (R5b, review 3.31.0 B10: the page filtered and sorted up to 2000 statements on
    /// every render, four times a second while recording). The recording is ordered by time already.
    /// </summary>
    public IReadOnlyList<StatementTotals> ShownStatements
    {
        get
        {
            var snapshot = Snapshot;
            var (filter, sort) = (OriginFilter, Sort);
            lock (_lock)
            {
                if (_shown is { } shown && shown.Version == snapshot.Version && shown.Filter == filter && shown.Sort == sort)
                {
                    return shown.Statements;
                }
            }

            var statements = filter is null ? snapshot.Statements : snapshot.Statements.Where(s => s.Origin == filter);
            IReadOnlyList<StatementTotals> ordered = (sort switch
            {
                StatementSort.Count => statements.OrderByDescending(s => s.Count),
                StatementSort.Average => statements.OrderByDescending(s => s.Average),
                StatementSort.Max => statements.OrderByDescending(s => s.Max),
                StatementSort.Rows => statements.OrderByDescending(s => s.Rows),
                _ => statements, // by time, as recorded
            }).ToList();
            lock (_lock)
            {
                _shown = (snapshot.Version, filter, sort, ordered);
            }

            return ordered;
        }
    }

    /// <summary>
    /// The connection of the app: the one the user chose, else by the app's environment or the data source of its
    /// commands (<see cref="AppConnectionMatch.Find"/>).
    /// </summary>
    public AppConnectionMatch Match
    {
        get
        {
            var profiles = connections.Profiles;
            if (_chosen is { } chosen && profiles.FirstOrDefault(p => p.Id == chosen.Id) is { } current)
            {
                return new AppConnectionMatch(current, MatchKind.Chosen, []);
            }

            return AppConnectionMatch.Find(profiles, Process?.Environment, Snapshot.DataSource);
        }
    }

    /// <summary>The statement shown in detail.</summary>
    public (StatementOrigin Origin, string Text)? Selected { get; private set; }

    /// <summary>The last look-up in the database, for <see cref="CursorLookup.Text"/>.</summary>
    public CursorLookup? Lookup { get; private set; }

    /// <summary>Only the statements of this origin; null = all.</summary>
    public StatementOrigin? OriginFilter { get; set; }

    public StatementSort Sort { get; set; } = StatementSort.Time;

    /// <summary>The process list shows build tools too (<see cref="TraceableProcess.IsTooling"/>).</summary>
    public bool ShowTooling { get; set; }

    /// <summary>The process list is shown although a trace is there ("Other app …").</summary>
    public bool ChoosingProcess { get; set; }

    public void Select(StatementTotals? statement)
    {
        Selected = statement is null ? null : (statement.Origin, statement.Text);
        Raise();
    }

    public void SetLookup(CursorLookup? lookup)
    {
        Lookup = lookup;
        Raise();
    }

    /// <summary>Reads the list of processes again (not watched in the background, decided with the user).</summary>
    public async Task RefreshProcessesAsync()
    {
        ListingProcesses = true;
        Raise();
        try
        {
            Processes = await Task.Run(source.ListProcesses);
            ListError = null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Listing .NET processes failed");
            ListError = TextFormat.Format(AppStatementsText.AppStatements_ListFailed, ex.Message);
        }
        finally
        {
            ListingProcesses = false;
            Raise();
        }
    }

    /// <summary>Attaches to the process – detaching from the one before – and starts a new recording.</summary>
    public async Task AttachAsync(TraceableProcess process)
    {
        await SwitchAsync(() =>
        {
            var cts = new CancellationTokenSource();
            var recording = new StatementRecording();
            lock (_lock)
            {
                _recording = recording;
                _snapshot = RecordingSnapshot.Empty;
            }

            _cts = cts;
            Paused = false;
            _chosen = null;
            Selected = null;
            Lookup = null;
            OriginFilter = null;
            ChoosingProcess = false;
            Process = process;
            TraceError = null;
            Status = TraceStatus.Attaching;
            _pump = Task.Run(() => PumpAsync(process, recording, cts));
        });
    }

    /// <summary>Stops the trace; the recording stays.</summary>
    public Task DetachAsync() => SwitchAsync(() =>
    {
        if (Status is TraceStatus.Attaching or TraceStatus.Recording)
        {
            Status = TraceStatus.Detached;
        }
    });

    /// <summary>Forgets what was recorded so far; a running trace goes on (paused, if it was).</summary>
    public void Reset()
    {
        lock (_lock)
        {
            _recording.Reset();
        }

        Raise();
    }

    /// <summary>Pauses or resumes the recording; the trace stays attached, the recording so far stays.</summary>
    public void SetPaused(bool paused)
    {
        lock (_lock)
        {
            Paused = paused;
            _recording.Paused = paused;
        }

        Raise();
    }

    /// <summary>The user picked the connection of the app (none of the candidates fit by themselves).</summary>
    public void Choose(ConnectionProfile profile)
    {
        _chosen = profile;
        Raise();
    }

    /// <summary>
    /// Remembers the app's environment at the connection (<see cref="ConnectionProfile.AppEnvironment"/>), so the next
    /// trace of the app finds it by itself. The choice stays if the environment is still not unique (another connection
    /// has it too).
    /// </summary>
    public async Task RememberEnvironmentAsync(ConnectionProfile profile, CancellationToken cancellationToken)
    {
        if (Process?.Environment is not { } environment)
        {
            return;
        }

        await connections.SaveAsync(profile with { AppEnvironment = environment }, null, cancellationToken);
        if (AppConnectionMatch.Find(connections.Profiles, environment, null).Profile?.Id == profile.Id)
        {
            _chosen = null; // found by the environment from now on
        }

        Raise();
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            _disposed = true;
            await StopAsync();
            lock (_lock)
            {
                _renderTimer?.Dispose();
                _renderTimer = null;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Stops the running trace and then does <paramref name="next"/> – one switch at a time.</summary>
    private async Task SwitchAsync(Action next)
    {
        Interlocked.Increment(ref _switching);
        Raise();
        await _gate.WaitAsync();
        try
        {
            if (_disposed)
            {
                return;
            }

            await StopAsync();
            next();
        }
        finally
        {
            _gate.Release();
            Interlocked.Decrement(ref _switching);
            Raise();
        }
    }

    private async Task PumpAsync(TraceableProcess process, StatementRecording recording, CancellationTokenSource cts)
    {
        try
        {
            var first = true;
            await foreach (var e in source.AttachAsync(process.Id, cts.Token))
            {
                if (first)
                {
                    first = false;
                    SetStatus(cts, TraceStatus.Recording);
                }

                bool changed;
                lock (_lock)
                {
                    changed = recording.Apply(e); // its own recording: a replaced trace never writes into the next one
                }

                if (changed)
                {
                    RaiseSoon();
                }
            }

            if (first)
            {
                SetStatus(cts, TraceStatus.Recording); // attached, but the app was quiet until it ended
            }

            SetStatus(cts, TraceStatus.Ended);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // detached
        }
        catch (RefusedException ex)
        {
            SetStatus(cts, TraceStatus.Failed, ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Tracing process {ProcessId} failed", process.Id);
            SetStatus(cts, TraceStatus.Failed, TextFormat.Format(AppStatementsText.AppStatements_ReadFailed, ex.Message));
        }
    }

    /// <summary>Sets the status unless the trace was stopped or replaced meanwhile.</summary>
    private void SetStatus(CancellationTokenSource cts, TraceStatus status, string? error = null)
    {
        if (cts.IsCancellationRequested || !ReferenceEquals(_cts, cts))
        {
            return;
        }

        Status = status;
        TraceError = error;
        Raise();
    }

    /// <summary>Cancels the running trace and waits for its end (the source closes it within seconds, also when halted).</summary>
    private async Task StopAsync()
    {
        if (_cts is not { } cts)
        {
            return;
        }

        _cts = null;
        await cts.CancelAsync();
        try
        {
            await _pump;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Stopping a trace failed");
        }

        cts.Dispose();
    }

    /// <summary>Reports a new event, at most every <see cref="RenderInterval"/>.</summary>
    private void RaiseSoon()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _renderTimer ??= _time.CreateTimer(_ => { lock (_lock) { _renderTimer?.Dispose(); _renderTimer = null; } Raise(); }, null, RenderInterval, Timeout.InfiniteTimeSpan);
        }
    }

    private void Raise() => Changed?.Invoke();
}
