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

    /// <summary>The solutions containing the linked project (several: the user chooses); empty without one or without a link.</summary>
    public IReadOnlyList<string> Solutions
    {
        get
        {
            if (_models.State.Link is not { } link)
            {
                return [];
            }

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
    public Task<CodeUsageResult?> EnsureAsync(CancellationToken cancellationToken) => GetAsync(force: false).WaitAsync(cancellationToken);

    /// <summary>Reads the solution again (new projects, a moved solution), the cache still serving unchanged assemblies.</summary>
    public Task<CodeUsageResult?> RefreshAsync(CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _solutions = null;
        }

        return GetAsync(force: true).WaitAsync(cancellationToken);
    }

    /// <summary>Reads another solution from now on and remembers it for the connection.</summary>
    public async Task SelectSolutionAsync(string solution, CancellationToken cancellationToken)
    {
        if (Profile is { } profile)
        {
            await _connections.SetCodeSolutionAsync(profile.Id, solution, cancellationToken);
        }

        await GetAsync(force: true).WaitAsync(cancellationToken);
    }

    private Task<CodeUsageResult?> GetAsync(bool force)
    {
        lock (_lock)
        {
            if (_building is { IsCompleted: false } running)
            {
                return running;
            }

            if (_models.State.Link is not { } link || _models.Mapping?.Model is not { } model)
            {
                return Task.FromResult<CodeUsageResult?>(null);
            }

            if (_lifetime.IsCancellationRequested)
            {
                return Task.FromResult<CodeUsageResult?>(null);
            }

            if (!force && State.Result is { } result && IsCurrent(result, model, link))
            {
                return Task.FromResult<CodeUsageResult?>(result);
            }

            return _building = BuildAsync(link, model, _lifetime.Token);
        }
    }

    private async Task<CodeUsageResult?> BuildAsync(ClrProjectLink link, ModelExport model, CancellationToken cancellationToken)
    {
        await Task.Yield(); // leave the lock (and the caller's thread) before reading anything
        Set(State with { Phase = CodeUsagePhase.Building, Error = null });
        try
        {
            var solution = Solution;
            var result = await Task.Run(() => _builder.BuildAsync(link, solution, model, cancellationToken), cancellationToken);
            Set(new CodeUsageState(CodeUsagePhase.Ready, result, _time.GetLocalNow(), null));
            return result;
        }
        catch (OperationCanceledException)
        {
            return null; // the connection closed
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            Set(State with { Phase = CodeUsagePhase.Failed, Error = ex.Message });
            return null;
        }
    }

    /// <summary>Built from this model and the solution as chosen now, and no assembly of it changed (or appeared) since.</summary>
    private bool IsCurrent(CodeUsageResult result, ModelExport model, ClrProjectLink link)
    {
        try
        {
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

        Set(model is null
            ? CodeUsageState.None
            : State with { Phase = State.Phase is CodeUsagePhase.Building ? CodeUsagePhase.Building : State.Result is null ? CodeUsagePhase.NotBuilt : CodeUsagePhase.Ready });
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
