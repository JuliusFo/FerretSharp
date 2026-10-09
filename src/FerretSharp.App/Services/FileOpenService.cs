using System.IO;
using System.Windows;
using FerretSharp.UI.Resources;
using FerretSharp.UI.State;
using Microsoft.Win32;

namespace FerretSharp.App.Services;

public sealed class FileOpenService : IFileOpenService
{
    public async Task<OpenedFile?> OpenAsync(string filter, IReadOnlyList<string> extensions, long maxBytes)
    {
        var path = await PickAsync(filter, extensions);
        if (path is null)
        {
            return null;
        }

        var length = new FileInfo(path).Length;
        return new OpenedFile(Path.GetFileName(path), length, length <= maxBytes ? await File.ReadAllBytesAsync(path) : null);
    }

    public Task<string?> PickAsync(string filter, IReadOnlyList<string> extensions, string? initialPath = null)
    {
        var patterns = string.Join(";", extensions.Select(e => "*." + e));
        return Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var dialog = new OpenFileDialog
            {
                Filter = extensions.Count > 0 ? $"{filter} ({patterns})|{patterns}|{CommonText.AllFiles} (*.*)|*.*" : $"{CommonText.AllFiles} (*.*)|*.*",
                CheckFileExists = true,
            };
            if (!string.IsNullOrWhiteSpace(initialPath))
            {
                var directory = Directory.Exists(initialPath) ? initialPath : Path.GetDirectoryName(initialPath);
                if (directory is not null && Directory.Exists(directory))
                {
                    dialog.InitialDirectory = directory;
                    dialog.FileName = File.Exists(initialPath) ? Path.GetFileName(initialPath) : "";
                }
            }

            return dialog.ShowDialog(Application.Current.MainWindow) == true ? dialog.FileName : null;
        }).Task;
    }
}
