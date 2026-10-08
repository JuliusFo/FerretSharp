using Microsoft.AspNetCore.Components;

namespace FerretSharp.UI.Components;

/// <summary>
/// Base of components that start work outside a Blazor event handler – a JS callback, a shortcut, a timer, an event of a
/// service (R3b). An exception of such work goes to the nearest error boundary (the tab's <c>TabFrame</c>), like one of an
/// event handler. Before, those calls were fire-and-forget (<c>_ = …</c>, <c>.catch(() =&gt; {})</c> in grid.js): a bug there
/// left no trace but an "unobserved task exception" in the log, if any.
/// </summary>
public abstract class FerretComponent : ComponentBase
{
    /// <summary>Starts <paramref name="work"/> on the renderer's context without waiting for it.</summary>
    protected void Fire(Func<Task> work) => _ = InvokeAsync(() => GuardAsync(work));

    /// <summary>
    /// Runs <paramref name="work"/>; an exception goes to the error boundary instead of the caller. For
    /// <c>[JSInvokable]</c> handlers: JS neither waits for nor shows their errors. Cancellation stays silent.
    /// </summary>
    protected async Task GuardAsync(Func<Task> work)
    {
        try
        {
            await work();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await DispatchExceptionAsync(ex);
        }
    }

    /// <inheritdoc cref="GuardAsync(Func{Task})"/>
    /// <param name="failed">What JS gets after an exception.</param>
    protected async Task<T> GuardAsync<T>(Func<Task<T>> work, T failed)
    {
        try
        {
            return await work();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await DispatchExceptionAsync(ex);
            return failed;
        }
    }
}
