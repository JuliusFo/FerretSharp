namespace FerretSharp.App.Services;

public interface IDialogService
{
    void ShowError(string title, Exception exception);

    /// <summary>A warning with Yes/No; true for Yes.</summary>
    bool Ask(string title, string message);

    /// <summary>A warning with Yes/No/Cancel; true for Yes, false for No, null for Cancel.</summary>
    bool? AskYesNoCancel(string title, string message);
}
