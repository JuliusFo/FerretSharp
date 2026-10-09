using FerretSharp.Core.Resources;

namespace FerretSharp.Core.ClrModel;

public enum LinqConsolePhase
{
    /// <summary>The active connection has no C# project.</summary>
    NotLinked,

    /// <summary>Linked, the host is not running (started on first use).</summary>
    Stopped,

    Starting,
    Ready,

    /// <summary>The last start failed (<see cref="LinqConsoleState.Error"/>); the next run tries again.</summary>
    Failed,
}

/// <param name="Step">
/// While starting: what the host is doing ("Building the model (OnModelCreating)"). When ready: a new build is being loaded
/// in the background ("Build changed – …"); the console keeps answering with the previous one meanwhile.
/// </param>
/// <param name="Error">
/// When failed: why the start failed. When ready: the new build could not be loaded, the console still runs the previous one.
/// </param>
public sealed record LinqConsoleState(LinqConsolePhase Phase, string? Step = null, ModelHostError? Error = null)
{
    public static readonly LinqConsoleState NotLinked = new(LinqConsolePhase.NotLinked);

    /// <summary>Ready, and a new build is loading in the background.</summary>
    public bool IsReloading => Phase == LinqConsolePhase.Ready && Step is not null;
}

/// <summary>
/// The LINQ console of the active connection's linked project (ADR 0011): one host process, started on first use (or
/// when a LINQ tab opens), stopped when the link changes or the connection ends. All LINQ tabs share it; runs go one at a
/// time. After a build (ADR 0016) a new host starts in the background; the old one answers until the new one is ready.
/// Every start runs off the caller's thread and can be cancelled.
/// </summary>
public sealed class LinqConsoleService : IAsyncDisposable, IDisposable
{
    /// <summary>The <see cref="LinqConsoleState.Step"/> while a new build loads in the background: “Build changed – &lt;step&gt;”.</summary>
    public static string ReloadStep(string step) => TextFormat.Format(ClrModelText.BuildChangedStep, step);

    private readonly IModelHostRunner _runner;
    private readonly ClrModelManager _models;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ILinqConsole? _console;
    private ClrProjectLink? _consoleLink;

    /// <summary>The files of the build output the running host was started from.</summary>
    private IReadOnlyDictionary<string, FileStamp>? _consoleFiles;

    private readonly Lock _lock = new();
    private ClrProjectLink? _seenLink;
    private CancellationTokenSource? _starting;
    private CancellationTokenSource? _restart;
    private Task _restartTask = Task.CompletedTask;

    /// <summary>Set (under the lock) when the connection closes: no restart starts or swaps hosts after that.</summary>
    private volatile bool _disposed;

    /// <summary>The build output a background restart failed for (not tried again until the output changes).</summary>
    private IReadOnlyDictionary<string, FileStamp>? _failedFiles;

    /// <summary>
    /// A build changed the output while the model was (about to be) reloaded: the console restarts once the model state
    /// has moved on from this one, so two hosts never build the model at the same time.
    /// </summary>
    private ClrModelState? _restartAfter;

    public LinqConsoleService(IModelHostRunner runner, ClrModelManager models)
    {
        _runner = runner;
        _models = models;
        _seenLink = models.State.Link;
        _models.Changed += OnModelsChanged;
        _models.BuildOutputChanged += OnBuildOutputChanged;
        State = models.State.Link is null ? LinqConsoleState.NotLinked : new LinqConsoleState(LinqConsolePhase.Stopped);
    }

    /// <summary>May fire on a background thread.</summary>
    public event Action? Changed;

    public LinqConsoleState State { get; private set; }

