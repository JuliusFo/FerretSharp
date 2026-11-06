using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Windows.Graphics;

namespace FerretSharp.App.Services;

/// <summary>
/// Logs the sizes along the WebView's way to the screen whenever the window changes size or state. Over RDP the picture
/// sometimes stood one step behind (2026-11-03/06, tier 0 and tier 2): empty after the start, after maximizing the picture
/// of the old size in the top left corner, after restoring the maximized layout cut off – frames went on arriving, none was
/// dropped. Where the size gets stuck is open: the browser's bounds (the page lays out for the old size), the capture (its
/// frames keep the old size, or are not copied) or WPF (it shows an old picture). Each line holds the whole chain:
/// <list type="bullet">
/// <item>window state and the control's size in pixels, computed as the control does it for the browser's bounds and the
/// size of the capture's frame pool (<c>UpdateSize</c>);</item>
/// <item>the browser's bounds, rasterization scale and visibility (<c>CoreWebView2Controller</c>);</item>
/// <item>the page's viewport (only in the line once the size has settled, it needs a round trip to the page);</item>
/// <item>"frame": the size the control last set up or copied (<c>_lastSize</c>) – set when the capture starts or is
/// rebuilt and by every copied frame of another <c>ContentSize</c>, so not proof that a frame of that size was copied;
/// whether a back buffer is set; the picture WPF shows (<see cref="D3DImage.PixelWidth"/>) and its front buffer;</item>
/// <item>in the settled line: how many frames arrived (<see cref="WebViewCapture.FramesArrived"/>) and how many were
/// copied into the picture during the change.</item>
/// </list>
/// A line when a change starts (for a change of state still with the old sizes: the layout follows after the event), one
/// once it has settled (<see cref="Settle"/>) and one when the picture first takes a new size afterwards; a picture that
/// changes size outside of a change (cleared by a restart, late) is always logged. The control's members are private,
/// reached by reflection like in <see cref="WebViewCapture"/>; missing ones show as "?". A diagnosis must never disturb
/// the app: nothing here throws to the dispatcher.
/// </summary>
public sealed class WebViewSizeWatch(WebViewCapture capture, ILogger<WebViewSizeWatch> logger)
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    private static readonly FieldInfo? BaseField = typeof(WebView2CompositionControl).GetField("m_webview2Base", Private);
    private static readonly PropertyInfo? ControllerProperty = BaseField?.FieldType.GetProperty("CoreWebView2Controller", Private);
    private static readonly FieldInfo? DpiField = typeof(WebView2CompositionControl).GetField("_dpiScale", Private);
    private static readonly FieldInfo? ImageField = typeof(WebView2CompositionControl).GetField("_d3dImage", Private);
    private static readonly FieldInfo? FrameSizeField = ImageField?.FieldType.GetField("_lastSize", Private);
    private static readonly FieldInfo? BackBufferField = ImageField?.FieldType.GetField("_backBufferSet", Private);

    /// <summary>How long the size has to stay put before the settled line; a drag at the border changes it many times.</summary>
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(1);

    /// <summary>The page answers within this, or the line says "?" (a hanging renderer must not hold it back).</summary>
    private static readonly TimeSpan PageLimit = TimeSpan.FromSeconds(1);

    private const string PageScript = "innerWidth + 'x' + innerHeight + ' @' + devicePixelRatio";

    private Window? _window;
    private WebView2CompositionControl? _control;
    private D3DImage? _image;
    private DispatcherTimer? _settle;

    /// <summary>The change in progress: its reasons, when it began, frames arrived and copied before it.</summary>
    private readonly List<string> _reasons = [];
    private long _changeBegan;
    private long? _arrivedBefore;
    private int _copies;
    private int _pictureChanges;
    private bool _pictureSeen;
    private (int Width, int Height) _picture;

    /// <summary>Takes window and control once the WebView has started (on the UI thread); the first line once it has settled.</summary>
    public void Attach(Window window, WebView2CompositionControl control)
    {
        _window = window;
        _control = control;
        if (ControllerProperty is null || DpiField is null || FrameSizeField is null || BackBufferField is null)
        {
            logger.LogWarning("The WebView's sizes cannot all be read with this WebView2 version (members not found): they show as ?");
        }

        _settle = new DispatcherTimer(DispatcherPriority.Background, control.Dispatcher) { Interval = Settle };
        _settle.Tick += (_, _) =>
        {
            _settle.Stop();
            LogSettled();
        };
        control.SizeChanged += (_, _) => Changed("size");
        window.StateChanged += (_, _) => Changed($"state {window.WindowState}");
        WatchImage();
        Begin("start");
        _settle.Start();
    }

    /// <summary>
    /// After the control's own handler of <c>SizeChanged</c> (added in its constructor): bounds and frame pool are already
    /// set. <c>StateChanged</c> comes before the layout, so its line still shows the sizes before the change.
    /// </summary>
    private void Changed(string reason)
    {
        if (_settle is not { } settle)
        {
            return;
        }

        WatchImage();
        if (!settle.IsEnabled)
        {
            Begin(reason);
            logger.LogInformation("WebView sizes ({Reason}, changing): {Sizes}", reason, Sizes());
        }
        else if (!_reasons.Contains(reason))
        {
            _reasons.Add(reason); // a drag at the border: one "size" however often it fires
        }

        settle.Stop();
        settle.Start();
    }

    private void Begin(string reason)
    {
        _reasons.Clear();
        _reasons.Add(reason);
        _changeBegan = Stopwatch.GetTimestamp();
        _arrivedBefore = capture.FramesArrived;
        _copies = 0;
        _pictureChanges = 0;
        _pictureSeen = false;
    }

    /// <summary>
    /// Everything of this change is taken before waiting for the page: a new change meanwhile starts its own counts, and
    /// its first new picture is still logged (<see cref="_pictureSeen"/> is set here, before it can begin).
    /// </summary>
    private async void LogSettled()
    {
        try
        {
            var reason = string.Join(", ", _reasons);
            var ms = (int)Stopwatch.GetElapsedTime(_changeBegan).TotalMilliseconds;
            var sizes = Sizes();
            var arrived = capture.FramesArrived is { } now && _arrivedBefore is { } before ? (now - before).ToString(CultureInfo.InvariantCulture) : "?";
            var copies = _copies;
            var pictureChanges = _pictureChanges;
            _pictureSeen = true; // from now on every change of the picture's size is noted
            var page = await PageViewport();
            logger.LogInformation(
                "WebView sizes ({Reason}, settled {Ms} ms after it began): {Sizes}, page {Page}, frames arrived {Arrived}, copied {Copies}, picture changes {Changes}",
                reason, ms, sizes, page, arrived, copies, pictureChanges);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Logging the WebView's sizes failed");
        }
    }

    /// <summary>The image exists only after the control has loaded; the same object survives a rebuild of the capture.</summary>
    private void WatchImage()
    {
        if (_image is not null || _control is null || ImageField?.GetValue(_control) is not D3DImage image)
        {
            return;
        }

        _image = image;
        _picture = (image.PixelWidth, image.PixelHeight);
        // With every copied frame (AddDirtyRect; setting a new back buffer comes in the same lock) and when the back buffer is dropped.
        image.Changed += (_, _) => PictureChanged(image);
    }

    private void PictureChanged(D3DImage image)
    {
        var picture = (image.PixelWidth, image.PixelHeight);
        if (picture.PixelWidth > 0)
        {
            _copies++; // a dropped back buffer (a restart, WM_SIZE in the control's hook) leaves 0x0 and is no copy
        }

        if (picture == _picture)
        {
            return;
        }

        _picture = picture;
        _pictureChanges++;
        if (_pictureSeen && _settle?.IsEnabled == true)
        {
            return; // further steps of a change in progress: counted in the settled line
        }

        _pictureSeen = true;
        logger.LogInformation("WebView picture now {Picture} ({Ms} ms after the last change began): {Sizes}",
            Size(picture.PixelWidth, picture.PixelHeight), (int)Stopwatch.GetElapsedTime(_changeBegan).TotalMilliseconds, Sizes());
    }

    private string Sizes()
    {
        if (_window is not { } window || _control is not { } control)
        {
            return "?";
        }

        // As the control computes bounds and frame pool: its own scale (the X axis's) for both axes, cut off, not rounded –
        // rounded, 1425.99 would show as 1426 beside bounds of 1425.
        var wpfScale = VisualTreeHelper.GetDpi(control).DpiScaleX;
        var scale = DpiField?.GetValue(control) as double? ?? wpfScale;
        var controlSize = Size((int)(control.ActualWidth * scale), (int)(control.ActualHeight * scale));
        var image = ImageField?.GetValue(control);
        var frame = image is not null && FrameSizeField?.GetValue(image) is SizeInt32 last ? Size(last.Width, last.Height) : "?";
        var backBuffer = image is not null && BackBufferField?.GetValue(image) is bool set ? (set ? "set" : "none") : "?";
        var picture = _image is { } d3d ? Size(d3d.PixelWidth, d3d.PixelHeight) : "?";
        var frontBuffer = _image is { } shown ? (shown.IsFrontBufferAvailable ? "available" : "gone") : "?";
        return $"window {window.WindowState}, control {controlSize} @{Scale(scale)} (WPF {Scale(wpfScale)}), "
            + $"browser {Browser(control)}, frame {frame}, back buffer {backBuffer}, picture {picture}, front buffer {frontBuffer}";
    }

    /// <summary>The bounds the browser was given; reading a controller that is being closed throws.</summary>
    private static string Browser(WebView2CompositionControl control)
    {
        if (BaseField?.GetValue(control) is not { } webViewBase || ControllerProperty?.GetValue(webViewBase) is not CoreWebView2Controller controller)
        {
            return "?";
        }

        try
        {
            var bounds = controller.Bounds;
            return $"{Size(bounds.Width, bounds.Height)} at {bounds.X},{bounds.Y} @{Scale(controller.RasterizationScale)}"
                + (controller.IsVisible ? "" : " hidden");
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            return "?";
        }
    }

    /// <summary>
    /// The page's own view of its size (CSS pixels and device pixel ratio). The control's <c>CoreWebView2</c> throws once
    /// it is disposed or the browser process has crashed (then every change brought the "unexpected error" dialog).
    /// </summary>
    private async Task<string> PageViewport()
    {
        try
        {
            if (_control?.CoreWebView2 is not { } webView)
            {
                return "?";
            }

            var json = await webView.ExecuteScriptAsync(PageScript).WaitAsync(PageLimit);
            return JsonSerializer.Deserialize<string>(json) ?? "?";
        }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException or JsonException
                                       or System.Runtime.InteropServices.COMException)
        {
            return "?";
        }
    }

    private static string Size(int width, int height) => string.Create(CultureInfo.InvariantCulture, $"{width}x{height}");

    private static string Scale(double scale) => scale.ToString("0.00", CultureInfo.InvariantCulture);
}
