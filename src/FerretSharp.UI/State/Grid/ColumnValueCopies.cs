using FerretSharp.Core.Connections;
using FerretSharp.Core.Data;
using FerretSharp.Core.Query;
using FerretSharp.Core.Resources;
using FerretSharp.Core.Schema;
using FerretSharp.UI.Resources;
using Microsoft.Extensions.Logging;

namespace FerretSharp.UI.State;

/// <summary>
/// All values of a column, read again (WP-57): the table's query with only this column, or the SQL editor's query run once
/// more.
/// </summary>
/// <param name="Heading">Heading of the menu group ("All rows of the filter · 12,345 rows").</param>
/// <param name="Total">Rows expected, if counted.</param>
/// <param name="Read">Reads the rows; true asks for each value once (an IN list), where the query can.</param>
/// <param name="ValueIndex">Where the column's value is in a row read.</param>
/// <param name="Tooltip">Second line of the entries' tooltips: what is read, and what it costs.</param>
public sealed record AllColumnValues(string Heading, long? Total, Func<bool, RowReader> Read, int ValueIndex, string Tooltip);

/// <summary>
/// Copies the values of one column (WP-57): from the selected rows as they are shown (pending values included, A21), or all
/// values of the grid's query, read in the background with the rows in the status bar and copied when the read is done.
/// </summary>
public static class ColumnValueCopies
{
    /// <summary>All values of a table tab's column under its applied filters and in its sort order (not the filter rows being typed).</summary>
    /// <param name="dataOf">The data access of a workspace (<c>WorkspaceManager.GetDataAsync</c>).</param>
    public static AllColumnValues ForTable(
        TableTab tab, TableDetails details, int column, Func<Guid, CancellationToken, Task<IDataAccess>> dataOf)
    {
        var (filters, sorts, workspaceId) = (tab.AppliedFilters, tab.Sorts, tab.WorkspaceId);
        var total = tab.TotalCount ?? (tab.AllRowsLoaded ? tab.RowsLoaded : null);
        return new AllColumnValues(
            Heading(GridText.GridMenu_AllRowsGroup, total),
            total,
            distinct => async (onBlock, ct) => await (await dataOf(workspaceId, ct)).ReadColumnAllAsync(details, column, filters, sorts, distinct, onBlock, ct),
            0,
            TextFormat.Format(GridText.ColumnMenu_AllValuesTitle, ColumnValues.MaxValues));
    }

    /// <summary>
    /// All values of a SQL editor result's column: the query runs again (with every column – wrapping it would fail on the
    /// duplicate names of joins), refused if it now returns other columns.
    /// </summary>
    /// <param name="total">The result's size if all its rows were loaded.</param>
    public static AllColumnValues ForResult(
        QuerySpec query, IReadOnlyList<SqlColumn> columns, int column, long? total, Guid workspaceId,
        Func<Guid, CancellationToken, Task<IDataAccess>> dataOf)
    {
        var names = columns.Select(c => c.Name).ToList();
        return new AllColumnValues(
            Heading(GridText.GridMenu_AllResultRowsGroup, total),
            total,
            _ => async (onBlock, ct) => await (await dataOf(workspaceId, ct)).ReadSqlAllAsync(query, names, onBlock, ct),
            column,
            TextFormat.Format(GridText.ColumnMenu_AllResultValuesTitle, ColumnValues.MaxValues));
    }

    /// <summary>
    /// The values of the selected rows in grid order, as the menu entries and the key copy them; rows no longer loaded are
    /// named as not copied.
    /// </summary>
    /// <param name="column">The column as presented (<see cref="ExportColumn.Source"/>: the index of its value).</param>
    /// <param name="selected">The selected rows in order, pending values applied; null for one no longer loaded.</param>
    /// <param name="copy">Writes the clipboard; false if it is not available.</param>
    public static async Task CopyRowsAsync(
        ShellState shell, Func<string, Task<bool>> copy, ExportColumn column, string columnName, ColumnValuesFormat format,
        IReadOnlyList<RowData?> selected)
    {
        var values = new ColumnValues(column, format);
        values.AddRange(selected.OfType<RowData>().Select(r => r.Values[column.Source]));
        var missing = selected.Count(r => r is null);
        IReadOnlyList<string> extra = missing == 0 ? [] : [TextFormat.Plural(missing, GridText.Grid_SelectedRowsNotCopiedOne, GridText.Grid_SelectedRowsNotCopiedOther)];
        await FinishAsync(shell, copy, values, columnName, extra);
    }

    /// <summary>
    /// All values of a column (<see cref="AllColumnValues"/>): read in the background – the status bar shows the rows and
    /// can cancel – and copied through the host when the read is done (the window may no longer be in front by then). More
    /// than <see cref="ColumnValues.MaxValues"/> values stop the read and copy nothing.
    /// </summary>
    public static async Task CopyAllAsync(
        ShellState shell, IClipboardService clipboard, ILogger logger, ActiveConnection? connection, ExportColumn column, string columnName,
        ColumnValuesFormat format, AllColumnValues all)
    {
        var exports = shell.Exports;
        var job = exports.Start(TextFormat.Format(ShellText.Status_CopyValuesLabel, columnName), all.Total, TextFormat.Format(ShellText.Status_CopyValuesTitle, columnName));
        var progress = new Progress<long>(rows => exports.Report(job, rows)); // reports on the UI thread
        try
        {
            var token = job.Token;
            var values = new ColumnValues(column, format);
            var read = all.Read(format == ColumnValuesFormat.InList);
            var result = await shell.RunDbAsync(logger, connection, async () =>
            {
                long rows = 0;
                await read((block, _, _) =>
                {
                    foreach (var row in block)
                    {
                        if (!values.Add(row.Values[all.ValueIndex]))
                        {
                            return Task.FromResult(false);
                        }
                    }

                    rows += block.Count;
                    ((IProgress<long>)progress).Report(rows);
                    return Task.FromResult(true);
                }, token);
                return values;
            }, token);
            if (result.Value is not null)
            {
                await FinishAsync(shell, clipboard.SetTextAsync, values, columnName, []);
            }
            else if (result.Cancelled)
            {
                shell.Notify(TextFormat.Format(GridText.ColumnValues_Cancelled, columnName));
            }
            else
            {
                shell.ShowFailure(result);
            }
        }
        finally
        {
            exports.End(job);
        }
    }

    private static async Task FinishAsync(
        ShellState shell, Func<string, Task<bool>> copy, ColumnValues values, string columnName, IReadOnlyList<string> extra)
    {
        if (values.TooMany)
        {
            shell.Notify(TextFormat.Format(GridText.ColumnValues_TooMany, ColumnValues.MaxValues), extra);
            return;
        }

        var text = values.Finish();
        IReadOnlyList<string> warnings = [.. text.Warnings, .. extra];
        if (text.Values == 0)
        {
            shell.Notify(TextFormat.Format(GridText.ColumnValues_None, columnName), warnings);
        }
        else if (await copy(text.Text))
        {
            shell.Notify(values.Format == ColumnValuesFormat.InList
                ? TextFormat.Plural(text.Values, GridText.ColumnValues_CopiedInListOne, GridText.ColumnValues_CopiedInListOther, columnName)
                : TextFormat.Plural(text.Values, GridText.ColumnValues_CopiedOne, GridText.ColumnValues_CopiedOther, columnName), warnings);
        }
        else
        {
            shell.Notify(CommonText.ClipboardUnavailable);
        }
    }

    private static string Heading(string format, long? total) =>
        TextFormat.Format(format, total is { } rows ? " · " + ExportActions.RowsText(rows) : "");
}
