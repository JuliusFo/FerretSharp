using FerretSharp.Core.Data;
using FerretSharp.Core.Schema;

namespace FerretSharp.UI.State;

/// <summary>
/// The export entries of a right-click menu (R3b; were written twice, in the grid's cell menu and the result menu, and their
/// messages had drifted apart): copy a value or rows to the clipboard, save them as a file – each closing the menu and
/// telling the user what happened. Copying happens before the menu closes (the clipboard needs the page's module, which
/// goes with the menu); saving after (the native dialog is modal).
/// </summary>
/// <param name="missingRows">Selected rows no longer loaded: mentioned as not exported.</param>
/// <param name="close">Closes the menu.</param>
public sealed class ExportActions(ShellState shell, DomModule dom, IFileSaveService files, int missingRows, Func<Task> close)
{
    public static string RowsText(int rows) => rows == 1 ? "1 Zeile" : $"{rows:N0} Zeilen";

    /// <summary>The full value (like Ctrl+C), not the shortened display text.</summary>
    public async Task CopyValueAsync(ColumnInfo column, object? value)
    {
        var cell = DelimitedExport.CellText(column, value);
        var copied = await dom.CopyTextAsync(cell.Text);
        await close();
        if (!copied)
        {
            shell.Notify("Die Zwischenablage ist nicht verfügbar.");
        }
        else if (cell.Warnings.Count > 0)
        {
            shell.Notify("Wert nicht kopiert.", cell.Warnings); // a LOB with only its preview loaded, a type that is not exported
        }
    }

    /// <summary>Copies any text (a C# value); only a missing clipboard is worth a word.</summary>
    public async Task CopyTextAsync(string text)
    {
        var copied = await dom.CopyTextAsync(text);
        await close();
        if (!copied)
        {
            shell.Notify("Die Zwischenablage ist nicht verfügbar.");
        }
    }

    /// <param name="done">"als Tabelle kopiert" – follows the number of rows in the notice.</param>
    public async Task CopyAsync(ExportText export, string done)
    {
        var copied = await dom.CopyTextAsync(export.Text);
        await close();
        shell.Notify(copied ? $"{RowsText(export.Rows)} {done}." : "Die Zwischenablage ist nicht verfügbar.", Warnings(export));
    }

    /// <param name="fileName">Offered file name with extension.</param>
    /// <param name="filter">Label of the file type in the dialog, e.g. <c>CSV (Semikolon)</c>.</param>
    public async Task SaveAsync(ExportText export, string fileName, string filter, bool utf8Bom)
    {
        await close();
        try
        {
            if (await files.SaveTextAsync(fileName, filter, export.Text, utf8Bom) is { } path)
            {
                shell.Notify($"{RowsText(export.Rows)} gespeichert: {path}", Warnings(export));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            shell.Notify($"Speichern fehlgeschlagen: {ex.Message}");
        }
    }

    private IReadOnlyList<string> Warnings(ExportText export) => missingRows == 0
        ? export.Warnings
        : [.. export.Warnings, $"{RowsText(missingRows)} der Auswahl nicht mehr geladen – nicht exportiert."];
}
