using FerretSharp.Core.Connections;
using FerretSharp.Core.Schema;
using FerretSharp.UI.State;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;

namespace FerretSharp.UI.Components;

/// <summary>
/// A view that loads its data from the schema reader on the explorer session when first shown and again whenever
/// <see cref="Version"/> changes (F5); a newer load cancels an older one. Base of the detail views of a table tab
/// (<see cref="DetailViewBase{T}"/>) and of a PL/SQL tab (<see cref="PlSqlViewBase{T}"/>).
/// </summary>
public abstract class LoadingViewBase<T> : ComponentBase, IDisposable where T : class
{
    private int? _loadedVersion;
    private CancellationTokenSource? _cts;

    /// <summary>The tab's connection – cascaded by its ConnectionScopeView, never injected (that would be the WebView scope's, without schema).</summary>
    [CascadingParameter]
    public ActiveConnection Active { get; set; } = null!;

    [Inject]
    private ILoggerFactory LoggerFactory { get; set; } = null!;

    [CascadingParameter]
    public ShellState Shell { get; set; } = null!;

    /// <summary>Incremented by the tab to reload.</summary>
    [Parameter]
    public int Version { get; set; }

    protected T? Data { get; private set; }

    protected DatabaseException? Error { get; private set; }

    protected bool Loading { get; private set; }

    protected abstract Task<T> LoadAsync(SchemaCache schema, CancellationToken cancellationToken);

    /// <summary>After a load succeeded (on the UI thread, before rendering).</summary>
    protected virtual void OnLoaded(T data)
    {
    }

    protected override async Task OnParametersSetAsync()
    {
        if (_loadedVersion == Version || Active.Schema is not { } schema)
        {
            return;
        }

        _loadedVersion = Version;
        _cts?.Cancel();
        _cts?.Dispose();
        var cts = _cts = new CancellationTokenSource();
        Loading = true;
        Error = null;
        var result = await Shell.RunDbAsync(LoggerFactory.CreateLogger(GetType()), Active, () => LoadAsync(schema, cts.Token), cts.Token);
        if (cts.IsCancellationRequested)
        {
            return; // a newer load took over
        }

        (Data, Error) = result.Succeeded ? (result.Value, null) : (Data, result.Error);
        Loading = false;
        if (result.Succeeded && result.Value is { } data)
        {
            OnLoaded(data);
        }
    }

    public virtual void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        GC.SuppressFinalize(this);
    }
}

/// <summary>A detail view of a table tab (header, constraints, indexes …).</summary>
public abstract class DetailViewBase<T> : LoadingViewBase<T> where T : class
{
    [Parameter, EditorRequired]
    public TableDetails Details { get; set; } = null!;

    protected TableSummary Table => Details.Table;
}

/// <summary>A view of a PL/SQL tab (WP-28): header, source, parameters, dependencies.</summary>
public abstract class PlSqlViewBase<T> : LoadingViewBase<T> where T : class
{
    [Parameter, EditorRequired]
    public PlSqlObjectSummary Unit { get; set; } = null!;
}
