using FerretSharp.Core.ClrModel;
using FerretSharp.Core.Query;
using FerretSharp.Core.Resources;
using FerretSharp.UI.Resources;

namespace FerretSharp.UI.State;

/// <summary>A preset picked in the menu of the filter bar: replace the filter rows with it, or add its rows (WP-37).</summary>
public sealed record PresetChoice(FilterPreset Preset, bool Append);

/// <summary>How a filter preset reads in its tooltip and in the save dialog (WP-37).</summary>
public static class FilterPresetText
{
    /// <summary>
    /// One line per filter row, as in the tab header with <paramref name="presentation"/>; disabled rows marked as such,
    /// rows of a group (WP-50) with the group's kind and number ("ODER-Gruppe 1: …"), a disabled group's as disabled.
    /// Without a presentation (the link dialog, the note on Prod values) the values stand as the link carries them.
    /// </summary>
    public static IEnumerable<string> FilterLines(IReadOnlyList<FilterCondition> filters, TablePresentation? presentation = null)
    {
        var groups = Core.Query.FilterGroups.Of(filters);
        var numbers = new Dictionary<int, int>();
        foreach (var f in filters)
        {
            var text = FilterText.Row(f, presentation);
            if (f.Group is { } own)
            {
                var group = groups[own.Id];
                if (!numbers.TryGetValue(group.Id, out var number))
                {
                    numbers[group.Id] = number = numbers.Count + 1;
                }

                text = TextFormat.Format(GridText.Presets_GroupRow, FilterGroupLabels.Label(group.Kind), number, text);
            }

            yield return Core.Query.FilterGroups.IsActive(f, groups) ? text : TextFormat.Format(GridText.Presets_Off, text);
        }
    }

    /// <summary><c>ERSTELLT ↓, ID ↑</c></summary>
    public static string SortText(IReadOnlyList<SortSpec> sorts) =>
        string.Join(", ", sorts.Select(s => $"{s.Column} {(s.Descending ? "↓" : "↑")}"));

    /// <summary>The tooltip of a preset in the menu: its filter rows, then its sort order.</summary>
    public static string Describe(FilterPreset preset, TablePresentation? presentation = null)
    {
        var lines = FilterLines(preset.Filters, presentation).ToList();
        if (lines.Count == 0)
        {
            lines.Add(GridText.Presets_NoFilterRows);
        }

        if (preset.Sorts.Count > 0)
        {
            lines.Add(TextFormat.Format(GridText.Presets_SortedBy, SortText(preset.Sorts)));
        }

        return string.Join("\n", lines);
    }
}
