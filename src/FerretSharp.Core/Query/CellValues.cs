using System.Text;
using FerretSharp.Core.Data;

namespace FerretSharp.Core.Query;

/// <param name="Rows">Rows in the text, the header line not counted.</param>
/// <param name="Warnings">Values not exported (a LOB with only its preview loaded and the like).</param>
public sealed record CellValuesText(string Text, int Rows, IReadOnlyList<string> Warnings);

/// <summary>
/// The cells of a range or of marked columns as text for the clipboard (WP-58), collected row by row: one line per row, the
/// cells separated by tabs as the grid presents them (an enum's member), in the notation of Ctrl+C, NULL as an empty field;
/// optionally a header line with the columns' headers. A cell holding a line break, tab or quote is quoted like Excel reads
/// it – one column gives the text of <see cref="ColumnValues"/> in <see cref="ColumnValuesFormat.Lines"/>. Up to
/// <see cref="ColumnValues.MaxValues"/> cells and <see cref="ColumnValues.MaxCharacters"/> characters: beyond that nothing
/// is copied, a cut range would look complete.
/// </summary>
/// <param name="columns">The columns in the order of the text; <see cref="ExportColumn.Source"/> is the index of a row's value.</param>
public sealed class CellValues(IReadOnlyList<ExportColumn> columns, bool header)
{
    private readonly StringBuilder _text = new();
    private readonly ExportWarnings _warnings = new();

    /// <summary>Whether a range of that size can be copied at all (checked before reading rows not loaded).</summary>
    public static bool Fits(long rows, int columns) => rows * columns <= ColumnValues.MaxValues;

    /// <summary>Rows collected so far.</summary>
    public int Rows { get; private set; }

    /// <summary>There were more than <see cref="ColumnValues.MaxValues"/> cells or <see cref="ColumnValues.MaxCharacters"/> characters.</summary>
    public bool TooMany { get; private set; }

    /// <summary>It was the characters, not the number of cells.</summary>
    public bool TooLong { get; private set; }

    /// <param name="values">A row's values; the columns pick theirs by <see cref="ExportColumn.Source"/>.</param>
    /// <returns>False once there are too many cells or characters: stop reading.</returns>
    public bool AddRow(IReadOnlyList<object?> values)
    {
        if (TooMany)
        {
            return false;
        }

        if (!Fits(Rows + 1, columns.Count) || _text.Length > ColumnValues.MaxCharacters)
        {
            TooMany = true;
            TooLong = Fits(Rows + 1, columns.Count);
            _text.Clear();
            return false;
        }

        if (Rows == 0 && header)
        {
            AppendFields(columns.Select(c => c.Header));
            _text.Append("\r\n");
        }

        if (Rows > 0)
        {
            _text.Append("\r\n");
        }

        Rows++;
        AppendFields(columns.Select(c =>
        {
            var value = values[c.Source];
            return ExportNotation.Text(c.Column, c.Present is { } present ? present(value) : value, _warnings) ?? "";
        }));
        return true;
    }

    /// <returns>False once there are too many cells or characters.</returns>
    public bool AddRows(IEnumerable<IReadOnlyList<object?>> rows)
    {
        foreach (var row in rows)
        {
            if (!AddRow(row))
            {
                return false;
            }
        }

        return true;
    }

    /// <exception cref="InvalidOperationException">There were too many cells or characters.</exception>
    public CellValuesText Finish()
    {
        if (TooMany)
        {
            throw new InvalidOperationException("Too many cells to copy.");
        }

        return new CellValuesText(_text.ToString(), Rows, _warnings.ToList(fractions: true));
    }

    private void AppendFields(IEnumerable<string> fields)
    {
        var first = true;
        foreach (var field in fields)
        {
            if (!first)
            {
                _text.Append(DelimitedExport.ClipboardSeparator);
            }

            first = false;
            DelimitedExport.AppendField(_text, field, DelimitedExport.ClipboardSeparator);
        }
    }
}
