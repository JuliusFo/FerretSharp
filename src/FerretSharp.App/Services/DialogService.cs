using System.Windows;

namespace FerretSharp.App.Services;

public sealed class DialogService : IDialogService
{
    public void ShowError(string title, Exception exception)
    {
        // Unexpected (non-database) errors only; database errors use the Blazor ErrorDialog with code and statement.
        MessageBox.Show(
            Application.Current?.MainWindow!,
            exception.Message,
            title,
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    public bool Ask(string title, string message) =>
        MessageBox.Show(Application.Current?.MainWindow!, message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No)
            == MessageBoxResult.Yes;
}
