using FerretSharp.Core.Connections;

namespace FerretSharp.Core.ClrModel;

public enum CodeUsagePhase
{
    /// <summary>No model loaded (no project linked, not connected, or still loading the first time).</summary>
    None,

    /// <summary>Not read yet: the index is built when a view needs it, not on connecting.</summary>
    NotBuilt,
    Building,
    Ready,
    Failed,
}

/// <param name="Result">The index; kept while a rebuild runs.</param>
/// <param name="BuiltAt">When <paramref name="Result"/> was built.</param>
public sealed record CodeUsageState(CodeUsagePhase Phase, CodeUsageResult? Result, DateTimeOffset? BuiltAt, string? Error)
{
    public static readonly CodeUsageState None = new(CodeUsagePhase.None, null, null, null);
}

/// <summary>
/// The code usage index of the active connection's linked solution (WP-40, ADR 0030), one per connection scope. Built on
/// demand – the first time a view needs it – and again when the model or an assembly of the solution changed since; a
/// rebuild reads only the changed assemblies (<see cref="CodeUsageBuilder"/> caches by MVID).
/// </summary>
public sealed class CodeUsageService : IDisposable
{
    private readonly ClrModelManager _models;
    private readonly ActiveConnection _active;
    private readonly ConnectionManager _connections;
    private readonly CodeUsageBuilder _builder;
    private readonly TimeProvider _time;
    private readonly Lock _lock = new();

    /// <summary>A build is shared by every view asking for it: it runs for the service's lifetime, not the first caller's.</summary>
    private readonly CancellationTokenSource _lifetime = new();
    private Task<CodeUsageResult?>? _building;

    /// <summary>A build asked for while another ran (another solution, "read again"): it follows the running one.</summary>
    private Task<CodeUsageResult?>? _rerun;
    private (string ProjectFile, IReadOnlyList<string> Solutions)? _solutions;
    private ModelExport? _seenModel;

    public CodeUsageService(ClrModelManager models, ActiveConnection active, ConnectionManager connections, CodeUsageBuilder builder, TimeProvider? time = null)
    {
        _models = models;
        _active = active;
        _connections = connections;
        _builder = builder;
        _time = time ?? TimeProvider.System;
        _models.Changed += OnModelChanged;
        OnModelChanged();
    }

    /// <summary>May fire on a background thread.</summary>
    public event Action? Changed;

    public CodeUsageState State { get; private set; } = CodeUsageState.None;

    /// <summary>The active connection's profile as currently saved.</summary>
    private ConnectionProfile? Profile =>
        _active.Profile is { } profile ? _connections.Profiles.FirstOrDefault(p => p.Id == profile.Id) ?? profile : null;

    /// <summary>
    /// The solutions containing the linked project (several: the user chooses), as found by the last build or check; empty
    /// before. Reads nothing: views render it (review of WP-40, no file system on the render thread).
    /// </summary>
    public IReadOnlyList<string> Solutions
    {
        get
        {
            lock (_lock)
            {
                return _solutions is { } known && known.ProjectFile == _models.State.Link?.ProjectFile ? known.Solutions : [];
            }
        }
    }

    /// <summary>The solution read: the one chosen for the connection if it still contains the project, else the first found.</summary>
    public string? Solution
    {
        get
        {
            var solutions = Solutions;
            return Profile?.CodeSolution is { } chosen && solutions.Contains(chosen, StringComparer.OrdinalIgnoreCase)
                ? chosen
                : solutions.FirstOrDefault();
        }
    }

    /// <summary>
    /// The index for the current model and builds; builds it if missing or outdated. Null without a model. Cancelling stops
    /// the wait, not a build other views may wait for too.
    /// </summary>
    public Task<CodeUsageResult?> EnsureAsync(CancellationToken cancellationToken) =>
        Task.Run(() => GetAsync(force: false), CancellationToken.None).WaitAsync(cancellationToken); // the check reads the file system

    /// <summary>Reads the solution again (new projects, a moved solution), the cache still serving unchanged assemblies.</summary>
    public Task<CodeUsageResult?> RefreshAsync(CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _solutions = null;
        }

