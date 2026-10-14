namespace FerretSharp.App.Services;

public interface IDialogService
{
    void ShowError(string title, Exception exception);

    /// <summary>A warning with Yes/No; true for Yes.</summary>
    bool Ask(string title, string message);
}
