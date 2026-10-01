using System.Windows;

namespace FerretSharp.App.Services;

public sealed class DialogService : IDialogService
{
    public void ShowError(string title, Exception exception)
    {
        // Plain MessageBox for now; replaced by a proper error dialog (Oracle code + statement) in WP-07.
        MessageBox.Show(
            Application.Current?.MainWindow!,
            exception.Message,
            title,
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }
}
