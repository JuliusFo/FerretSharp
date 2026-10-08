using FerretSharp.Core.ClrModel;
using FerretSharp.Core.Data;
using FerretSharp.Core.Oracle;
using FerretSharp.Core.Schema;
using FerretSharp.Core.Workspaces;

namespace FerretSharp.Core.Forms;

/// <summary>One value of a compared row.</summary>
/// <param name="Deviates">
/// Differs from the most frequent value of the column (on a tie: from the first row's) – the cell to mark. With two rows
/// the second one is marked; with three, only the odd one out.
/// </param>
public sealed record CompareCell(PresentedValue Value, object? Raw, ChangeStage? Stage, bool Deviates = false)
{
    public bool IsNull => Raw is null;
}

/// <summary>A column across the compared rows.</summary>
/// <param name="Cells">One per row, in the order of the rows.</param>
/// <param name="Differs">Not all rows hold the same value (compared on the raw values, as Oracle would).</param>
public sealed record CompareField(int Column, ColumnInfo Info, ColumnLabel Label, IReadOnlyList<CompareCell> Cells, bool Differs)
{
    public bool AllNull => Cells.All(c => c.IsNull);
}

/// <summary>What the comparison shows: the search and its switches.</summary>
public sealed record CompareFilter(string? Query = null, bool HideEmpty = false, bool OnlyDifferences = false);

/// <summary>
/// Selected rows side by side (WP-21, read-only): which columns differ. Compared on the current values (pending and
/// flushed changes laid over, like the grid shows them), not on display texts – the building block the audit mode
/// of the backlog needs as well. LOBs are known only as preview and length: equal previews of equal length count as
/// equal.
/// </summary>
public static class RowComparison
{
    /// <summary>At most this many rows side by side; more get unreadable even with scrolling.</summary>
    public const int MaxRows = 20;

    public static IReadOnlyList<CompareField> Compare(
        TableDetails table, TablePresentation presentation, IReadOnlyList<FormRow> rows, IReadOnlyList<string> pinned) =>
        ColumnPinning.DisplayOrder(table, pinned)
            .Select(i =>
            {
                var values = rows.Select(r => r.ValueOf(i)).ToList();
                var common = MostFrequent(values);
                var cells = values
                    .Select((raw, r) => new CompareCell(presentation.Present(i, raw), raw, rows[r].Change?.StageOf(i), !OracleTypeMapper.ValuesEqual(common, raw)))
                    .ToList();
                return new CompareField(i, table.Columns[i], presentation.LabelOf(i), cells, cells.Any(c => c.Deviates));
            })
            .ToList();

    /// <summary>The value most rows hold; on a tie the one that occurs first. Few rows: pairwise is fine.</summary>
    private static object? MostFrequent(IReadOnlyList<object?> values)
    {
        object? best = null;
        var bestCount = 0;
        foreach (var value in values)
        {
            var count = values.Count(v => OracleTypeMapper.ValuesEqual(value, v));
            if (count > bestCount)
            {
                (best, bestCount) = (value, count);
            }
        }

        return best;
    }

    /// <summary>The columns matching the search and the switches; "leere" are columns NULL in every row.</summary>
    public static FormView<CompareField> Visible(IReadOnlyList<CompareField> fields, TablePresentation presentation, CompareFilter filter)
    {
        var found = RowForm.Matches(presentation, filter.Query);
        var matching = fields.Where(f => found.Contains(f.Column) && (!filter.OnlyDifferences || f.Differs)).ToList();
        if (!filter.HideEmpty)
        {
            return new FormView<CompareField>(matching, fields.Count, 0);
        }

        var shown = matching.Where(f => !f.AllNull).ToList();
        return new FormView<CompareField>(shown, fields.Count, matching.Count - shown.Count);
    }
}
