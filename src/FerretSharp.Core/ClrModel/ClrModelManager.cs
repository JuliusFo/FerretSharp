using FerretSharp.Core.Connections;

namespace FerretSharp.Core.ClrModel;

public enum ClrModelPhase
{
    /// <summary>No project linked (or not connected).</summary>
    None,
    Loading,
    Loaded,
    Failed,
}

/// <param name="Output">The build output read (also on a failure after it was found).</param>
/// <param name="Mapping">The model laid over the schema; kept while a reload runs.</param>
public sealed record ClrModelState(
    ClrModelPhase Phase,
    ClrProjectLink? Link,
    BuildOutput? Output,
    ClrModelMapping? Mapping,
    ModelHostError? Error,
    DateTimeOffset? LoadedAt)
{
    public static readonly ClrModelState None = new(ClrModelPhase.None, null, null, null, null, null);
}

/// <summary>
/// The C# model of the active connection's linked project (WP-11): loaded in the background after connecting, again on
/// request ("Neu laden", after "Neu bauen") and when the link changes. Nothing of it touches the database except reading
/// column lists through the schema cache.
/// </summary>
public sealed class ClrModelManager : IDisposable
{
    private readonly IModelHostRunner _runner;
    private readonly ActiveConnection _active;
    private readonly ConnectionManager _connections;
    private readonly Lock _lock = new();
    private CancellationTokenSource? _loading;
    private Guid? _profileId;

    public ClrModelManager(IModelHostRunner runner, ActiveConnection active, ConnectionManager connections)
    {
        _runner = runner;
        _active = active;
        _connections = connections;
        _active.Changed += OnConnectionChanged;
        _connections.Changed += OnConnectionChanged;
    }

    /// <summary>May fire on a background thread.</summary>
    public event Action? Changed;

    public ClrModelState State { get; private set; } = ClrModelState.None;

    public bool IsBuilding { get; private set; }

    /// <summary>Output of the last "Neu bauen".</summary>
    public DotNetRun? LastBuild { get; private set; }

    /// <summary>The mapping if a model is loaded (also while it reloads).</summary>
    public ClrModelMapping? Mapping => State.Mapping;

    /// <summary>The link of the active connection as currently saved (the dialog may have changed it).</summary>
    private ClrProjectLink? CurrentLink =>
        _active.Profile is { } profile ? (_connections.Profiles.FirstOrDefault(p => p.Id == profile.Id) ?? profile).ClrProject : null;

    /// <summary>Reads the model again; a load still running is cancelled.</summary>
    public async Task LoadAsync()
    {
        CancellationTokenSource cts;
        lock (_lock)
        {
            _loading?.Cancel();
            _loading = cts = new CancellationTokenSource();
        }

        var link = CurrentLink;
        var schema = _active.Schema;
        if (link is null || schema is null || !_active.IsConnected)
        {
            Set(ClrModelState.None, cts);
            return;
        }

        var previous = State.Link == link ? State.Mapping : null;
        Set(new ClrModelState(ClrModelPhase.Loading, link, State.Output, previous, null, State.LoadedAt), cts);
        BuildOutput? output = null;
        try
        {
            output = await Task.Run(() => BuildOutputLocator.Find(link), cts.Token);
            var result = await Task.Run(() => _runner.ReadModelAsync(link, output, cts.Token), cts.Token);
            if (result.Model is not { } model)
            {
                Set(new ClrModelState(ClrModelPhase.Failed, link, output, previous,
                    result.Error ?? new ModelHostError(ClrModelErrorKind.HostFailed, "Kein Modell geliefert."), State.LoadedAt), cts);
                return;
            }

            var mapping = await ClrModelMapping.BuildAsync(model, schema, cts.Token);
            Set(new ClrModelState(ClrModelPhase.Loaded, link, output, mapping, null, DateTimeOffset.Now), cts);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // superseded by a newer load or the connection ended
        }
        catch (ClrModelException ex)
        {
            Set(new ClrModelState(ClrModelPhase.Failed, link, output, previous, ex.ToError(), State.LoadedAt), cts);
        }
        catch (DatabaseException ex)
        {
            Set(new ClrModelState(ClrModelPhase.Failed, link, output, previous,
                new ModelHostError(ClrModelErrorKind.HostFailed, $"Spalten ließen sich nicht lesen: {ex.Display}"), State.LoadedAt), cts);
        }
    }

    /// <summary><c>dotnet build</c> of the linked project, then reads the model again if the build succeeded.</summary>
    public async Task<DotNetRun?> BuildAsync()
    {
        if (CurrentLink is not { } link || IsBuilding)
        {
            return null;
        }

        IsBuilding = true;
        Changed?.Invoke();
        try
        {
            LastBuild = await Task.Run(() => _runner.BuildAsync(link, CancellationToken.None));
        }
        catch (ClrModelException ex)
        {
            LastBuild = new DotNetRun(-1, ex.Message);
        }
        finally
        {
            IsBuilding = false;
            Changed?.Invoke();
        }

        if (LastBuild.Succeeded)
        {
            await LoadAsync();
        }

        return LastBuild;
    }

    public void Dispose()
    {
        _active.Changed -= OnConnectionChanged;
        _connections.Changed -= OnConnectionChanged;
        _loading?.Cancel();
    }

    /// <summary>Connected (to another profile), disconnected, or the link of the active profile changed.</summary>
    private void OnConnectionChanged()
    {
        var connected = _active.IsConnected ? _active.Profile?.Id : null;
        var link = connected is null ? null : CurrentLink;
        if (connected != _profileId || link != State.Link)
        {
            _profileId = connected;
            _ = LoadAsync();
        }
    }

    private void Set(ClrModelState state, CancellationTokenSource owner)
    {
        lock (_lock)
        {
            if (!ReferenceEquals(_loading, owner))
            {
                return; // a newer load owns the state
            }

            State = state;
        }

        Changed?.Invoke();
    }
}
