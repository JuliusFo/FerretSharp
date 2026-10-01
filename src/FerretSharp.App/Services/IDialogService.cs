namespace FerretSharp.App.Services;

public interface IDialogService
{
    void ShowError(string title, Exception exception);
}
