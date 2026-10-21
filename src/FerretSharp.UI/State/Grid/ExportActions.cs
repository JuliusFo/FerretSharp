using FerretSharp.Core.Connections;
using FerretSharp.Core.Data;
using FerretSharp.Core.IO;
using FerretSharp.Core.Query;
using FerretSharp.Core.Resources;
using FerretSharp.Core.Schema;
using FerretSharp.UI.Resources;
using Microsoft.Extensions.Logging;

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
/// <param name="logger">The menu's logger: a database error of an export of all rows is logged with it.</param>
/// <param name="connection">The connection an export of all rows reads from (its errors, a lost session).</param>
public sealed class ExportActions(
    ShellState shell, DomModule dom, IFileSaveService files, int missingRows, Func<Task> close, ILogger logger, ActiveConnection? connection)
{
    /// <summary>"1 row", "3 rows" – for headings and notices.</summary>
    public static string RowsText(long rows) => TextFormat.Plural(rows, GridText.Export_RowsOne, GridText.Export_RowsOther);

    /// <summary>"3 rows copied as a table." and the like.</summary>
    public static string CopiedText(ExportCopy kind, int rows) => kind switch
    {
        ExportCopy.Insert => TextFormat.Plural(rows, GridText.Export_CopiedAsInsertOne, GridText.Export_CopiedAsInsertOther),
        ExportCopy.CSharpObjects => TextFormat.Plural(rows, GridText.Export_CopiedAsCSharpObjectsOne, GridText.Export_CopiedAsCSharpObjectsOther),
        ExportCopy.HasData => TextFormat.Plural(rows, GridText.Export_CopiedAsHasDataOne, GridText.Export_CopiedAsHasDataOther),
        _ => TextFormat.Plural(rows, GridText.Export_CopiedAsTableOne, GridText.Export_CopiedAsTableOther),
    };

    /// <summary>
    /// Ctrl+C with several rows selected, in either grid (R5b: written in both): the rows as a table, rows no longer loaded
    /// named as not copied. A missing clipboard says so itself (<see cref="GridBridge.CopyTextAsync"/>).
    /// </summary>
    /// <param name="selected">The selected rows in order; null for one no longer loaded.</param>
    public static async Task CopyRowsAsync(ShellState shell, GridBridge grid, TableDetails table, IReadOnlyList<RowData?> selected)
    {
        var rows = selected.OfType<RowData>().ToList();
        var export = DelimitedExport.Build(table, rows, DelimitedExport.ClipboardSeparator);
        var missing = selected.Count - rows.Count;
        IReadOnlyList<string> warnings = missing == 0
            ? export.Warnings
            : [.. export.Warnings, TextFormat.Plural(missing, GridText.Grid_SelectedRowsNotCopiedOne, GridText.Grid_SelectedRowsNotCopiedOther)];
        if (await grid.CopyTextAsync(shell, export.Text))
        {
            shell.Notify(CopiedText(ExportCopy.Table, rows.Count), warnings);
        }
    }

    /// <summary>Ctrl+C on one cell, in either grid: its full value, and what it lacks (a LOB with only its preview loaded).</summary>
    public static async Task CopyCellAsync(ShellState shell, GridBridge grid, ColumnInfo column, object? value)
    {
        var cell = DelimitedExport.CellText(column, value);
        if (await grid.CopyTextAsync(shell, cell.Text) && cell.Warnings.Count > 0)
        {
            shell.Notify(GridText.Grid_ValueNotCopied, cell.Warnings);
        }
    }

    /// <summary>The full value (like Ctrl+C), not the shortened display text.</summary>
    public async Task CopyValueAsync(ColumnInfo column, object? value)
    {
        var cell = DelimitedExport.CellText(column, value);
        var copied = await dom.CopyTextAsync(cell.Text);
        await close();
        if (!copied)
        {
            shell.Notify(CommonText.ClipboardUnavailable);
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
            shell.Notify(CommonText.ClipboardUnavailable);
        }
    }

    /// <param name="kind">How the rows were copied: picks the notice.</param>
    public async Task CopyAsync(ExportText export, ExportCopy kind)
    {
        var copied = await dom.CopyTextAsync(export.Text);
        await close();
        shell.Notify(copied ? CopiedText(kind, export.Rows) : CommonText.ClipboardUnavailable, Warnings(export.Warnings));
    }

    /// <summary>The values of one column of the selected rows (WP-57); rows no longer loaded are named as not copied.</summary>
    /// <param name="column">The column as presented; <see cref="ExportColumn.Source"/> is the index of its value.</param>
    public async Task CopyColumnAsync(ExportColumn column, string columnName, ColumnValuesFormat format, IReadOnlyList<RowData> rows)
    {
        await ColumnValueCopies.CopyRowsAsync(shell, dom.CopyTextAsync, column, columnName, format, [.. rows, .. Enumerable.Repeat<RowData?>(null, missingRows)]);
        await close();
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
                shell.Notify(TextFormat.Plural(export.Rows, GridText.Export_SavedOne, GridText.Export_SavedOther, path), Warnings(export.Warnings));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            shell.Notify(TextFormat.Format(CommonText.SaveFailed, ex.Message));
        }
    }

    /// <summary>The selected rows as a file (CSV, Excel): <see cref="SaveFileAsync"/>; rows no longer loaded are named as not exported.</summary>
    /// <param name="fileName">Offered file name with extension.</param>
    /// <param name="filter">Label of the file type in the dialog, e.g. <c>Excel workbook</c>.</param>
    public Task SaveRowsAsync(string fileName, string filter, int rows, Func<Stream, IProgress<long>, CancellationToken, Task<ExportSummary>> write) =>
        SaveFileAsync(fileName, filter, rows, write, Warnings);

    /// <summary>All rows of a grid's query as a file (WP-54): <see cref="SaveFileAsync"/>.</summary>
    public Task SaveAllRowsAsync(string fileName, string filter, long? total, Func<Stream, IProgress<long>, CancellationToken, Task<ExportSummary>> write) =>
        SaveFileAsync(fileName, filter, total, write, warnings => warnings);

    /// <summary>
    /// An export to a file (review 3.40.0 A13, WP-54): the native dialog first, then the file is written in the background,
    /// block by block, shown in the status bar with its rows and a cancel button. It goes into a temporary file beside the
    /// target (<see cref="AtomicFile"/>): a cancelled or failed export leaves no half file, an existing one stays as it was.
    /// A database error is logged and shown like any other (<see cref="DbCalls"/>).
    /// </summary>
    /// <param name="total">Rows expected, if known: the status bar shows "x of n".</param>
    /// <param name="write">Writes the file into the stream, reporting the rows written.</param>
    private async Task SaveFileAsync(
        string fileName, string filter, long? total, Func<Stream, IProgress<long>, CancellationToken, Task<ExportSummary>> write,
        Func<IReadOnlyList<string>, IReadOnlyList<string>> warnings)
    {
        await close();
        if (await files.AskPathAsync(fileName, filter) is not { Length: > 0 } path)
        {
            return;
        }

        var exports = shell.Exports;
        var export = exports.Start(Path.GetFileName(path), total);
        var progress = new Progress<long>(rows => exports.Report(export, rows)); // reports on the UI thread
        try
        {
            var token = export.Token;
            var result = await shell.RunDbAsync(logger, connection, () => AtomicFile.WriteExportAsync(path, (stream, ct) => write(stream, progress, ct), token), token);
            if (result.Value is { } summary)
            {
                foreach (var failure in summary.Failures)
                {
                    QueryErrorLog.Log(logger, failure, connection?.Profile); // names left out: the toast says so, the log says why
                }

                shell.Notify(TextFormat.Plural(summary.Rows, GridText.Export_SavedOne, GridText.Export_SavedOther, path), warnings(summary.Warnings));
            }
            else if (result.Cancelled)
            {
                shell.Notify(TextFormat.Format(GridText.Export_Cancelled, export.Label));
            }
            else
            {
                shell.ShowFailure(result);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            shell.Notify(TextFormat.Format(CommonText.SaveFailed, ex.Message));
        }
        finally
        {
            exports.End(export);
        }
    }

    private IReadOnlyList<string> Warnings(IReadOnlyList<string> warnings) => missingRows == 0
        ? warnings
        : [.. warnings, TextFormat.Format(GridText.Export_MissingRows, RowsText(missingRows))];
}
