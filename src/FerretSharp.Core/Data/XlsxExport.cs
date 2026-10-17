using System.Globalization;
using FerretSharp.Core.Resources;
using FerretSharp.Core.Schema;
using SpreadCheetah;
using SpreadCheetah.Styling;
using SpreadCheetah.Worksheets;

namespace FerretSharp.Core.Data;

/// <param name="Content">The file's bytes.</param>
/// <param name="Warnings">Values that could not be exported completely (LOBs only loaded as preview, texts shortened …).</param>
public sealed record ExportFile(byte[] Content, int Rows, IReadOnlyList<string> Warnings);

/// <summary>
/// One column of an Excel export (WP-53): its header and where its values come from. The plain database view is
/// <see cref="Raw"/>; the presentation layer adds C# names, enum members and the names beside foreign keys
/// (<c>PresentedExport</c>, <c>SqlResultPresentation</c>).
/// </summary>
/// <param name="Source">Index of the row value the column shows.</param>
/// <param name="Column">The column the value belongs to (its type decides how dates are written).</param>
public sealed record ExportColumn(string Header, int Source, ColumnInfo Column)
{
    /// <summary>
    /// What is written instead of the value: a text (an enum member, a looked-up name), a bool, or the value itself if the
    /// presentation has nothing for it. Null writes the value as it is.
    /// </summary>
    public Func<object?, object?>? Present { get; init; }

    /// <summary>The table's columns with their database names and raw values.</summary>
    public static IReadOnlyList<ExportColumn> Raw(TableDetails table) =>
        [.. table.Columns.Select((c, i) => new ExportColumn(c.Name, i, c))];

    /// <summary>Whether the columns show anything else than <see cref="Raw"/> would: only then is the raw variant offered.</summary>
    public static bool DiffersFromRaw(IReadOnlyList<ExportColumn> columns) =>
        columns.Where((c, i) => c.Present is not null || c.Source != i || c.Header != c.Column.Name).Any();
}

/// <summary>
/// Rows as an Excel file (WP-53, ADR 0028) with typed cells: numbers as numbers, DATE/TIMESTAMP as dates, text as text
/// (never as formula), NULL as empty cell. What Excel would change is written as text in the notation of the CSV export:
/// numbers with more than 15 significant digits, timestamps finer than milliseconds, dates before 1 March 1900, values with
/// time zone. Header row bold and frozen, auto filter, column widths by content.
/// </summary>
public static class XlsxExport
{
    /// <summary>Rows of a sheet including the header row.</summary>
    public const int MaxSheetRows = 1_048_576;

    /// <summary>Characters a cell holds.</summary>
    public const int MaxCellText = 32_767;

    /// <summary>Significant digits Excel keeps of a number (it stores a double and shows 15 digits).</summary>
    public const int MaxDigits = 15;

    /// <summary>Column widths in characters of the standard font.</summary>
    public const double MinWidth = 6;
    public const double MaxWidth = 60;

    /// <summary>Earliest date Excel shows correctly: it counts from 1900 and has a 29 February 1900 that never was.</summary>
    private static readonly DateTime FirstDate = new(1900, 3, 1);

    private static readonly CultureInfo German = ValueCulture.German;

