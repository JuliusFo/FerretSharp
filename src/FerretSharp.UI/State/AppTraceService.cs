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

    /// <summary>Attaching or reading failed (<see cref="AppTraceService.Error"/>).</summary>
    Failed,
}

/// <summary>What the database knows about the selected statement (<see cref="AppTraceService.Lookup"/>).</summary>
/// <param name="Cursor">Null with <paramref name="Loading"/> or <paramref name="Error"/> set, or when the statement is not in the shared pool.</param>
public sealed record CursorLookup(string Text, Guid ProfileId, bool Loading, Core.Query.CursorInfo? Cursor, string? Error);

/// <summary>
/// The "App statements" page's state (WP-33): the local .NET processes, the one attached to and its recording, and the
/// connection the app works on. One for the whole app (not per connection): a trace goes on while the user is elsewhere.
/// Events arrive on the source's thread; <see cref="Changed"/> reports them at most every <see cref="RenderInterval"/>
/// (an app runs thousands of statements a second – one render per event would stall the UI), everything else at once.
/// </summary>
public sealed class AppTraceService(IAppTraceSource source, ConnectionManager connections, ILogger<AppTraceService> logger, TimeProvider? timeProvider = null) : IAsyncDisposable
{
    public static readonly TimeSpan RenderInterval = TimeSpan.FromMilliseconds(250);

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly Lock _lock = new();
    private StatementRecording _recording = new();
    private CancellationTokenSource? _cts;
    private Task _pump = Task.CompletedTask;
    private ITimer? _renderTimer;
    private ConnectionProfile? _chosen;
    private RecordingSnapshot _snapshot = RecordingSnapshot.Empty;

    /// <summary>Something changed; may fire on a background thread.</summary>
    public event Action? Changed;

    public IReadOnlyList<TraceableProcess> Processes { get; private set; } = [];

    public bool ListingProcesses { get; private set; }

    public TraceStatus Status { get; private set; } = TraceStatus.Idle;

    /// <summary>The process attached to (or last attached to); null before the first attach.</summary>
    public TraceableProcess? Process { get; private set; }

    /// <summary>Why attaching or reading failed, or why the process list could not be read.</summary>
    public string? Error { get; private set; }

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

    /// <summary>The statement shown in detail; kept here because the page is built anew when another connection is shown.</summary>
    public (StatementOrigin Origin, string Text)? Selected { get; private set; }

    /// <summary>The last look-up in the database, for <see cref="CursorLookup.Text"/>.</summary>
    public CursorLookup? Lookup { get; private set; }

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
            Error = null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Listing .NET processes failed");
            Error = TextFormat.Format(AppStatementsText.AppStatements_ListFailed, ex.Message);
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
        await StopAsync();
        var cts = new CancellationTokenSource();
        lock (_lock)
        {
            _recording = new StatementRecording();
            _snapshot = RecordingSnapshot.Empty;
        }

        _cts = cts;
        _chosen = null;
        Selected = null;
        Lookup = null;
        Process = process;
        Error = null;
        Status = TraceStatus.Attaching;
        Raise();
        _pump = Task.Run(() => PumpAsync(process, cts));
    }

    /// <summary>Stops the trace; the recording stays.</summary>
    public async Task DetachAsync()
    {
        if (Status is TraceStatus.Attaching or TraceStatus.Recording)
        {
            await StopAsync();
            Status = TraceStatus.Detached;
            Raise();
        }
    }

    /// <summary>Forgets what was recorded so far; a running trace goes on.</summary>
    public void Reset()
    {
        lock (_lock)
        {
            _recording.Reset();
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
    /// trace of the app finds it by itself.
    /// </summary>
    public async Task RememberEnvironmentAsync(ConnectionProfile profile, CancellationToken cancellationToken)
    {
        if (Process?.Environment is not { } environment)
        {
            return;
        }

        await connections.SaveAsync(profile with { AppEnvironment = environment }, null, cancellationToken);
        _chosen = null; // found by the environment from now on
        Raise();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _renderTimer?.Dispose();
    }

    private async Task PumpAsync(TraceableProcess process, CancellationTokenSource cts)
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
                    changed = _recording.Apply(e);
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
        Error = error;
        Raise();
    }

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
            _renderTimer ??= _time.CreateTimer(_ => { lock (_lock) { _renderTimer?.Dispose(); _renderTimer = null; } Raise(); }, null, RenderInterval, Timeout.InfiniteTimeSpan);
        }
    }

    private void Raise() => Changed?.Invoke();
}
