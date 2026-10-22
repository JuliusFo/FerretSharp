using FerretSharp.Core.Connections;
using FerretSharp.Core.Data;
using FerretSharp.Core.Query;
using FerretSharp.Core.Resources;
using FerretSharp.UI.Resources;
using Microsoft.Extensions.Logging;

namespace FerretSharp.UI.State;

/// <summary>How marked cells are copied (WP-58).</summary>
public enum MarksCopy
{
    /// <summary>Tab-separated, without a header line (Ctrl+C).</summary>
    Cells,

    /// <summary>With a header line ("Copy as table").</summary>
    Table,

    /// <summary>The database values of one column as an IN list.</summary>
    InList,
}

/// <summary>
/// What a grid hands over to copy its marked cells (WP-58): its columns as presented, its loaded rows and a way to read the
/// rest – a range can reach rows whose block was never loaded (Shift+click far down) or was dropped from the cache – and, for
/// whole columns, all their values read again.
/// </summary>
/// <param name="ColumnOf">A grid column as presented; <see cref="ExportColumn.Source"/> is its index in a row's values.</param>
/// <param name="RowAt">A loaded row by grid index, pending values applied as shown (A21); null if its block is not loaded.</param>
/// <param name="ReadRows">
/// Reads rows from a grid index on (start, count), pending values applied; null if the read failed – the grid has said why.
/// </param>
/// <param name="AllOf">All values of whole columns, read again; null where the grid cannot read all rows (LINQ console).</param>
/// <param name="PendingIn">Rows with pending changes of the columns: a read of all values does not have them.</param>
public sealed record MarkedCells(
    Func<int, ExportColumn> ColumnOf,
    Func<int, RowData?> RowAt,
    Func<int, int, Task<IReadOnlyList<RowData>?>> ReadRows,
    Func<IReadOnlyList<int>, AllColumnValues>? AllOf,
    Func<IReadOnlyList<int>, int> PendingIn);

/// <summary>
/// Copies the marked cells of a grid (WP-58): a rectangle as shown – pending values included (A21), rows not loaded read
/// first – or whole columns, all their rows read again in the background like the values of a column (WP-57), with a note
/// on pending changes the read does not have. Up to <see cref="ColumnValues.MaxValues"/> cells; beyond that nothing.
/// </summary>
public static class CellCopies
{
    /// <summary>
    /// Rows read at most at a time for a range's rows not loaded: a gap is read in one query where it can (roundtrips over
    /// the VPN are what costs), not block by block.
    /// </summary>
    public const int ReadSize = 5000;

    /// <param name="copy">Writes the clipboard from the page (a range: the window is in front); false if it is not available.</param>
    /// <param name="clipboard">Writes the clipboard through the host after a read in the background (whole columns).</param>
    public static async Task CopyAsync(
        ShellState shell, Func<string, Task<bool>> copy, IClipboardService clipboard, ILogger logger, ActiveConnection? connection,
        MarkedCells grid, GridMarks marks, MarksCopy kind)
    {
        var columns = marks.ColumnIndexes;
        if (columns.Count == 0 || (kind == MarksCopy.InList && columns.Count != 1))
        {
            return; // an IN list is offered for one column only
        }

        if (marks.Whole)
        {
            await CopyColumnsAsync(shell, clipboard, logger, connection, grid, marks, columns, kind);
            return;
        }

        if (!CellValues.Fits(marks.Rows, columns.Count))
        {
            shell.Notify(TextFormat.Format(GridText.Marks_TooMany, ColumnValues.MaxValues));
            return;
        }

        if (await RowsAsync(grid.RowAt, grid.ReadRows, marks.FromRow, marks.ToRow) is not { } rows)
        {
            return;
        }

        if (kind == MarksCopy.InList)
        {
            var column = grid.ColumnOf(columns[0]);
            await ColumnValueCopies.CopyRowsAsync(shell, copy, column, column.Column.Name, ColumnValuesFormat.InList, rows, 0);
            return;
        }

        var cells = new CellValues([.. columns.Select(grid.ColumnOf)], kind == MarksCopy.Table);
        cells.AddRows(rows.Select(r => r.Values));
        await FinishAsync(shell, copy, cells, marks, kind, []);
    }

