using System.IO;
using System.Text;
using System.Windows;
using FerretSharp.UI.State;
using Microsoft.Win32;

namespace FerretSharp.App.Services;

public sealed class FileSaveService : IFileSaveService
{
    public async Task<string?> SaveTextAsync(string fileName, string filter, string text, bool utf8Bom)
    {
        var path = await AskAsync(fileName, filter);
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

    private static Task<string?> AskAsync(string fileName, string filter)
    {
        var extension = Path.GetExtension(fileName);
        return Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var dialog = new SaveFileDialog
            {
                FileName = fileName,
                DefaultExt = extension,
                Filter = $"{filter} (*{extension})|*{extension}|Alle Dateien (*.*)|*.*",
                AddExtension = true,
                OverwritePrompt = true,
            };
            return dialog.ShowDialog(Application.Current.MainWindow) == true ? dialog.FileName : null;
        }).Task;
    }
}
