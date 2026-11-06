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
/// frames keep the old size) or WPF (it shows an old picture). Each line holds the whole chain:
/// <list type="bullet">
/// <item>window state and the control's size in pixels (what the control hands on);</item>
/// <item>the browser's bounds, rasterization scale and visibility (<c>CoreWebView2Controller</c>);</item>
/// <item>the page's viewport (only in the line once the size has settled, it needs a round trip to the page);</item>
/// <item>the size of the last frame copied (<c>ContentSize</c>, the control's <c>_lastSize</c>) and of the picture WPF
/// shows (<see cref="D3DImage.PixelWidth"/>), and whether WPF's front buffer is there.</item>
/// </list>
/// A line when a change starts, one once it has settled (<see cref="Settle"/>) and one when the picture first takes a new
/// size afterwards; a picture that changes size outside of a change (cleared by a restart, late) is always logged. The
/// control's members are private, reached by reflection like in <see cref="WebViewCapture"/>; missing ones show as "?".
/// </summary>
public sealed class WebViewSizeWatch(ILogger<WebViewSizeWatch> logger)
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    private static readonly FieldInfo? BaseField = typeof(WebView2CompositionControl).GetField("m_webview2Base", Private);
    private static readonly PropertyInfo? ControllerProperty = BaseField?.FieldType.GetProperty("CoreWebView2Controller", Private);
    private static readonly FieldInfo? DpiField = typeof(WebView2CompositionControl).GetField("_dpiScale", Private);
    private static readonly FieldInfo? ImageField = typeof(WebView2CompositionControl).GetField("_d3dImage", Private);
    private static readonly FieldInfo? FrameSizeField = ImageField?.FieldType.GetField("_lastSize", Private);

    /// <summary>How long the size has to stay put before the settled line; a drag at the border changes it many times.</summary>
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(1);

    /// <summary>The page answers within this, or the line says "?" (a hanging renderer must not hold it back).</summary>
    private static readonly TimeSpan PageLimit = TimeSpan.FromSeconds(1);

    private const string PageScript = "innerWidth + 'x' + innerHeight + ' @' + devicePixelRatio";

    private Window? _window;
    private WebView2CompositionControl? _control;
    private D3DImage? _image;
    private DispatcherTimer? _settle;
    private string _reason = "";
    private long _changeSince;
    private bool _pictureSeen;
    private int _pictureChanges;
    private (int Width, int Height) _picture;

    /// <summary>Takes window and control once the WebView has started (on the UI thread); the first line once it has settled.</summary>
    public void Attach(Window window, WebView2CompositionControl control)
    {
        _window = window;
        _control = control;
        if (ControllerProperty is null || DpiField is null || FrameSizeField is null)
        {
            logger.LogWarning("The WebView's sizes cannot all be read with this WebView2 version (members not found): they show as ?");
        }

        _settle = new DispatcherTimer(DispatcherPriority.Background, control.Dispatcher) { Interval = Settle };
        _settle.Tick += (_, _) =>
        {
            _settle.Stop();
            LogSettled(_reason);
        };
        control.SizeChanged += (_, _) => Changed("size");
        window.StateChanged += (_, _) => Changed($"state {window.WindowState}");
        WatchImage();
        _changeSince = Stopwatch.GetTimestamp();
        _reason = "start";
        _settle.Start();
    }

    /// <summary>After the control's own handler (added in its constructor): its bounds and capture size are already set.</summary>
    private void Changed(string reason)
    {
        if (_settle is not { } settle)
        {
            return;
        }

        WatchImage();
        if (!settle.IsEnabled)
        {
            _reason = reason;
            _pictureSeen = false;
            _pictureChanges = 0;
            logger.LogInformation("WebView sizes ({Reason}, changing): {Sizes}", reason, Sizes());
        }
        else if (_reason != reason)
        {
            _reason = $"{_reason}, {reason}";
        }

        _changeSince = Stopwatch.GetTimestamp();
        settle.Stop();
        settle.Start();
    }

    private async void LogSettled(string reason)
    {
        var sizes = Sizes();
        var page = await PageViewport();
        logger.LogInformation("WebView sizes ({Reason}, settled after {Ms} ms): {Sizes}, page {Page}, picture changes {Changes}",
            reason, (int)Stopwatch.GetElapsedTime(_changeSince).TotalMilliseconds, sizes, page, _pictureChanges);
        _pictureSeen = true; // from now on every change of the picture's size is noted
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
        image.Changed += (_, _) => PictureChanged(image); // raised when a back buffer is set or dropped
    }

    private void PictureChanged(D3DImage image)
    {
        var picture = (image.PixelWidth, image.PixelHeight);
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
        logger.LogInformation("WebView picture now {Picture} ({Ms} ms after the last change of size or state): {Sizes}",
            Size(picture.PixelWidth, picture.PixelHeight), (int)Stopwatch.GetElapsedTime(_changeSince).TotalMilliseconds, Sizes());
    }

    private string Sizes()
    {
        if (_window is not { } window || _control is not { } control)
        {
            return "?";
        }

        var dpi = VisualTreeHelper.GetDpi(control);
        var controlSize = Size(control.ActualWidth * dpi.DpiScaleX, control.ActualHeight * dpi.DpiScaleY);
        var controlDpi = DpiField?.GetValue(control) is double scale ? Scale(scale) : "?";
        var image = ImageField?.GetValue(control);
        var frame = image is not null && FrameSizeField?.GetValue(image) is SizeInt32 last ? Size(last.Width, last.Height) : "?";
        var picture = _image is { } d3d ? Size(d3d.PixelWidth, d3d.PixelHeight) : "?";
        var frontBuffer = _image is { } shown ? (shown.IsFrontBufferAvailable ? "available" : "gone") : "?";
        return $"window {window.WindowState}, control {controlSize} @{Scale(dpi.DpiScaleX)} (its scale {controlDpi}), "
            + $"browser {Browser(control)}, frame {frame}, picture {picture}, front buffer {frontBuffer}";
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

    /// <summary>The page's own view of its size (CSS pixels and device pixel ratio).</summary>
    private async Task<string> PageViewport()
    {
        if (_control?.CoreWebView2 is not { } webView)
        {
            return "?";
        }

        try
        {
            var json = await webView.ExecuteScriptAsync(PageScript).WaitAsync(PageLimit);
            return JsonSerializer.Deserialize<string>(json) ?? "?";
        }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException or JsonException
                                       or System.Runtime.InteropServices.COMException)
        {
            return "?";
        }
    }

    private static string Size(double width, double height) =>
        string.Create(CultureInfo.InvariantCulture, $"{Math.Round(width)}x{Math.Round(height)}");

    private static string Scale(double scale) => scale.ToString("0.00", CultureInfo.InvariantCulture);
}
