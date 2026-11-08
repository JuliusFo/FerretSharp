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
            // A quote would end the argument (one in the folder of the settings: .NET keeps it in a full path) – not shown.
            // Explorer by its full path, not looked up in the app's or the current folder (review WP-77, K6).
            var full = Path.GetFullPath(path);
            if (full.Contains('"', StringComparison.Ordinal))
            {
                logger.LogWarning("Not shown in Explorer: the path holds a quote");
                return;
            }

            var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            using var process = Process.Start(new ProcessStartInfo(explorer, $"/select,\"{full}\"") { UseShellExecute = false });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or ArgumentException)
        {
            logger.LogWarning(ex, "Could not show {Path} in Explorer", path);
        }
    }
}
