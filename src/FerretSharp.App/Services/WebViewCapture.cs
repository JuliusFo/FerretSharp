using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Wpf;
using Windows.Foundation;
using Windows.Graphics.Capture;

namespace FerretSharp.App.Services;

/// <summary>
/// Keeps the picture of the WebView current. BlazorWebView shows the page through the
/// <see cref="WebView2CompositionControl"/>, which captures the browser's output (<c>GraphicsCaptureSession</c>, a frame
/// pool of one buffer) and copies each frame into a <see cref="D3DImage"/>. Two of its weak spots are covered here
/// (ADR 0038); the control's members are private, reached by reflection and checked once the WebView exists – should a
/// WebView2 update rename them, the log says so and the control works as it does on its own.
/// <list type="bullet">
/// <item>Frozen after sleep (2026-10-30): the Direct3D devices and the back buffer are set up once, after sleep the window
/// kept the last frame for good while the page went on running – the cursor still changed over buttons (it comes from the
/// browser, not the picture), clicks seemed to do nothing. Since WebView2 1.0.4258 the control rebuilds devices and capture
/// after a change of the display settings; waking up does not always change them, and a front buffer that comes back does
/// not set the back buffer again. The same recovery is started here: after waking, when the front buffer is back, and when
/// the window returns from minimized (the user's way out should it still freeze).</item>
/// <item>Last frame lost (2026-10-29/30): the control copies a frame only if it can lock the image at once
/// (<c>TryLock</c> without waiting) and drops it otherwise – while WPF's render thread still presents the previous one. The
/// capture delivers a frame only when the page changes, so after a quick series of changes the last one could be missing
/// until the next change: the LOB dialog stayed at "Loading …", rows after an FK jump stayed empty, a SQL tab showed its
/// first line – until a click. Here the frame waits in the pool until the image is free and is copied then.</item>
/// </list>
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

    /// <summary>Its frame pool (new with every rebuild), its handler of a frame and the copy of the next frame into the image.</summary>
    private static readonly FieldInfo? PoolField = ImageField?.FieldType.GetField("_framePool", Private);
    private static readonly MethodInfo? FrameArrived = ImageField?.FieldType.GetMethod("OnFrameArrived", Private, [typeof(Direct3D11CaptureFramePool), typeof(object)]);
    private static readonly MethodInfo? RequestRender = ImageField?.FieldType.GetMethod("RequestRender", BindingFlags.Instance | BindingFlags.Public, Type.EmptyTypes);

    private static readonly TimeSpan AfterWaking = TimeSpan.FromSeconds(3);

    /// <summary>How often a frame that waits checks the image again, and for how long at most (then copied or dropped as before).</summary>
    private static readonly TimeSpan HoldStep = TimeSpan.FromMilliseconds(4);
    private static readonly TimeSpan HoldLimit = TimeSpan.FromSeconds(1);

    /// <summary>A rebuild creates a new pool, at a time of the control's choosing (retries): looked for this often.</summary>
    private static readonly TimeSpan PoolCheck = TimeSpan.FromSeconds(2);

    /// <summary>Held frames are noted in the log at most this often.</summary>
    private static readonly TimeSpan NoteEvery = TimeSpan.FromMinutes(1);

    private WebView2CompositionControl? _control;
    private D3DImage? _watched;

    private Direct3D11CaptureFramePool? _pool;
    private DispatcherTimer? _hold;
    private long _heldSince;
    private int _held;
    private TimeSpan _longestHold;
    private long _lastNote;

    /// <summary>Takes the control once the WebView has started (on the UI thread).</summary>
    public void Attach(WebView2CompositionControl control)
    {
        _control = control;
        if (ImageField is null || DisplayChanging is null || DisplayChanged is null)
        {
            logger.LogWarning("The WebView's capture cannot be restarted with this WebView2 version (members not found)");
        }
        else
        {
            WatchFrontBuffer();
        }

        if (PoolField is null || FrameArrived is null || RequestRender is null)
        {
            logger.LogWarning("The WebView's frames cannot be held with this WebView2 version (members not found)");
            return;
        }

        _hold = new DispatcherTimer(DispatcherPriority.Render, control.Dispatcher) { Interval = HoldStep };
        _hold.Tick += (_, _) => DeliverHeld();
        TakeOverPool();
        var check = new DispatcherTimer(DispatcherPriority.Background) { Interval = PoolCheck };
        check.Tick += (_, _) => TakeOverPool();
        check.Start();
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

    /// <summary>
    /// Takes the frames of the control's current pool over: its own handler off (the delegate compares equal to the one it
    /// added, the control removes it the same way), this one on. A pool replaced by a rebuild is taken over at the next check.
    /// </summary>
    private void TakeOverPool()
    {
        if (_control is null || ImageField?.GetValue(_control) is not { } image
            || PoolField?.GetValue(image) is not Direct3D11CaptureFramePool pool || ReferenceEquals(pool, _pool))
        {
            return;
        }

        try
        {
            var controls = (TypedEventHandler<Direct3D11CaptureFramePool, object>)Delegate.CreateDelegate(
                typeof(TypedEventHandler<Direct3D11CaptureFramePool, object>), image, FrameArrived!);
            pool.FrameArrived -= controls;
            pool.FrameArrived += OnFrameArrived;
            _pool = pool;
            logger.LogDebug("Frames of the WebView's capture taken over");
        }
        catch (Exception ex) when (ex is ObjectDisposedException or System.Runtime.InteropServices.COMException)
        {
            logger.LogDebug("Taking over the WebView's frame pool failed: {Message}", ex.Message); // disposed by a rebuild: the next check
        }
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool pool, object args)
    {
        if (!ReferenceEquals(pool, _pool) || _hold is not { } hold || hold.IsEnabled)
        {
            return; // an old pool; or a frame already waits – the copy then takes the newest frame of the pool
        }

        if (CanWrite())
        {
            Render();
            return;
        }

        _heldSince = Stopwatch.GetTimestamp();
        hold.Start();
    }

    private void DeliverHeld()
    {
        var waited = Stopwatch.GetElapsedTime(_heldSince);
        if (!CanWrite() && waited < HoldLimit)
        {
            return;
        }

        _hold!.Stop();
        Render(); // past the limit (front buffer gone): the control drops it as it would have
        NoteHeld(waited);
    }

    /// <summary>
    /// Whether the control's copy will get the lock without waiting. The lock is free once WPF's render thread has presented
    /// the last copy; only a commit on this (the UI) thread takes it again, so it stays free until the copy below.
    /// </summary>
    private bool CanWrite()
    {
        if (_control is null || ImageField?.GetValue(_control) is not D3DImage image)
        {
            return true;
        }

        var free = image.TryLock(new Duration(TimeSpan.Zero));
        image.Unlock(); // TryLock counts the lock also when it failed
        return free;
    }

    private void Render()
    {
        if (_control is null || ImageField?.GetValue(_control) is not { } image)
        {
            return;
        }

        try
        {
            RequestRender!.Invoke(image, null);
        }
        catch (TargetInvocationException ex)
        {
            logger.LogDebug("Copying a WebView frame failed: {Message}", ex.InnerException?.Message); // device lost: the rebuild follows
        }
    }

    /// <summary>A held frame is normal under load; the log gets how often and how long, at most once a minute.</summary>
    private void NoteHeld(TimeSpan waited)
    {
        _held++;
        if (waited > _longestHold)
        {
            _longestHold = waited;
        }

        if (_lastNote != 0 && Stopwatch.GetElapsedTime(_lastNote) < NoteEvery)
        {
            return;
        }

        logger.LogDebug("WebView frames held until the image was free: {Count}, longest {Longest} ms", _held, (int)_longestHold.TotalMilliseconds);
        _held = 0;
        _longestHold = TimeSpan.Zero;
        _lastNote = Stopwatch.GetTimestamp();
    }
}
