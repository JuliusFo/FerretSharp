using System.Text;
using FerretSharp.Core.Data;
using FerretSharp.Core.Resources;
using FerretSharp.Core.Schema;

namespace FerretSharp.Core.Query;

/// <summary>How the values of a column are copied (WP-57).</summary>
public enum ColumnValuesFormat
{
    /// <summary>One value per line as the grid presents it (an enum's member), NULL as an empty line.</summary>
    Lines,

    /// <summary>Oracle literals for an IN list (<c>'A', 'B'</c>): database values, each once, without NULL.</summary>
    InList,
}

/// <param name="Values">Values in the text: lines, or the distinct values of the IN list.</param>
/// <param name="Warnings">NULLs left out of the IN list, a list longer than Oracle takes, values not exported.</param>
public sealed record ColumnValuesText(string Text, int Values, IReadOnlyList<string> Warnings);

/// <summary>
/// The values of one column as text for the clipboard (WP-57), collected value by value – from the selected rows or block by
/// block from the database – up to <see cref="MaxValues"/>: beyond that nothing is copied, a cut list (an IN list above all)
/// would look complete. Lines use the notation of Ctrl+C, a line holding a line break, tab or quote is quoted like Excel reads it.
/// </summary>
public sealed class ColumnValues(ExportColumn column, ColumnValuesFormat format)
{
    /// <summary>More values than this are not copied: the export as CSV or Excel is the way for them.</summary>
    public const int MaxValues = 100_000;

    private readonly StringBuilder _text = new();
    private readonly ExportWarnings _warnings = new();
    private readonly HashSet<string> _literals = new(StringComparer.Ordinal);
    private int _nulls;

    /// <summary>Whether the values of a column can be copied: not for LOB, LONG and types read only as a marker.</summary>
    public static bool CanCopy(ColumnInfo column) => QueryBuilder.ProjectionOf(column) == Projection.Value;

    public ColumnValuesFormat Format => format;

    /// <summary>
    /// The values came each once from the database (<c>SELECT DISTINCT</c>): all NULLs are one row, so the IN list's note
    /// on them gives no number.
    /// </summary>
    public bool NullsCollapsed { get; init; }

    /// <summary>Values collected so far (for an IN list: distinct ones).</summary>
    public int Count { get; private set; }

    /// <summary>There were more than <see cref="MaxValues"/> values.</summary>
    public bool TooMany { get; private set; }

    /// <returns>False once there are more than <see cref="MaxValues"/> values: stop reading.</returns>
    public bool Add(object? value)
    {
        if (TooMany)
        {
            return false;
        }

        if (format == ColumnValuesFormat.InList)
        {
            if (value is null or DBNull)
            {
                _nulls++;
                return true;
            }

            // Without the CHAR padding (review 3.44.0, A8): it finds the rows in its own column all the same (blank-padded
            // comparison), and in a VARCHAR2 column too – a relationship without a constraint, as FkNavigation.FilterValue.
            if (value is string text && OracleTypes.IsFixedChar(column.Column.DataType) && text.TrimEnd(' ') is { Length: > 0 } trimmed)
            {
                value = trimmed;
            }

            var literal = InsertExport.Literal(column.Column, value, _warnings);
            if (!_literals.Add(literal))
            {
                return true;
            }

            if (!Counted())
            {
                return false;
            }

            _text.Append(Count == 1 ? "" : ", ").Append(literal);
            return true;
        }

        if (!Counted())
        {
            return false;
        }

        var shown = column.Present is { } present ? present(value) : value;
        if (Count > 1)
        {
            _text.Append("\r\n");
        }

        DelimitedExport.AppendField(_text, ExportNotation.Text(column.Column, shown, _warnings) ?? "", DelimitedExport.ClipboardSeparator);
        return true;
    }

    /// <returns>False once there are more than <see cref="MaxValues"/> values.</returns>
    public bool AddRange(IEnumerable<object?> values)
    {
        foreach (var value in values)
        {
            if (!Add(value))
            {
                return false;
            }
        }

        return true;
    }

    /// <exception cref="InvalidOperationException">There were more than <see cref="MaxValues"/> values.</exception>
    public ColumnValuesText Finish()
    {
        if (TooMany)
        {
            throw new InvalidOperationException("Too many values to copy.");
        }

        var warnings = _warnings.ToList(fractions: format != ColumnValuesFormat.InList);
        if (format == ColumnValuesFormat.InList)
        {
            if (_nulls > 0)
            {
                warnings.Add(NullsCollapsed
                    ? QueryText.ColumnValuesNullsLeftOut
                    : TextFormat.Plural(_nulls, QueryText.ColumnValuesNullsLeftOutOne, QueryText.ColumnValuesNullsLeftOutOther));
            }

            // TIMESTAMP(8) and (9) are read as DateTime: a literal of a value with finer fractions does not find its row.
            if (Count > 0 && ColumnCategories.LosesFractionsWhenRead(column.Column))
            {
                warnings.Add(TextFormat.Format(QueryText.ColumnValuesFractionsCut, column.Column.DataType, ColumnCategories.ReadFractionalDigits));
            }

            if (Count > QueryBuilder.MaxInListSize)
            {
                warnings.Add(TextFormat.Format(QueryText.ColumnValuesInListTooLong, QueryBuilder.MaxInListSize));
            }
        }

        return new ColumnValuesText(_text.ToString(), Count, warnings);
    }

    private bool Counted()
    {
        if (Count == MaxValues)
        {
            TooMany = true;
            _text.Clear();
            return false;
        }

        Count++;
        return true;
    }
}
