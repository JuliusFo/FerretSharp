using FerretSharp.Core.Data;
using FerretSharp.Core.Display;

namespace FerretSharp.Core.ClrModel;

/// <summary>
/// The columns of an Excel export as the grid presents them (WP-53, ADR 0028): the property name as header if C# names
/// are shown, enum and bool members by name, and behind a foreign key with a display column (WP-31) a column with the
/// names the grid has looked up. The values themselves stay typed: a foreign key stays a number.
/// </summary>
public static class PresentedExport
{
    /// <param name="names">The names the grid has looked up; <see cref="DisplayNames.None"/> adds no name columns.</param>
    public static IReadOnlyList<ExportColumn> Columns(TablePresentation presentation, DisplayNames names)
    {
        var columns = new List<ExportColumn>();
        var details = presentation.Details;
        for (var i = 0; i < details.Columns.Count; i++)
        {
            var column = Column(presentation, i, details.Columns[i].Name, i);
            columns.Add(column);
            if (names.Shows(i) && names.Links.FirstOrDefault(l => l.Column == i) is { } link)
            {
                var source = i;
                columns.Add(new ExportColumn($"{column.Header} · {link.Label}", i, link.Target.Columns[link.Parts[0].Column])
                {
                    Present = value => names.NameOf(source, value)?.Text,
                });
            }
        }

        return columns;
    }

    /// <summary>
    /// One column of a table as presented: header and values. <paramref name="name"/> is the header without C# names (a
    /// query result keeps the name the query gave the column); <paramref name="source"/> the index of its row value.
    /// </summary>
    public static ExportColumn Column(TablePresentation presentation, int column, string name, int source)
    {
        var header = presentation.ShowsClrNames && presentation.PropertyOf(column) is { } property ? property.Name : name;
        var values = presentation.ValuesOf(column);
        return new ExportColumn(header, source, presentation.Details.Columns[column])
        {
            Present = values is null ? null : value => Value(values, value),
        };
    }

    /// <summary>
    /// What the grid shows for an enum or bool value: the member (<c>Gewerbe (2)</c>), a bool as bool. A value without a
    /// member stays the value itself, so a number stays a number.
    /// </summary>
    public static object? Value(ValueTable values, object? value)
    {
        var presented = values.Present(value);
        if (presented.Text is null || presented.Unknown)
        {
            return value;
        }

        return values.IsBool && bool.TryParse(presented.Text, out var flag) ? flag : presented.Text;
    }
}
