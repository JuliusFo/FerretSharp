using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace FerretSharp.App.Services;

/// <summary>
/// Logs what the graphics output runs on and when it changes: at the start remote session (RDP), render tier, monitors
/// and the primary screen; later every change of the display settings and every switch of the Windows session (RDP
/// connected/disconnected, locked). On a change of the display settings the WebView's control locks its image without a
/// limit (ADR 0038) – a hang right after such a line points there. Started before the main window exists: the handlers
/// of system events run in the order they were added, so the line is written before the control's handler can hang.
/// </summary>
public sealed class DisplayWatch(ILogger<DisplayWatch> logger) : IDisposable
{
    private const int MonitorCount = 80; // SM_CMONITORS
    private const int ScreenWidth = 0; // SM_CXSCREEN
    private const int ScreenHeight = 1; // SM_CYSCREEN

    /// <summary>WPF's switch for the GPU in a remote session (ADR 0043, set unless <c>--rdp-software</c>); off, it renders in software there.</summary>
    public const string HardwareAccelerationInRdp = "Switch.System.Windows.Media.EnableHardwareAccelerationInRdp";

    private bool _started;

    public void Start()
    {
        var rdpGpu = AppContext.TryGetSwitch(HardwareAccelerationInRdp, out var on) && on;
        logger.LogInformation("Display: {Facts}, render tier {Tier}, GPU in a remote session {RdpGpu}",
            Facts(), RenderCapability.Tier >> 16, rdpGpu ? "on" : "off");
        SystemEvents.DisplaySettingsChanging += OnDisplaySettingsChanging;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        SystemEvents.SessionSwitch += OnSessionSwitch;
        _started = true;
    }

    private void OnDisplaySettingsChanging(object? sender, EventArgs e) =>
        logger.LogInformation("Display settings changing");

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) =>
        logger.LogInformation("Display settings changed: {Facts}", Facts());

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e) =>
        logger.LogInformation("Windows session {Reason}, remote: {Remote}", e.Reason, SystemParameters.IsRemoteSession);

    /// <summary>Screen size in physical pixels (the process is per-monitor DPI aware).</summary>
    private static string Facts() =>
        $"remote session {(SystemParameters.IsRemoteSession ? "yes" : "no")}, {GetSystemMetrics(MonitorCount)} monitor(s), "
        + $"primary {GetSystemMetrics(ScreenWidth)}x{GetSystemMetrics(ScreenHeight)}";

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    /// <summary>Static events: they would keep this object alive.</summary>
    public void Dispose()
    {
        if (!_started)
        {
            return;
        }

        SystemEvents.DisplaySettingsChanging -= OnDisplaySettingsChanging;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
    }
}