    /// <param name="sheetName">Name of the sheet, made valid (<see cref="SheetName"/>).</param>
    public static async Task<ExportFile> BuildAsync(
        IReadOnlyList<ExportColumn> columns, IReadOnlyList<RowData> rows, string sheetName, CancellationToken cancellationToken = default)
    {
        var warnings = new ExportWarnings();
        var count = Math.Min(rows.Count, MaxSheetRows - 1);
        if (count < rows.Count)
        {
            warnings.Note(TextFormat.Format(DataText.ExportSheetRowLimit, count.ToString("N0", CultureInfo.CurrentCulture)));
        }

        using var stream = new MemoryStream();
        await using (var spreadsheet = await Spreadsheet.CreateNewAsync(stream, new SpreadCheetahOptions { PreserveStringWhitespace = true }, cancellationToken))
        {
            // Styles first: the cells are built before the sheet starts, because their widths decide the column widths.
            var styles = new Styles(spreadsheet);
            var widths = columns.Select(c => c.Header.Length + 3.0).ToArray(); // + the filter button
            var cells = new List<Cell[]>(count);
            foreach (var row in rows.Take(count))
            {
                var line = new Cell[columns.Count];
                for (var i = 0; i < columns.Count; i++)
                {
                    (line[i], var width) = CellOf(columns[i], row.Values[columns[i].Source], styles, warnings);
                    widths[i] = Math.Max(widths[i], width);
                }

                cells.Add(line);
            }

            var options = new WorksheetOptions { FrozenRows = 1 };
            if (columns.Count > 0)
            {
                options.AutoFilter = new AutoFilterOptions($"A1:{ColumnLetters(columns.Count)}{count + 1}");
            }

            for (var i = 0; i < columns.Count; i++)
            {
                options.Column(i + 1).Width = Math.Clamp(widths[i] + 1, MinWidth, MaxWidth);
            }

            await spreadsheet.StartWorksheetAsync(SheetName(sheetName), options, cancellationToken);
            await spreadsheet.AddRowAsync([.. columns.Select(c => new Cell(c.Header, styles.Header))], cancellationToken);
            foreach (var line in cells)
            {
                await spreadsheet.AddRowAsync(line, cancellationToken);
            }

            await spreadsheet.FinishAsync(cancellationToken);
        }

        return new ExportFile(stream.ToArray(), count, warnings.ToList());
    }

