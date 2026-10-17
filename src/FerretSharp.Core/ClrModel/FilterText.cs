using FerretSharp.Core.Query;

namespace FerretSharp.Core.ClrModel;

/// <summary>
/// A filter row as text, <c>KUNDENART = Gewerbe</c> – the one way for the tab header, the preset tooltip, the links and the
/// notes of the LINQ export (review 3.40.0, A31 and B11: four copies, and only the tab header read enum and bool values as
/// members of the C# model).
/// </summary>
public static class FilterText
{
    /// <param name="presentation">The table's C# model; null shows the values as they are (what a link carries).</param>
    public static string Row(FilterCondition filter, TablePresentation? presentation = null)
    {
        ArgumentNullException.ThrowIfNull(filter);
        return $"{filter.Column} {OperatorLabels.Label(filter.Op)} {string.Join("; ", filter.Values.Select(v => presentation?.FilterValueText(filter.Column, v) ?? v))}".TrimEnd();
    }
}
