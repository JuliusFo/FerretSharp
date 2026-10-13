using System.Runtime.InteropServices;

namespace FerretSharp.AppTrace;

/// <summary>
/// The command line of another process (for its <c>-environment=…</c>, WP-33). .NET has no API for it, and the diagnostics
/// client keeps its own (<c>GetProcessInfo</c>) internal: Windows through <c>NtQueryInformationProcess</c>, Linux through
/// <c>/proc/&lt;pid&gt;/cmdline</c>. Null if it cannot be read (another user, the process ended).
/// </summary>
internal static partial class ProcessCommandLine
{
    public static string? Read(int processId)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return ReadWindows(processId);
            }

            if (OperatingSystem.IsLinux())
            {
                return ReadLinux(processId);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ExternalException)
        {
            // gone or not ours: shown without environment
        }

        return null;
    }

    /// <summary>NUL-separated arguments, joined like a Windows command line (quoted where they contain spaces).</summary>
    private static string? ReadLinux(int processId)
    {
        var raw = File.ReadAllText($"/proc/{processId}/cmdline");
        var args = raw.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        return args.Length == 0 ? null : Join(args);
    }

    private const int ProcessQueryLimitedInformation = 0x1000;
    private const int ProcessCommandLineInformation = 60; // PROCESSINFOCLASS, Windows 8.1 and later
    private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);

    private static string? ReadWindows(int processId)
    {
        var process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (process == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var status = NtQueryInformationProcess(process, ProcessCommandLineInformation, IntPtr.Zero, 0, out var length);
            if (status != StatusInfoLengthMismatch || length <= 0)
            {
                return null;
            }

            var buffer = Marshal.AllocHGlobal(length);
            try
            {
                if (NtQueryInformationProcess(process, ProcessCommandLineInformation, buffer, length, out _) != 0)
                {
                    return null;
                }

                // UNICODE_STRING { ushort Length (bytes); ushort MaximumLength; pointer Buffer } – the text follows in the buffer.
                var bytes = (ushort)Marshal.ReadInt16(buffer);
                var text = Marshal.ReadIntPtr(buffer, IntPtr.Size);
                return bytes == 0 ? null : Marshal.PtrToStringUni(text, bytes / sizeof(char));
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            CloseHandle(process);
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr OpenProcess(int access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int processId);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);

    [LibraryImport("ntdll.dll")]
    private static partial int NtQueryInformationProcess(IntPtr process, int informationClass, IntPtr information, int length, out int returnLength);

    /// <summary>Arguments as one command line, quoted where they contain spaces (what <see cref="Core.AppTrace.AppEnvironment.Split"/> reads).</summary>
    private static string Join(IEnumerable<string> args) =>
        string.Join(' ', args.Select(a => a.Contains(' ', StringComparison.Ordinal) ? $"\"{a}\"" : a));
}