        return Task.Run(() => GetAsync(force: true), CancellationToken.None).WaitAsync(cancellationToken);
    }

    /// <summary>Reads another solution from now on and remembers it for the connection.</summary>
    public async Task SelectSolutionAsync(string solution, CancellationToken cancellationToken)
    {
        if (Profile is { } profile)
        {
            await _connections.SetCodeSolutionAsync(profile.Id, solution, cancellationToken);
        }

        await Task.Run(() => GetAsync(force: true), CancellationToken.None).WaitAsync(cancellationToken);
    }

    private Task<CodeUsageResult?> GetAsync(bool force)
    {
        ClrProjectLink link;
        ModelExport model;
        CodeUsageResult? current;
        lock (_lock)
        {
            if (_building is { IsCompleted: false } running)
            {
                // A forced build reads what the running one may not have seen – another solution, a moved one (review
                // 3.44.0, A4: the view showed solution B in its list and the uses of A).
                return force ? _rerun ??= RerunAfterAsync(running) : running;
            }

            if (_lifetime.IsCancellationRequested || _models.State.Link is not { } linked || _models.Mapping?.Model is not { } loaded)
            {
                return Task.FromResult<CodeUsageResult?>(null);
            }

            (link, model, current) = (linked, loaded, State.Result);
        }

        // Outside the lock: the check reads the file system.
        if (!force && current is not null && IsCurrent(current, model, link))
        {
            return Task.FromResult<CodeUsageResult?>(current);
        }

        lock (_lock)
        {
            if (_building is { IsCompleted: false } running)
            {
                return force ? _rerun ??= RerunAfterAsync(running) : running;
            }

            return _lifetime.IsCancellationRequested ? Task.FromResult<CodeUsageResult?>(null) : _building = BuildAsync(link, model, _lifetime.Token);
        }
    }

    private async Task<CodeUsageResult?> RerunAfterAsync(Task<CodeUsageResult?> running)
    {
        await running; // never faults: BuildAsync ends in Failed or null
        lock (_lock)
        {
            _rerun = null;
        }

        return await GetAsync(force: true);
    }

    private async Task<CodeUsageResult?> BuildAsync(ClrProjectLink link, ModelExport model, CancellationToken cancellationToken)
    {
        await Task.Yield(); // leave the lock (and the caller's thread) before reading anything
        Set(State with { Phase = CodeUsagePhase.Building, Error = null });
        try
        {
            var result = await Task.Run(() =>
            {
                FindSolutions(link);
                return _builder.BuildAsync(link, Solution, model, cancellationToken);
            }, cancellationToken);

            // The model changed while it ran (a build of the project), or another solution was chosen: the result is kept
            // but out of date, so views showing it build it again (review of WP-40: it ended as Ready, and nothing rebuilt it).
            var outdated = !ReferenceEquals(_models.Mapping?.Model, model)
                           || !string.Equals(result.Solution, Solution, StringComparison.OrdinalIgnoreCase);
            Set(new CodeUsageState(outdated ? CodeUsagePhase.NotBuilt : CodeUsagePhase.Ready, result, _time.GetLocalNow(), null));
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null; // the connection closed
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Never left at Building: views would wait for ever, and the fault would reach every tab waiting for it.
            Set(State with { Phase = CodeUsagePhase.Failed, Error = ex.Message });
            return null;
        }
    }

    /// <summary>The solutions containing the linked project, read once per link (again after "Neu einlesen").</summary>
    private IReadOnlyList<string> FindSolutions(ClrProjectLink link)
    {
        lock (_lock)
        {
            if (_solutions is { } known && known.ProjectFile == link.ProjectFile)
            {
                return known.Solutions;
            }
        }

        var found = SolutionFile.Find(link.ProjectFile);
        lock (_lock)
        {
            _solutions = (link.ProjectFile, found);
        }

        return found;
    }

    /// <summary>Built from this model and the solution as chosen now, and no assembly of it changed (or appeared) since.</summary>
    private bool IsCurrent(CodeUsageResult result, ModelExport model, ClrProjectLink link)
    {
        try
        {
            FindSolutions(link);
            return ReferenceEquals(result.Index.Model, model)
                   && string.Equals(result.Solution, Solution, StringComparison.OrdinalIgnoreCase)
                   && result.Projects.All(p => p.Build switch
                   {
                       { Assembly: { } assembly, BuiltAtUtc: { } builtAt } => File.Exists(assembly) && File.GetLastWriteTimeUtc(assembly) == builtAt,
                       { } => !BuildOutputLocator.FindAssembly(p.ProjectFile, link.Configuration).IsBuilt,
                       null => true,
                   });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return false;
        }
    }

    private void OnModelChanged()
    {
        var model = _models.Mapping?.Model;
        lock (_lock)
        {
            if (ReferenceEquals(model, _seenModel))
            {
                return;
            }

            _seenModel = model;
        }

        // A new model: the index (kept meanwhile) is out of date; views showing it build it again.
        Set(model is null
            ? CodeUsageState.None
            : State with { Phase = State.Phase is CodeUsagePhase.Building ? CodeUsagePhase.Building : CodeUsagePhase.NotBuilt });
    }

    private void Set(CodeUsageState state)
    {
        State = state;
        Changed?.Invoke();
    }

    public void Dispose()
    {
        _models.Changed -= OnModelChanged;
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
