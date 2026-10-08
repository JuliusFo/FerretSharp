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

/// <param name="Step">While starting: what the host is doing ("Baue das Modell (OnModelCreating)").</param>
public sealed record LinqConsoleState(LinqConsolePhase Phase, string? Step = null, ModelHostError? Error = null)
{
    public static readonly LinqConsoleState NotLinked = new(LinqConsolePhase.NotLinked);
}

/// <summary>
/// The LINQ console of the active connection's linked project (ADR 0011): one host process, started on first use (or
/// when a LINQ tab opens), started again after a new build, stopped when the link changes or the connection ends. All
/// LINQ tabs share it; runs go one at a time.
/// </summary>
public sealed class LinqConsoleService : IAsyncDisposable, IDisposable
{
    private readonly IModelHostRunner _runner;
    private readonly ClrModelManager _models;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ILinqConsole? _console;
    private ClrProjectLink? _consoleLink;
    private DateTime _consoleBuild;
    private readonly Lock _linkLock = new();
    private ClrProjectLink? _seenLink;

    public LinqConsoleService(IModelHostRunner runner, ClrModelManager models)
    {
        _runner = runner;
        _models = models;
        _seenLink = models.State.Link;
        _models.Changed += OnModelsChanged;
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
        catch (ClrModelException)
        {
            // shown through State; the next run tries again
        }
    }

    /// <summary>
    /// Ends the host process if it is idle (WP-24: its connection has been in the background for a while; the next run
    /// starts it again, from the model cache). A host that is busy stays. True if a host was stopped.
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

            await StopAsync();
            Set(_models.State.Link is null ? LinqConsoleState.NotLinked : new LinqConsoleState(LinqConsolePhase.Stopped));
            return true;
        }
        finally
        {
            _gate.Release();
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
    /// they are simply empty then. After a new build the host restarts in the background (as the next run would).
    /// Errors give an empty list too (a dead host is stopped, the next run restarts it).
    /// </summary>
    public async Task<IReadOnlyList<LinqCompletionItem>> CompleteAsync(string code, string variables, string section, int offset)
    {
        if (!await _gate.WaitAsync(0))
        {
            return [];
        }

        var restart = false;
        try
        {
            if (_console is not { IsAlive: true } console || _consoleLink != _models.State.Link)
            {
                return [];
            }

            if (IsNewerBuild(console.Output))
            {
                // The user is writing in the console: load the new build now rather than at the next run.
                restart = true;
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
            if (restart)
            {
                _ = WarmUpAsync();
            }
        }
    }

    private async Task<ILinqConsole> EnsureAsync(CancellationToken cancellationToken)
    {
        var link = _models.State.Link ?? throw new ClrModelException(ClrModelErrorKind.ProjectNotFound,
            "Für diese Verbindung ist kein C#-Projekt verknüpft (Verbindung bearbeiten → C#-Modell).");
        if (_console is { IsAlive: true } running && _consoleLink == link && !IsNewerBuild(running.Output))
        {
            return running;
        }

        await StopAsync();
        Set(new LinqConsoleState(LinqConsolePhase.Starting, "Suche den Build"));
        try
        {
            var output = await Task.Run(() => BuildOutputLocator.Find(link), cancellationToken);
            var build = File.GetLastWriteTimeUtc(output.Assembly);
            var console = await _runner.StartConsoleAsync(link, output, cancellationToken,
                new SyncProgress<string>(step => Set(new LinqConsoleState(LinqConsolePhase.Starting, step))));
            (_console, _consoleLink, _consoleBuild) = (console, link, build);
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or System.ComponentModel.Win32Exception)
        {
            // A deps.json being rewritten by a build, dotnet not startable …: a failure like the others, not "Starting" forever.
            var error = new ClrModelException(ClrModelErrorKind.HostFailed, $"Die LINQ-Konsole ließ sich nicht starten: {ex.Message}");
            Set(new LinqConsoleState(LinqConsolePhase.Failed, Error: error.ToError()));
            throw error;
        }
    }

    /// <summary>The project was built again since the host loaded it: its assemblies are stale.</summary>
    private bool IsNewerBuild(BuildOutput output)
    {
        try
        {
            return File.GetLastWriteTimeUtc(output.Assembly) > _consoleBuild;
        }
        catch (IOException)
        {
            return true;
        }
    }

    private async Task StopAsync()
    {
        var console = _console;
        _console = null;
        _consoleLink = null;
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
        var link = _models.State.Link;
        lock (_linkLock)
        {
            if (link == _seenLink)
            {
                return;
            }

            _seenLink = link;
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
        _models.Changed -= OnModelsChanged;
        await StopAsync();
    }

    /// <summary>For a synchronous shutdown (the host's service provider): stops the host process, waiting at most 3 s.</summary>
    public void Dispose()
    {
        _models.Changed -= OnModelsChanged;
        Task.Run(StopAsync).Wait(TimeSpan.FromSeconds(3));
    }
}
