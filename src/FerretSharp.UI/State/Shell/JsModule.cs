using Microsoft.JSInterop;

namespace FerretSharp.UI.State;

/// <summary>
/// An ES module of <c>wwwroot/js</c> for one component (R5b, review 3.31.0 B9: <c>DomModule</c>, the grid, Monaco, the
/// shell, the tab bar and the shortcut settings each imported and disposed theirs by hand; two of them kept a module
/// that arrived after the component was gone): imported on first use; once disposed, no import happens and calls do
/// nothing – also a call whose import was still on its way. A page or component that went away meanwhile is no error;
/// disposing never throws.
/// </summary>
public class JsModule(IJSRuntime js, string path) : IAsyncDisposable
{
    private IJSObjectReference? _module;

    protected bool IsDisposed { get; private set; }

    /// <summary>Imported and not disposed.</summary>
    public bool IsLoaded => _module is not null && !IsDisposed;

    /// <summary>Imports the module now; false once disposed (also if that happened while it loaded).</summary>
    public async ValueTask<bool> LoadAsync() => await ModuleAsync() is not null;

    /// <returns>False if it did not run: disposed, or gone while it ran.</returns>
    public async ValueTask<bool> InvokeVoidAsync(string identifier, params object?[]? args)
    {
        if (await ModuleAsync() is not { } module)
        {
            return false;
        }

        try
        {
            await module.InvokeVoidAsync(identifier, args);
            return true;
        }
        catch (Exception ex) when (Gone(ex))
        {
            return false;
        }
    }

    /// <returns>The result; default once disposed or gone while it ran.</returns>
    public async ValueTask<T> InvokeAsync<T>(string identifier, params object?[]? args)
    {
        if (await ModuleAsync() is { } module)
        {
            try
            {
                return await module.InvokeAsync<T>(identifier, args);
            }
            catch (Exception ex) when (Gone(ex))
            {
            }
        }

        return default!;
    }

    public ValueTask DisposeAsync() => DisposeAsync(null);

    /// <summary>Disposes the module; if it was loaded, calls <paramref name="lastCall"/> first (<c>destroy</c>, <c>unregister</c>).</summary>
    public async ValueTask DisposeAsync(string? lastCall, params object?[] args)
    {
        IsDisposed = true;
        if (_module is not { } module)
        {
            return;
        }

        _module = null;
        try
        {
            if (lastCall is not null)
            {
                await module.InvokeVoidAsync(lastCall, args);
            }

            await module.DisposeAsync();
        }
        catch (Exception ex) when (ex is JSDisconnectedException or ObjectDisposedException or JSException)
        {
            // the page is gone
        }
    }

    /// <summary>The page went away, or the component did while the call ran.</summary>
    private bool Gone(Exception ex) => ex is JSDisconnectedException || (IsDisposed && ex is ObjectDisposedException or JSException);

    private async ValueTask<IJSObjectReference?> ModuleAsync()
    {
        if (_module is null && !IsDisposed)
        {
            IJSObjectReference module;
            try
            {
                module = await js.InvokeAsync<IJSObjectReference>("import", path);
            }
            catch (Exception ex) when (Gone(ex))
            {
                return null;
            }

            if (IsDisposed || _module is not null)
            {
                await QuietlyDisposeAsync(module); // disposed meanwhile, or a parallel call imported it first
            }
            else
            {
                _module = module;
            }
        }

        return IsDisposed ? null : _module;
    }

    private static async ValueTask QuietlyDisposeAsync(IJSObjectReference module)
    {
        try
        {
            await module.DisposeAsync();
        }
        catch (Exception ex) when (ex is JSDisconnectedException or ObjectDisposedException)
        {
        }
    }
}
