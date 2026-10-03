using FerretSharp.Core.Connections;
using FerretSharp.Core.Schema;
using FerretSharp.UI.State;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;

namespace FerretSharp.UI.Components;

/// <summary>
/// A detail view of a tab (header, constraints, indexes …): loads its data from the schema reader on the explorer
/// session when first shown and again whenever <see cref="Version"/> changes (F5); a newer load cancels an older one.
/// </summary>
public abstract class DetailViewBase<T> : ComponentBase, IDisposable where T : class
{
    private int? _loadedVersion;
    private CancellationTokenSource? _cts;

    [Inject]
    protected ActiveConnection Active { get; set; } = null!;

    [Inject]
    private ILoggerFactory LoggerFactory { get; set; } = null!;

    [CascadingParameter]
    public ShellState Shell { get; set; } = null!;

    [Parameter, EditorRequired]
    public TableDetails Details { get; set; } = null!;

    /// <summary>Incremented by the tab to reload.</summary>
    [Parameter]
    public int Version { get; set; }

    protected T? Data { get; private set; }

    protected DatabaseException? Error { get; private set; }

    protected bool Loading { get; private set; }

    protected TableSummary Table => Details.Table;

    protected abstract Task<T> LoadAsync(SchemaCache schema, CancellationToken cancellationToken);

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
        try
        {
            var data = await Task.Run(() => LoadAsync(schema, cts.Token), cts.Token);
            if (!cts.IsCancellationRequested)
            {
                Data = data;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (DatabaseException ex)
        {
            QueryErrorLog.Log(LoggerFactory.CreateLogger(GetType()), ex, Active.Profile);
            Error = ex;
            Shell.ReportFailure(ex);
        }
        finally
        {
            if (ReferenceEquals(_cts, cts))
            {
                Loading = false;
            }
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        GC.SuppressFinalize(this);
    }
}
