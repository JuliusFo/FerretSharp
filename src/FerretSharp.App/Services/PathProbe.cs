using System.IO;

namespace FerretSharp.App.Services;

/// <summary>
/// File system checks before a native dialog, off the UI thread and with a time limit: a remembered folder on a share
/// behind a VPN that is gone made <c>Directory.Exists</c> wait for the SMB timeout with the window frozen (review 3.31.0, A45).
/// </summary>
internal static class PathProbe
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(2);

    /// <summary>The folder for the dialog to start in: <paramref name="path"/> if it is one, else its parent folder; null if neither answers in time.</summary>
    public static async Task<(string? Folder, string? FileName)> StartOfAsync(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return (null, null);
        }

        try
        {
            return await Task.Run(() =>
            {
                if (Directory.Exists(path))
                {
                    return (path, (string?)null);
                }

                var folder = Path.GetDirectoryName(path);
                return folder is not null && Directory.Exists(folder)
                    ? (folder, File.Exists(path) ? Path.GetFileName(path) : null)
                    : (null, null);
            }).WaitAsync(Limit);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            return (null, null);
        }
    }
}
