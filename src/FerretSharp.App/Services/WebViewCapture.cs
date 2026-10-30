using System.Reflection;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Wpf;

namespace FerretSharp.App.Services;

/// <summary>
/// Restarts the picture of the WebView when it may have frozen. BlazorWebView shows the page through the
/// <see cref="WebView2CompositionControl"/>, which captures the browser's output (<c>GraphicsCaptureSession</c>) into a
/// <see cref="D3DImage"/> whose Direct3D devices and back buffer are set up once. After sleep the window kept the last
/// frame for good while the page went on running: the cursor still changed over buttons (it comes from the browser, not
/// the picture), clicks seemed to do nothing (2026-10-30).
/// Since WebView2 1.0.4258 the control rebuilds devices and capture after a change of the display settings. Waking up does
/// not always change them, and the control does not set its back buffer again when WPF's front buffer comes back – so the
/// same recovery is started here: after waking, when the front buffer is back, and when the window returns from minimized
/// (the user's way out should it still freeze). The recovery is private to the control, reached by reflection and checked
/// once the WebView exists; should a WebView2 update rename it, the log says so and nothing else changes.
/// </summary>
public sealed class WebViewCapture(ILogger<WebViewCapture> logger)
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly Type[] HandlerParameters = [typeof(object), typeof(EventArgs)];

    /// <summary>The control's <c>GraphicsItemD3DImage</c>; created when the control is loaded.</summary>
    private static readonly FieldInfo? ImageField = typeof(WebView2CompositionControl).GetField("_d3dImage", Private);

    /// <summary>Its handlers of the display change: the first drops the back buffer, the second rebuilds (with retries).</summary>
    private static readonly MethodInfo? DisplayChanging = ImageField?.FieldType.GetMethod("OnDisplaySettingsChanging", Private, HandlerParameters);
    private static readonly MethodInfo? DisplayChanged = ImageField?.FieldType.GetMethod("OnDisplaySettingsChanged", Private, HandlerParameters);

    private static readonly TimeSpan AfterWaking = TimeSpan.FromSeconds(3);

    private WebView2CompositionControl? _control;
    private D3DImage? _watched;

    /// <summary>Takes the control once the WebView has started (on the UI thread).</summary>
    public void Attach(WebView2CompositionControl control)
    {
        _control = control;
        if (ImageField is null || DisplayChanging is null || DisplayChanged is null)
        {
            logger.LogWarning("The WebView's capture cannot be restarted with this WebView2 version (members not found)");
            return;
        }

        WatchFrontBuffer();
    }

    /// <summary>After waking up: a few seconds later, when the graphics output is back (from any thread).</summary>
    public void RestartSoon(string reason)
    {
        if (_control is not { } control)
        {
            return;
        }

        control.Dispatcher.BeginInvoke(() =>
        {
            var timer = new DispatcherTimer { Interval = AfterWaking };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                Restart(reason);
            };
            timer.Start();
        });
    }

    /// <summary>Rebuilds devices and capture as after a change of the display settings (on the UI thread).</summary>
    public void Restart(string reason)
    {
        if (_control is null || ImageField?.GetValue(_control) is not { } image || DisplayChanging is null || DisplayChanged is null)
        {
            return;
        }

        WatchFrontBuffer();
        logger.LogInformation("Restarting the WebView's capture ({Reason})", reason);
        try
        {
            DisplayChanging.Invoke(image, [null, EventArgs.Empty]);
            DisplayChanged.Invoke(image, [null, EventArgs.Empty]);
        }
        catch (TargetInvocationException ex)
        {
            logger.LogWarning(ex.InnerException, "Restarting the WebView's capture failed");
        }
    }

    /// <summary>The image exists only after the control has loaded: watched from the first chance on, once.</summary>
    private void WatchFrontBuffer()
    {
        if (_watched is not null || _control is null || ImageField?.GetValue(_control) is not D3DImage image)
        {
            return;
        }

        _watched = image;
        image.IsFrontBufferAvailableChanged += (_, e) =>
        {
            if (e.NewValue is true)
            {
                Restart("front buffer available again");
            }
        };
    }
}
