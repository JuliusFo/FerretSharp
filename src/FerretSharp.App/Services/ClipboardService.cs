using System.Runtime.InteropServices;
using System.Windows;
using FerretSharp.UI.State;

namespace FerretSharp.App.Services;

public sealed class ClipboardService : IClipboardService
{
    private const int Attempts = 5;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// The clipboard is opened on the UI thread (it needs STA). Another program holding it briefly (a clipboard manager, Excel
    /// reading it) is common: a few more attempts before giving up.
    /// </summary>
    public async Task<bool> SetTextAsync(string text)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await Application.Current.Dispatcher.InvokeAsync(() => Clipboard.SetDataObject(text, copy: true));
                return true;
            }
            catch (ExternalException) when (attempt < Attempts)
            {
                await Task.Delay(RetryDelay);
            }
            catch (ExternalException)
            {
                return false;
            }
        }
    }
}
