using System.IO;
using System.Text;
using System.Windows;
using FerretSharp.UI.Resources;
using FerretSharp.UI.State;
using Microsoft.Win32;

namespace FerretSharp.App.Services;

public sealed class FileSaveService : IFileSaveService
{
    public async Task<string?> SaveTextAsync(string fileName, string filter, string text, bool utf8Bom, string? folder = null)
    {
        var path = await AskAsync(fileName, filter, folder);
        if (path is not null)
        {
            await File.WriteAllTextAsync(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: utf8Bom));
        }

        return path;
    }

    public async Task<string?> SaveBytesAsync(string fileName, string filter, byte[] content)
    {
        var path = await AskAsync(fileName, filter);
        if (path is not null)
        {
            await File.WriteAllBytesAsync(path, content);
        }

        return path;
    }

    private static Task<string?> AskAsync(string fileName, string filter, string? folder = null)
    {
        // A remembered folder that is gone (renamed repository, other machine) is no reason to fail: the dialog picks one.
        var initial = folder is not null && Directory.Exists(folder) ? folder : null;
        var extension = Path.GetExtension(fileName);
        return Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var dialog = new SaveFileDialog
            {
                FileName = fileName,
                InitialDirectory = initial,
                DefaultExt = extension,
                Filter = $"{filter} (*{extension})|*{extension}|{CommonText.AllFiles} (*.*)|*.*",
                AddExtension = true,
                OverwritePrompt = true,
            };
            return dialog.ShowDialog(Application.Current.MainWindow) == true ? dialog.FileName : null;
        }).Task;
    }
}
