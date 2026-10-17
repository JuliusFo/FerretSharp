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
/// time. After a build (ADR 0016) a new host starts in the background (<see cref="LinqConsoleRestart"/>); the old one
/// answers until the new one is ready. Every start runs off the caller's thread and can be cancelled.
/// </summary>
public sealed class LinqConsoleService : IAsyncDisposable, IDisposable, LinqConsoleRestart.ITarget
{
    /// <summary>The <see cref="LinqConsoleState.Step"/> while a new build loads in the background: “Build changed – &lt;step&gt;”.</summary>
    public static string ReloadStep(string step) => TextFormat.Format(ClrModelText.BuildChangedStep, step);

    private readonly IModelHostRunner _runner;
    private readonly ClrModelManager _models;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly LinqConsoleRestart _restart;
    private ILinqConsole? _console;
    private ClrProjectLink? _consoleLink;

    /// <summary>The files of the build output the running host was started from.</summary>
    private IReadOnlyDictionary<string, FileStamp>? _consoleFiles;

    private readonly Lock _lock = new();
    private ClrProjectLink? _seenLink;
    private CancellationTokenSource? _starting;

    /// <summary>Set (under the lock) when the connection closes: no restart starts or swaps hosts after that.</summary>
    private volatile bool _disposed;

    private readonly Lock _stateLock = new();
    private LinqConsoleState _state;

    public LinqConsoleService(IModelHostRunner runner, ClrModelManager models)
    {
        _runner = runner;
        _models = models;
        _restart = new LinqConsoleRestart(this);
        _seenLink = models.State.Link;
        _models.Changed += OnModelsChanged;
        _models.BuildOutputChanged += OnBuildOutputChanged;
        _state = models.State.Link is null ? LinqConsoleState.NotLinked : new LinqConsoleState(LinqConsolePhase.Stopped);
    }

    /// <summary>May fire on a background thread.</summary>
    public event Action? Changed;

    /// <summary>Set as a whole from any thread (R4b: under a lock); a restart superseded meanwhile does not overwrite it.</summary>
    public LinqConsoleState State
    {
        get
        {
            lock (_stateLock)
            {
                return _state;
            }
        }
    }

    /// <summary>The background restart (tests wait for it).</summary>
    internal LinqConsoleRestart Restart => _restart;

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

            _restart.Cancel(dropWaiting: true);
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
                _restart.StartIfIdle();
            }

            return running;
        }

        await StopAsync();
        using var start = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_lock)
        {
            _starting = start;
        }

        _restart.Cancel(dropWaiting: false); // this start reads the newest build anyway
        Set(new LinqConsoleState(LinqConsolePhase.Starting, ClrModelText.StepFindBuild));
        try
        {
            var (console, files) = await Task.Run(
                () => StartHostAsync(link, step => Set(new LinqConsoleState(LinqConsolePhase.Starting, step)), start.Token), start.Token);
            if (_disposed)
            {
                // Ready just as the connection closed (R4a): kept, the host ran on until FerretSharp ended, holding its copy
                // of the build output.
                await console.DisposeAsync();
                throw new OperationCanceledException();
            }

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

    internal static bool IsStartFailure(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or System.ComponentModel.Win32Exception;

    internal static ClrModelException StartError(Exception ex) =>
        ex as ClrModelException ?? new ClrModelException(ClrModelErrorKind.HostFailed, TextFormat.Format(ClrModelText.ConsoleNotStarted, ex.Message));

    /// <summary>The build output differs from the one the host was started from (and from one that failed to load).</summary>
    private bool IsNewerBuild(ILinqConsole console)
    {
        try
        {
            var files = BuildFiles.List(Path.GetDirectoryName(console.Output.Assembly)!);
            return _consoleFiles is { } started && !BuildFiles.Same(started, files) && !_restart.FailedFor(files);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false; // being written right now: the watcher reports the end of the build
        }
    }

    /// <summary>
    /// A build changed the output: load it in the background if a console runs (otherwise the next start takes it) – after
    /// the model has been reloaded, which the model manager starts at the same time.
    /// </summary>
    private void OnBuildOutputChanged()
    {
        if (_console is not { IsAlive: true })
        {
            return;
        }

        _restart.BuildChanged(_models.State);
        Set(new LinqConsoleState(LinqConsolePhase.Ready, ReloadStep(ClrModelText.ReloadWaitingForModel)));
        OnModelsChanged();
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
        bool sameLink;
        lock (_lock)
        {
            sameLink = link == _seenLink;
        }

        _restart.ModelChanged(state, sameLink);

        lock (_lock)
        {
            if (link == _seenLink)
            {
                return;
            }

            _seenLink = link;
            _starting?.Cancel();
        }

        _restart.Cancel(dropWaiting: true);
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
        lock (_stateLock)
        {
            _state = state;
        }

        Changed?.Invoke();
    }

    // ---- what a background restart needs (LinqConsoleRestart) ----

    ClrProjectLink? LinqConsoleRestart.ITarget.RunningLink => _console is { IsAlive: true } ? _consoleLink : null;

    ClrProjectLink? LinqConsoleRestart.ITarget.ModelLink => _models.State.Link;

    Task<(ILinqConsole Console, IReadOnlyDictionary<string, FileStamp> Files)> LinqConsoleRestart.ITarget.StartHostAsync(
        ClrProjectLink link, Action<string> report, CancellationToken cancellationToken) => StartHostAsync(link, report, cancellationToken);

    async Task<(bool Taken, ILinqConsole? Replaced)> LinqConsoleRestart.ITarget.SwapAsync(
        ClrProjectLink link, ILinqConsole started, IReadOnlyDictionary<string, FileStamp> files, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_disposed || _models.State.Link != link || (_consoleLink is not null && _consoleLink != link))
            {
                // The connection closed meanwhile (the new host would outlive it), or the link changed: the new host
                // belongs to the old one too.
                return (false, null);
            }

            var replaced = _console;
            (_console, _consoleLink, _consoleFiles) = (started, link, files);
            return (true, replaced);
        }
        finally
        {
            _gate.Release();
        }
    }

    IReadOnlyDictionary<string, FileStamp>? LinqConsoleRestart.ITarget.ListFiles(ClrProjectLink link)
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

    void LinqConsoleRestart.ITarget.SetState(LinqConsoleState state)
    {
        lock (_stateLock)
        {
            _state = state;
        }
    }

    void LinqConsoleRestart.ITarget.RaiseChanged() => Changed?.Invoke();

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
            _starting?.Cancel();
        }

        return _restart.End();
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
            // the restart's swap checks _disposed under the gate, and it disposes the host it started itself
        }
    }
}
