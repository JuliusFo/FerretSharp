using FerretSharp.UI.Resources;
using Microsoft.JSInterop;

namespace FerretSharp.UI.State;

/// <summary>
/// <c>dom.js</c> for one component (R3b; was imported by hand in sixteen places, only three of them safe against being
/// disposed meanwhile), a <see cref="JsModule"/>. A component keeps one through <c>Dom =&gt; _dom ??= new(JS)</c> and
/// disposes it with <c>await Dom.DisposeAsync()</c>, so a render that runs after the dispose finds the disposed instance.
/// </summary>
public sealed class DomModule(IJSRuntime js) : JsModule(js, "./_content/FerretSharp.UI/js/dom.js")
{
    /// <summary>Copies text to the clipboard; false if it is not available (or the component is gone).</summary>
    public async Task<bool> CopyTextAsync(string text) => await InvokeAsync<bool>("copyText", text);

    /// <summary>Copies text to the clipboard; if it is not available, says so as a notice.</summary>
    public async Task<bool> CopyTextAsync(ShellState shell, string text)
    {
        if (await CopyTextAsync(text))
        {
            return true;
        }

        if (!IsDisposed)
        {
            shell.Notify(CommonText.ClipboardUnavailable);
        }

        return false;
    }
}
