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

    public async Task<string?> PickAsync(string filter, IReadOnlyList<string> extensions, string? initialPath = null)
    {
        var patterns = string.Join(";", extensions.Select(e => "*." + e));
        var (folder, file) = await PathProbe.StartOfAsync(initialPath);
        return await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var dialog = new OpenFileDialog
            {
                Filter = extensions.Count > 0 ? $"{filter} ({patterns})|{patterns}|{CommonText.AllFiles} (*.*)|*.*" : $"{CommonText.AllFiles} (*.*)|*.*",
                CheckFileExists = true,
            };
            if (folder is not null)
            {
                dialog.InitialDirectory = folder;
                dialog.FileName = file ?? "";
            }

            return dialog.ShowDialog(Application.Current.MainWindow) == true ? dialog.FileName : null;
        }).Task;
    }
}