    /// <summary>
    /// The rows of a range: loaded ones as they are, each gap of rows not loaded read at once (up to <see cref="ReadSize"/>).
    /// Null if a read failed. A read that returns fewer rows than asked for ends the range there (the result shrank meanwhile).
    /// Also for changing marked cells (WP-59, <see cref="GridEditing"/>), there with the rows as loaded.
    /// </summary>
    /// <param name="rowAt">A loaded row by grid index; null if its block is not loaded.</param>
    /// <param name="read">Reads rows from a grid index on (start, count); null if the read failed.</param>
    internal static async Task<IReadOnlyList<RowData>?> RowsAsync(
        Func<int, RowData?> rowAt, Func<int, int, Task<IReadOnlyList<RowData>?>> read, int from, int to)
    {
        var rows = new List<RowData>(to - from + 1);
        for (var i = from; i <= to;)
        {
            if (rowAt(i) is { } row)
            {
                rows.Add(row);
                i++;
                continue;
            }

            var count = 1;
            while (i + count <= to && count < ReadSize && rowAt(i + count) is null)
            {
                count++;
            }

            if (await read(i, count) is not { } block)
            {
                return null;
            }

            rows.AddRange(block);
            if (block.Count < count)
            {
                break;
            }

            i += count;
        }

        return rows;
    }

    /// <summary>Whole columns: all their rows read again in the background, copied through the host when the read is done.</summary>
    private static async Task CopyColumnsAsync(
        ShellState shell, IClipboardService clipboard, ILogger logger, ActiveConnection? connection, MarkedCells grid, GridMarks marks,
        IReadOnlyList<int> columns, MarksCopy kind)
    {
        if (grid.AllOf is not { } allOf)
        {
            return;
        }

        if (columns.Any(c => !ColumnValues.CanCopy(grid.ColumnOf(c).Column)))
        {
            shell.Notify(GridText.GridMenu_CopyColumnUnavailable); // a table's read selects only values (BuildColumnValues)
            return;
        }

        var all = allOf(columns);
        var extra = ColumnValueCopies.PendingLeftOut(grid.PendingIn(columns));
        var presented = columns.Select((c, i) => grid.ColumnOf(c) with { Source = all.ValueIndexes[i] }).ToList();
        if (kind == MarksCopy.InList)
        {
            await ColumnValueCopies.CopyAllAsync(shell, clipboard, logger, connection, presented[0], presented[0].Column.Name, ColumnValuesFormat.InList, all, extra);
            return;
        }

        var cells = new CellValues(presented, kind == MarksCopy.Table);
        var read = all.Read(false);
        var label = marks.Text;
        var result = await shell.RunInBackgroundAsync(logger, connection,
            TextFormat.Format(ShellText.Status_CopyValuesLabel, label), all.Total, TextFormat.Format(ShellText.Status_CopyValuesTitle, label), all.WorkspaceId,
            async (progress, token) =>
            {
                long rows = 0;
                await read((block, _, _) =>
                {
                    foreach (var row in block)
                    {
                        if (!cells.AddRow(row.Values))
                        {
                            return Task.FromResult(false);
                        }
                    }

                    rows += block.Count;
                    progress.Report(rows);
                    return Task.FromResult(true);
                }, token);
                return cells;
            });
        if (result.Value is not null)
        {
            await FinishAsync(shell, clipboard.SetTextAsync, cells, marks, kind, extra);
        }
        else if (result.Cancelled)
        {
            shell.Notify(TextFormat.Format(GridText.Marks_Cancelled, label));
        }
        else
        {
            shell.ShowFailure(result);
        }
    }

    private static async Task FinishAsync(
        ShellState shell, Func<string, Task<bool>> copy, CellValues cells, GridMarks marks, MarksCopy kind, IReadOnlyList<string> extra)
    {
        if (cells.TooMany)
        {
            shell.Notify(cells.TooLong
                ? TextFormat.Format(GridText.ColumnValues_TooLong, ColumnValues.MaxCharacters)
                : TextFormat.Format(GridText.Marks_TooMany, ColumnValues.MaxValues), extra);
            return;
        }

        var text = cells.Finish();
        IReadOnlyList<string> warnings = [.. text.Warnings, .. extra];
        if (text.Rows == 0)
        {
            shell.Notify(TextFormat.Format(GridText.Marks_None, marks.Text), warnings);
        }
        else if (!await copy(text.Text))
        {
            shell.Notify(CommonText.ClipboardUnavailable);
        }
        else if (marks.Whole)
        {
            shell.Notify(TextFormat.Format(kind == MarksCopy.Table ? GridText.Marks_ColumnsCopiedAsTable : GridText.Marks_ColumnsCopied,
                marks.Text, ExportActions.RowsText(text.Rows)), warnings);
        }
        else
        {
            shell.Notify(TextFormat.Format(kind == MarksCopy.Table ? GridText.Marks_CopiedAsTable : GridText.Marks_Copied, marks.Text), warnings);
        }
    }
}
