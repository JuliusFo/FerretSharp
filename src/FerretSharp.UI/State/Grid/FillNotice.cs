using FerretSharp.Core.Data;
using FerretSharp.Core.Resources;
using FerretSharp.UI.Resources;

namespace FerretSharp.UI.State;

/// <summary>The status bar after a command for many cells (WP-59): how many were set, then what was left out and why.</summary>
public static class FillNotice
{
    public static string Text(CellFillResult result)
    {
        var sentences = new List<string>
        {
            result.Cells == 0 ? GridText.Fill_NothingSet : TextFormat.Plural(result.Cells, GridText.Fill_CellsSetOne, GridText.Fill_CellsSetOther),
        };
        if (result.DeletedRows > 0)
        {
            sentences.Add(TextFormat.Plural(result.DeletedRows, GridText.Fill_DeletedRowsOne, GridText.Fill_DeletedRowsOther));
        }

        if (result.TooPrecise > 0)
        {
            sentences.Add(TextFormat.Plural(result.TooPrecise, GridText.Fill_TooPreciseOne, GridText.Fill_TooPreciseOther));
        }

        sentences.AddRange(result.Columns.Select(c => TextFormat.Format(GridText.Fill_ColumnLeft, c.Name, c.Reason)));
        return string.Join(" ", sentences);
    }
}
