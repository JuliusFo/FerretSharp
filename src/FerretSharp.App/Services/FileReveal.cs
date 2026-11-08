using System.Diagnostics;
using System.IO;
using FerretSharp.UI.State;
using Microsoft.Extensions.Logging;

namespace FerretSharp.App.Services;

/// <summary>Windows Explorer with the file selected (<c>explorer.exe /select,"path"</c>).</summary>
public sealed class FileReveal(ILogger<FileReveal> logger) : IFileReveal
{
    public void Reveal(string path)
    {
        try
        {
            // A full path never holds a quote on Windows: the argument cannot be broken out of.
            using var process = Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{Path.GetFullPath(path)}\"") { UseShellExecute = false });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or ArgumentException)
        {
            logger.LogWarning(ex, "Could not show {Path} in Explorer", path);
        }
    }
}