    /// <summary>The cell for a value and the width it needs in characters.</summary>
    private static (Cell Cell, double Width) CellOf(ExportColumn column, object? value, Styles styles, ExportWarnings warnings)
    {
        var shown = column.Present is { } present ? present(value) : value;
        return shown switch
        {
            null or DBNull => (new Cell((string?)null), 0),
            string s => Text(column.Column, s, warnings),
            decimal d => Number(d, styles),
            int or long or short => Number(Convert.ToDecimal(shown, CultureInfo.InvariantCulture), styles),
            BigNumber big => Text(column.Column, big.Invariant.Replace('.', ','), warnings),
            double d when double.IsFinite(d) => (new Cell(d), d.ToString("R", German).Length),
            // float → double directly would write 0.1 as 0.100000001490116.
            float f when float.IsFinite(f) => (new Cell(double.Parse(f.ToString("R", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture)), f.ToString("R", German).Length),
            DateTimeOffset dto => Text(column.Column, dto.ToString("dd.MM.yyyy HH:mm:ss.FFFFFFF zzz", CultureInfo.InvariantCulture), warnings),
            DateTime dt => Date(column.Column, dt, styles, warnings),
            bool b => (new Cell(b), 5),
            byte[] bytes => Text(column.Column, Convert.ToHexString(bytes), warnings),
            LobValue { Preview: { } preview } clob when preview.Length == clob.Length => Text(column.Column, preview, warnings),
            LobValue lob => Skip(column.Column, warnings, lob.Preview is null ? DataText.ExportReasonOnlyLength : DataText.ExportReasonOnlyPreview),
            NotNullMarker => Skip(column.Column, warnings, DataText.ExportReasonTypeNotExported),
            _ => Text(column.Column, Convert.ToString(shown, CultureInfo.InvariantCulture) ?? "", warnings), // NaN, intervals …
        };
    }

    private static (Cell, double) Skip(ColumnInfo column, ExportWarnings warnings, string reason)
    {
        warnings.Skip(column, reason);
        return (new Cell((string?)null), 0);
    }

    /// <summary>A text cell; longer than a cell holds is shortened with a warning. Its width is that of the first line.</summary>
    private static (Cell, double) Text(ColumnInfo column, string text, ExportWarnings warnings)
    {
        if (text.Length > MaxCellText)
        {
            warnings.Truncate(column);
            text = text[..MaxCellText];
        }

        var end = text.AsSpan().IndexOfAny('\r', '\n');
        return (new Cell(text), end >= 0 ? end : text.Length);
    }

    /// <summary>
    /// A number as number if Excel keeps it exactly (at most 15 significant digits), else as text in German notation.
    /// Whole numbers get the format <c>0</c>: "General" would show a 12-digit key as <c>1,23457E+11</c>.
    /// </summary>
    private static (Cell, double) Number(decimal value, Styles styles)
    {
        var invariant = value.ToString(CultureInfo.InvariantCulture);
        if (SignificantDigits(invariant) > MaxDigits)
        {
            return (new Cell(value.ToString(German)), invariant.Length);
        }

        return value == decimal.Truncate(value)
            ? (new Cell(value, styles.WholeNumber), decimal.Truncate(value).ToString(CultureInfo.InvariantCulture).Length)
            : (new Cell(value), invariant.Length);
    }

    /// <summary>Significant digits of an invariant number text: without sign, point, leading and trailing zeros.</summary>
    internal static int SignificantDigits(string invariant)
    {
        var digits = invariant.Where(char.IsAsciiDigit).SkipWhile(c => c == '0').ToList();
        var trailing = Enumerable.Reverse(digits).TakeWhile(c => c == '0').Count();
        return digits.Count - trailing;
    }

    /// <summary>
    /// DATE to the second, TIMESTAMP to the millisecond, as the grid shows them. A finer timestamp or a date before
    /// 1 March 1900 becomes text in the notation of the CSV export.
    /// </summary>
    private static (Cell, double) Date(ColumnInfo column, DateTime value, Styles styles, ExportWarnings warnings)
    {
        var isDate = ColumnCategories.Of(column) == ColumnCategory.Date;
        if (value < FirstDate || (!isDate && value.Ticks % TimeSpan.TicksPerMillisecond != 0))
        {
            return Text(column, value.ToString(isDate ? "dd.MM.yyyy HH:mm:ss" : "dd.MM.yyyy HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture), warnings);
        }

        return isDate ? (new Cell(value, styles.Date), 19) : (new Cell(value, styles.Timestamp), 23);
    }

    /// <summary>A1 column letters: 1 → A, 27 → AA.</summary>
    internal static string ColumnLetters(int column)
    {
        var letters = "";
        for (var n = column; n > 0; n = (n - 1) / 26)
        {
            letters = (char)('A' + (n - 1) % 26) + letters;
        }

        return letters;
    }

    /// <summary>A valid sheet name: at most 31 characters, none of <c>[ ] : * ? / \</c>, no quote at either end.</summary>
    internal static string SheetName(string name)
    {
        var cleaned = new string([.. name.Select(c => c is '[' or ']' or ':' or '*' or '?' or '/' or '\\' ? '_' : c)]).Trim().Trim('\'');
        if (cleaned.Length > 31)
        {
            cleaned = cleaned[..31].TrimEnd('\'', ' ');
        }

        return cleaned.Length == 0 ? "Export" : cleaned;
    }

    /// <summary>
    /// The styles of a file. Dates have their dots escaped: unescaped, Excel reads a dot in a format as the decimal separator
    /// and shows a comma under German settings. The point before the milliseconds is one.
    /// </summary>
    private sealed class Styles(Spreadsheet spreadsheet)
    {
        public StyleId Header { get; } = spreadsheet.AddStyle(new Style { Font = { Bold = true } });

        public StyleId WholeNumber { get; } = spreadsheet.AddStyle(new Style { Format = NumberFormat.Custom("0") });

        public StyleId Date { get; } = spreadsheet.AddStyle(new Style { Format = NumberFormat.Custom(@"dd\.mm\.yyyy hh:mm:ss") });

        public StyleId Timestamp { get; } = spreadsheet.AddStyle(new Style { Format = NumberFormat.Custom(@"dd\.mm\.yyyy hh:mm:ss.000") });
    }
}
