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
        var extension = Path.GetExtension(fileName);
        var path = await Application.Current.Dispatcher.InvokeAsync(() =>
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
        });

        if (path is not null)
        {
            await File.WriteAllTextAsync(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: utf8Bom));
        }

        return path;
    }
}
