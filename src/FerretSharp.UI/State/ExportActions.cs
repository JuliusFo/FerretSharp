using FerretSharp.Core.Data;
using FerretSharp.Core.Resources;
using FerretSharp.Core.Schema;
using FerretSharp.UI.Resources;

namespace FerretSharp.UI.State;

/// <summary>How rows were copied: picks the whole notice ("3 rows copied as a table."), one sentence per language.</summary>
public enum ExportCopy
{
    Table,
    Insert,
    CSharpObjects,
    HasData,
}

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
    /// <summary>"1 row", "3 rows" – for headings and notices.</summary>
    public static string RowsText(int rows) => TextFormat.Plural(rows, GridText.Export_RowsOne, GridText.Export_RowsOther);

    /// <summary>"3 rows copied as a table." and the like.</summary>
    public static string CopiedText(ExportCopy kind, int rows) => kind switch
    {
        ExportCopy.Insert => TextFormat.Plural(rows, GridText.Export_CopiedAsInsertOne, GridText.Export_CopiedAsInsertOther),
        ExportCopy.CSharpObjects => TextFormat.Plural(rows, GridText.Export_CopiedAsCSharpObjectsOne, GridText.Export_CopiedAsCSharpObjectsOther),
        ExportCopy.HasData => TextFormat.Plural(rows, GridText.Export_CopiedAsHasDataOne, GridText.Export_CopiedAsHasDataOther),
        _ => TextFormat.Plural(rows, GridText.Export_CopiedAsTableOne, GridText.Export_CopiedAsTableOther),
    };

    /// <summary>The full value (like Ctrl+C), not the shortened display text.</summary>
    public async Task CopyValueAsync(ColumnInfo column, object? value)
    {
        var cell = DelimitedExport.CellText(column, value);
        var copied = await dom.CopyTextAsync(cell.Text);
        await close();
        if (!copied)
        {
            shell.Notify(GridText.Export_ClipboardUnavailable);
        }
        else if (cell.Warnings.Count > 0)
        {
            shell.Notify(GridText.Grid_ValueNotCopied, cell.Warnings); // a LOB with only its preview loaded, a type that is not exported
        }
    }

    /// <summary>Copies any text (a C# value); only a missing clipboard is worth a word.</summary>
    public async Task CopyTextAsync(string text)
    {
        var copied = await dom.CopyTextAsync(text);
        await close();
        if (!copied)
        {
            shell.Notify(GridText.Export_ClipboardUnavailable);
        }
    }

    /// <param name="kind">How the rows were copied: picks the notice.</param>
    public async Task CopyAsync(ExportText export, ExportCopy kind)
    {
        var copied = await dom.CopyTextAsync(export.Text);
        await close();
        shell.Notify(copied ? CopiedText(kind, export.Rows) : GridText.Export_ClipboardUnavailable, Warnings(export));
    }

    /// <summary>
    /// Transitional (WP-29): the result menu of the SQL editor still passes the German end of the notice. Its two known
    /// texts get the localized notice; switch the callers to <see cref="CopyAsync(ExportText, ExportCopy)"/> and remove this.
    /// </summary>
    /// <param name="done">"als Tabelle kopiert" – follows the number of rows in the notice.</param>
    public Task CopyAsync(ExportText export, string done) => done switch
    {
        "als Tabelle kopiert" => CopyAsync(export, ExportCopy.Table),
        "als INSERT kopiert" => CopyAsync(export, ExportCopy.Insert),
        _ => CopyGluedAsync(export, done),
    };

    private async Task CopyGluedAsync(ExportText export, string done)
    {
        var copied = await dom.CopyTextAsync(export.Text);
        await close();
        shell.Notify(copied ? $"{RowsText(export.Rows)} {done}." : GridText.Export_ClipboardUnavailable, Warnings(export));
    }

    /// <param name="fileName">Offered file name with extension.</param>
    /// <param name="filter">Label of the file type in the dialog, e.g. <c>CSV (semicolon)</c>.</param>
    public async Task SaveAsync(ExportText export, string fileName, string filter, bool utf8Bom)
    {
        await close();
        try
        {
            if (await files.SaveTextAsync(fileName, filter, export.Text, utf8Bom) is { } path)
            {
                shell.Notify(TextFormat.Plural(export.Rows, GridText.Export_SavedOne, GridText.Export_SavedOther, path), Warnings(export));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            shell.Notify(TextFormat.Format(GridText.Export_SaveFailed, ex.Message));
        }
    }

    private IReadOnlyList<string> Warnings(ExportText export) => missingRows == 0
        ? export.Warnings
        : [.. export.Warnings, TextFormat.Format(GridText.Export_MissingRows, RowsText(missingRows))];
}