    /// <summary>Starts the host if it is not running, so the first run does not wait for the model (fire and forget).</summary>
    public async Task WarmUpAsync()
    {
        try
        {
            await _gate.WaitAsync();
            try
            {
                await EnsureAsync(CancellationToken.None);
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (Exception ex) when (ex is ClrModelException or OperationCanceledException)
        {
            // shown through State; the next run tries again
        }
    }

    /// <summary>
    /// Ends the host process if it is idle (WP-24: its connection has been in the background for a while; the next run
    /// starts it again, from the model cache). A host that is busy stays. True if a host was stopped. A restart for a new
    /// build is dropped too: the next start reads the newest build anyway.
    /// </summary>
    public async Task<bool> StopIdleAsync()
    {
        if (!await _gate.WaitAsync(0))
        {
            return false;
        }

        try
        {
            if (_console is null)
            {
                return false;
            }

            lock (_lock)
            {
                _restart?.Cancel();
                _restartAfter = null;
            }

            await StopAsync();
            Set(_models.State.Link is null ? LinqConsoleState.NotLinked : new LinqConsoleState(LinqConsolePhase.Stopped));
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Cancels a start in progress ("Abbrechen" while the console loads the project); the next run starts again.</summary>
    public void CancelStart()
    {
        lock (_lock)
        {
            _starting?.Cancel();
        }
    }

    /// <exception cref="ClrModelException">No project linked, the host could not start or did not answer.</exception>
    public async Task<LinqRunResult> RunAsync(string code, string variables, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var console = await EnsureAsync(cancellationToken);
            try
            {
                return await console.RunAsync(code, variables, cancellationToken);
            }
            finally
            {
                if (!console.IsAlive)
                {
                    await StopAsync();
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Completion items (WP-19) – only from a console that is ready and idle: suggestions never wait for a start or a run,
    /// they are simply empty then. Errors give an empty list too (a dead host is stopped, the next run restarts it).
    /// </summary>
    public async Task<IReadOnlyList<LinqCompletionItem>> CompleteAsync(string code, string variables, string section, int offset)
    {
        if (!await _gate.WaitAsync(0))
        {
            return [];
        }

        try
        {
            if (_console is not { IsAlive: true } console || _consoleLink != _models.State.Link)
            {
                return [];
            }

            try
            {
                return await console.CompleteAsync(code, variables, section, offset, CancellationToken.None);
            }
            catch (ClrModelException)
            {
                if (!console.IsAlive)
                {
                    await StopAsync();
                    Set(new LinqConsoleState(LinqConsolePhase.Stopped));
                }

                return [];
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The running console, or a newly started one (under the gate).</summary>
    private async Task<ILinqConsole> EnsureAsync(CancellationToken cancellationToken)
    {
        var link = _models.State.Link ?? throw new ClrModelException(ClrModelErrorKind.ProjectNotFound,
            ClrModelText.NoProjectLinked);
        if (_console is { IsAlive: true } running && _consoleLink == link)
        {
            if (IsNewerBuild(running))
            {
                // The watcher missed the build (or could not watch): load it in the background, this run takes the old one.
                StartRestart(supersede: false);
            }

            return running;
        }

        await StopAsync();
        using var start = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_lock)
        {
            _restart?.Cancel(); // this start reads the newest build anyway
            _starting = start;
        }

        Set(new LinqConsoleState(LinqConsolePhase.Starting, ClrModelText.StepFindBuild));
        try
        {
            var (console, files) = await Task.Run(
                () => StartHostAsync(link, step => Set(new LinqConsoleState(LinqConsolePhase.Starting, step)), start.Token), start.Token);
            (_console, _consoleLink, _consoleFiles) = (console, link, files);
            Set(new LinqConsoleState(LinqConsolePhase.Ready));
            return console;
        }
        catch (ClrModelException ex)
        {
            Set(new LinqConsoleState(LinqConsolePhase.Failed, Error: ex.ToError()));
            throw;
        }
        catch (OperationCanceledException)
        {
            Set(new LinqConsoleState(LinqConsolePhase.Stopped));
            throw;
        }
        catch (Exception ex) when (IsStartFailure(ex))
        {
            // A deps.json being rewritten by a build, dotnet not startable …: a failure like the others, not "Starting" forever.
            var error = StartError(ex);
            Set(new LinqConsoleState(LinqConsolePhase.Failed, Error: error.ToError()));
            throw error;
        }
        finally
        {
            lock (_lock)
            {
                _starting = null;
            }
        }
    }

    /// <summary>Finds the build and starts a host on it (on a pool thread: nothing of it may run on the UI thread).</summary>
    private async Task<(ILinqConsole Console, IReadOnlyDictionary<string, FileStamp> Files)> StartHostAsync(
        ClrProjectLink link, Action<string> report, CancellationToken cancellationToken)
    {
        var output = BuildOutputLocator.Find(link);
        var files = BuildFiles.List(Path.GetDirectoryName(output.Assembly)!);
        var console = await _runner.StartConsoleAsync(link, output, cancellationToken, new SyncProgress<string>(report));
        return (console, files);
    }

    private static bool IsStartFailure(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or System.ComponentModel.Win32Exception;

    private static ClrModelException StartError(Exception ex) =>
        ex as ClrModelException ?? new ClrModelException(ClrModelErrorKind.HostFailed, TextFormat.Format(ClrModelText.ConsoleNotStarted, ex.Message));

    /// <summary>The build output differs from the one the host was started from (and from one that failed to load).</summary>
    private bool IsNewerBuild(ILinqConsole console)
    {
        try
        {
            var files = BuildFiles.List(Path.GetDirectoryName(console.Output.Assembly)!);
            return _consoleFiles is { } started && !BuildFiles.Same(started, files)
                   && (_failedFiles is not { } failed || !BuildFiles.Same(failed, files));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false; // being written right now: the watcher reports the end of the build
        }
    }

    /// <summary>
    /// A build changed the output: load it in the background if a console runs (otherwise the next start takes it) – after
    /// the model has been reloaded, which the model manager starts at the same time. Building a large model takes seconds
    /// of CPU; two hosts doing it at once made the UI stutter.
    /// </summary>
    private void OnBuildOutputChanged()
    {
        if (_console is not { IsAlive: true })
        {
            return;
        }

        lock (_lock)
        {
            _restart?.Cancel(); // a restart on the previous build is outdated
            _restartAfter = _models.State;
        }

        Set(new LinqConsoleState(LinqConsolePhase.Ready, ReloadStep(ClrModelText.ReloadWaitingForModel)));
        OnModelsChanged();
    }

    /// <param name="supersede">A newer build: cancel a restart still running. Otherwise only start one if none runs.</param>
    private void StartRestart(bool supersede)
    {
        lock (_lock)
        {
            if (_disposed || (!supersede && !_restartTask.IsCompleted))
            {
                return;
            }

            _restart?.Cancel();
            var cts = _restart = new CancellationTokenSource();
            _restartTask = Task.Run(() => RestartAsync(cts));
        }
    }

    /// <summary>
    /// Starts a host on the new build next to the running one and swaps them once it is ready; runs meanwhile go to the old
    /// one. If the new build cannot be loaded, the old host stays (with the error in the state).
    /// </summary>
    private async Task RestartAsync(CancellationTokenSource cts)
    {
        var link = _consoleLink;
        if (link is null || _console is not { IsAlive: true } || _models.State.Link != link)
        {
            return; // not running: the next start reads the new build
        }

        void Reloading(string step)
        {
            if (!cts.IsCancellationRequested)
            {
                Set(new LinqConsoleState(LinqConsolePhase.Ready, ReloadStep(step)));
            }
        }

        Reloading(ClrModelText.ReloadStarting);
        ILinqConsole? started = null;
        IReadOnlyDictionary<string, FileStamp>? files = null;
        try
        {
            (started, files) = await StartHostAsync(link, Reloading, cts.Token);
            ILinqConsole? old;
            await _gate.WaitAsync(cts.Token);
            try
            {
                if (_disposed || _models.State.Link != link || (_consoleLink is not null && _consoleLink != link))
                {
                    // The connection closed meanwhile (the new host would outlive it), or the link changed: the new host
                    // belongs to the old one too.
                    return;
                }

                old = _console;
                (_console, _consoleLink, _consoleFiles, _failedFiles) = (started, link, files, null);
                started = null;
            }
            finally
            {
                _gate.Release();
            }

            Set(new LinqConsoleState(LinqConsolePhase.Ready));
            if (old is not null)
            {
                await old.DisposeAsync();
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // superseded by a newer build, a foreground start, another link or the end
        }
        catch (Exception ex) when (ex is ClrModelException || IsStartFailure(ex))
        {
            var error = StartError(ex);
            _failedFiles = files ?? TryList(link);
            Set(new LinqConsoleState(LinqConsolePhase.Ready, Error: new ModelHostError(error.Kind,
                TextFormat.Format(ClrModelText.NewBuildNotLoaded, error.Message), error.Detail)));
        }
        finally
        {
            if (started is not null)
            {
                await started.DisposeAsync();
            }
        }
    }

    private static IReadOnlyDictionary<string, FileStamp>? TryList(ClrProjectLink link)
    {
        try
        {
            return BuildFiles.List(Path.GetDirectoryName(BuildOutputLocator.Find(link).Assembly)!);
        }
        catch (Exception ex) when (ex is ClrModelException || IsStartFailure(ex))
        {
            return null;
        }
    }

    private async Task StopAsync()
    {
        var console = _console;
        _console = null;
        _consoleLink = null;
        _consoleFiles = null;
        if (console is not null)
        {
            await console.DisposeAsync();
        }
    }

    /// <summary>
    /// The model manager reports every loading step. A load after a build that has ended starts the waiting restart; a
    /// changed link (other connection, other project, unlinked) stops the running host, which belongs to the old one.
    /// </summary>
    private void OnModelsChanged()
    {
        var state = _models.State;
        var link = state.Link;
        var restart = false;
        lock (_lock)
        {
            if (_restartAfter is { } after && !ReferenceEquals(after, state) && state.Phase != ClrModelPhase.Loading)
            {
                _restartAfter = null;
                restart = link == _seenLink;
            }
        }

        if (restart)
        {
            StartRestart(supersede: true);
        }

        lock (_lock)
        {
            if (link == _seenLink)
            {
                return;
            }

            _restartAfter = null;
            _seenLink = link;
            _restart?.Cancel();
            _starting?.Cancel();
        }

        _ = Task.Run(async () =>
        {
            await _gate.WaitAsync();
            try
            {
                var current = _models.State.Link;
                if (_consoleLink != current)
                {
                    await StopAsync();
                }

                Set(current is null ? LinqConsoleState.NotLinked
                    : _console is not null ? new LinqConsoleState(LinqConsolePhase.Ready)
                    : new LinqConsoleState(LinqConsolePhase.Stopped));
            }
            finally
            {
                _gate.Release();
            }
        });
    }

    private void Set(LinqConsoleState state)
    {
        State = state;
        Changed?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        await EndRestartAsync(Unsubscribe());
        await StopAsync();
    }

    /// <summary>For a synchronous shutdown (the host's service provider): stops the host process, waiting at most 3 s.</summary>
    public void Dispose()
    {
        var restart = Unsubscribe();
        Task.Run(async () =>
        {
            await EndRestartAsync(restart);
            await StopAsync();
        }).Wait(TimeSpan.FromSeconds(3));
    }

    /// <summary>Stops listening, cancels a start and a restart in the background; returns the restart to wait for.</summary>
    private Task Unsubscribe()
    {
        _models.Changed -= OnModelsChanged;
        _models.BuildOutputChanged -= OnBuildOutputChanged;
        lock (_lock)
        {
            _disposed = true;
            _restart?.Cancel();
            _starting?.Cancel();
            return _restartTask;
        }
    }

    /// <summary>
    /// A restart that is about to swap hosts must not do so after the console was stopped: the new host would run on
    /// unnoticed, holding its copy of the build output. Cancelled, it ends quickly; it is not waited for forever.
    /// </summary>
    private static async Task EndRestartAsync(Task restart)
    {
        try
        {
            await restart.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (TimeoutException)
        {
            // the restart checks _disposed before swapping and disposes its host itself
        }
    }
}
