using System.Runtime.InteropServices;
using System.Windows;
using FerretSharp.UI.State;

namespace FerretSharp.App.Services;

public sealed class ClipboardService : IClipboardService
{
    /// <summary>
    /// The clipboard needs an STA thread – its own, not the UI thread (review 3.44.0, A23): WPF already tries again for about a
    /// second with <c>Thread.Sleep</c> when another program holds the clipboard (a clipboard manager, the RDP clipboard),
    /// and with retries of our own on the UI thread the window froze for up to five seconds.
    /// </summary>
    public Task<bool> SetTextAsync(string text)
    {
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                Clipboard.SetDataObject(text, copy: true);
                done.SetResult(true);
            }
            catch (ExternalException)
            {
                done.SetResult(false);
            }
            catch (Exception ex)
            {
                // Unhandled on a thread of its own it ended the app (review 3.54.0, A20) – the caller shows it instead.
                done.SetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "Clipboard",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task;
    }
}
