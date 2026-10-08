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
/// While starting: what the host is doing ("Baue das Modell (OnModelCreating)"). When ready: a new build is being loaded
/// in the background ("Build geändert – …"); the console keeps answering with the previous one meanwhile.
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
    /// <summary>Prefix of <see cref="LinqConsoleState.Step"/> while a new build loads in the background.</summary>
    public const string ReloadPrefix = "Build geändert – ";

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
            "Für diese Verbindung ist kein C#-Projekt verknüpft (Verbindung bearbeiten → C#-Modell).");
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

        Set(new LinqConsoleState(LinqConsolePhase.Starting, "Suche den Build"));
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
        var console = await _runner.StartConsoleAsync(link, output, cancellationToken, new Reporter(report));
        return (console, files);
    }

    private static bool IsStartFailure(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or System.ComponentModel.Win32Exception;

    private static ClrModelException StartError(Exception ex) =>
        ex as ClrModelException ?? new ClrModelException(ClrModelErrorKind.HostFailed, $"Die LINQ-Konsole ließ sich nicht starten: {ex.Message}");

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

        Set(new LinqConsoleState(LinqConsolePhase.Ready, ReloadPrefix + "wartet auf das Modell"));
        OnModelsChanged();
    }

    /// <param name="supersede">A newer build: cancel a restart still running. Otherwise only start one if none runs.</param>
    private void StartRestart(bool supersede)
    {
        lock (_lock)
        {
            if (!supersede && !_restartTask.IsCompleted)
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
                Set(new LinqConsoleState(LinqConsolePhase.Ready, ReloadPrefix + step));
            }
        }

        Reloading("lade neu");
        ILinqConsole? started = null;
        IReadOnlyDictionary<string, FileStamp>? files = null;
        try
        {
            (started, files) = await StartHostAsync(link, Reloading, cts.Token);
            ILinqConsole? old;
            await _gate.WaitAsync(cts.Token);
            try
            {
                if (_models.State.Link != link || (_consoleLink is not null && _consoleLink != link))
                {
                    return; // the link changed meanwhile: the new host belongs to the old one too
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
                $"Der neue Build ließ sich nicht laden, die Konsole nutzt weiter den vorherigen: {error.Message}", error.Detail)));
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
    /// The link changed (other connection, other project, unlinked): a running host belongs to the old one. The model
    /// manager also reports every loading step; those leave the console alone.
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
        Unsubscribe();
        await StopAsync();
    }

    /// <summary>For a synchronous shutdown (the host's service provider): stops the host process, waiting at most 3 s.</summary>
    public void Dispose()
    {
        Unsubscribe();
        Task.Run(StopAsync).Wait(TimeSpan.FromSeconds(3));
    }

    private void Unsubscribe()
    {
        _models.Changed -= OnModelsChanged;
        _models.BuildOutputChanged -= OnBuildOutputChanged;
        lock (_lock)
        {
            _restart?.Cancel();
            _starting?.Cancel();
        }
    }

    /// <summary>Calls back on whatever thread reports.</summary>
    private sealed class Reporter(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }
}
