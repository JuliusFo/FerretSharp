using Microsoft.JSInterop;

namespace FerretSharp.UI.State;

/// <summary>
/// <c>dom.js</c> for one component (R3b; was imported by hand in sixteen places, only three of them safe against being
/// disposed meanwhile): imported on first use; once disposed, no import happens and calls do nothing – also a call whose
/// import was still on its way. Disposing never throws. Same call signatures as an <see cref="IJSObjectReference"/>.
/// A component keeps one through <c>Dom =&gt; _dom ??= new(JS)</c> and disposes it with <c>await Dom.DisposeAsync()</c>, so a
/// render that runs after the dispose finds the disposed instance.
/// </summary>
public sealed class DomModule(IJSRuntime js) : IAsyncDisposable
{
    private const string Path = "./_content/FerretSharp.UI/js/dom.js";

    private IJSObjectReference? _module;
    private bool _disposed;

    public async ValueTask InvokeVoidAsync(string identifier, params object?[]? args)
    {
        if (await ModuleAsync() is { } module)
        {
            try
            {
                await module.InvokeVoidAsync(identifier, args);
            }
            catch (Exception ex) when (_disposed && ex is JSDisconnectedException or ObjectDisposedException)
            {
                // disposed while the call ran
            }
        }
    }

    /// <returns>The result; default once disposed.</returns>
    public async ValueTask<T> InvokeAsync<T>(string identifier, params object?[]? args)
    {
        if (await ModuleAsync() is { } module)
        {
            try
            {
                return await module.InvokeAsync<T>(identifier, args);
            }
            catch (Exception ex) when (_disposed && ex is JSDisconnectedException or ObjectDisposedException)
            {
                // disposed while the call ran
            }
        }

        return default!;
    }

    /// <summary>Copies text to the clipboard; false if it is not available (or the component is gone).</summary>
    public async Task<bool> CopyTextAsync(string text) => await InvokeAsync<bool>("copyText", text);

    /// <summary>Copies text to the clipboard; if it is not available, says so as a notice.</summary>
    public async Task<bool> CopyTextAsync(ShellState shell, string text)
    {
        if (await CopyTextAsync(text))
        {
            return true;
        }

        if (!_disposed)
        {
            shell.Notify("Die Zwischenablage ist nicht verfügbar.");
        }

        return false;
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        if (_module is { } module)
        {
            _module = null;
            try
            {
                await module.DisposeAsync();
            }
            catch (Exception ex) when (ex is JSDisconnectedException or ObjectDisposedException)
            {
                // the page is gone
            }
        }
    }

    private async ValueTask<IJSObjectReference?> ModuleAsync()
    {
        if (_module is null && !_disposed)
        {
            var module = await js.InvokeAsync<IJSObjectReference>("import", Path);
            if (_disposed || _module is not null)
            {
                await module.DisposeAsync(); // disposed meanwhile, or a parallel call imported it first
            }
            else
            {
                _module = module;
            }
        }

        return _disposed ? null : _module;
    }
}
