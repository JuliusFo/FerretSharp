using System.Diagnostics;
using System.Runtime.InteropServices;

namespace FerretSharp.App.Services;

/// <summary>
/// A second start on the same data folder (WP-32, <see cref="Core.IO.InstanceLock"/>) brings the running instance's
/// window forward and ends. The new process may do so: it was just started by the user, so Windows lets it set the
/// foreground window.
/// </summary>
internal static class RunningInstance
{
    private const int SwRestore = 9;

    /// <summary>False if the process is gone, is not FerretSharp, or has no window (yet).</summary>
    public static bool BringForward(int? processId)
    {
        if (processId is not { } id)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(id);
            if (!string.Equals(process.ProcessName, Process.GetCurrentProcess().ProcessName, StringComparison.OrdinalIgnoreCase)
                || process.MainWindowHandle == IntPtr.Zero)
            {
                return false;
            }

            var window = process.MainWindowHandle;
            if (IsIconic(window))
            {
                ShowWindow(window, SwRestore);
            }

            return SetForegroundWindow(window);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hwnd);
}
