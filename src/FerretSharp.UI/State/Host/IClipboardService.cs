namespace FerretSharp.UI.State;

/// <summary>
/// The system clipboard through the host (WPF), for text that arrives after a read in the background (the values of a column,
/// WP-57): the page's clipboard (<c>navigator.clipboard</c>) refuses to write while the window is not in front – and the user
/// may well be waiting in Excel. Copies made right from a menu or key stay with the page (<see cref="DomModule"/>).
/// </summary>
public interface IClipboardService
{
    /// <returns>False if the clipboard could not be written (another program holds it).</returns>
    Task<bool> SetTextAsync(string text);
}
