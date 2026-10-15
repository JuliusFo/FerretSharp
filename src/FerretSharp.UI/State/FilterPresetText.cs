using FerretSharp.Core.Query;
using FerretSharp.Core.Resources;
using FerretSharp.UI.Resources;

namespace FerretSharp.UI.State;

/// <summary>A preset picked in the menu of the filter bar: replace the filter rows with it, or add its rows (WP-37).</summary>
public sealed record PresetChoice(FilterPreset Preset, bool Append);

/// <summary>How a filter preset reads in its tooltip and in the save dialog (WP-37).</summary>
public static class FilterPresetText
{
    /// <summary>One line per filter row, as in the tab header; disabled rows marked as such.</summary>
    public static IEnumerable<string> FilterLines(IReadOnlyList<FilterCondition> filters) => filters.Select(f =>
    {
        var text = $"{f.Column} {OperatorLabels.Label(f.Op)} {string.Join("; ", f.Values)}".TrimEnd();
        return f.Enabled ? text : TextFormat.Format(GridText.Presets_Off, text);
    });

    /// <summary><c>ERSTELLT ↓, ID ↑</c></summary>
    public static string SortText(IReadOnlyList<SortSpec> sorts) =>
        string.Join(", ", sorts.Select(s => $"{s.Column} {(s.Descending ? "↓" : "↑")}"));

    /// <summary>The tooltip of a preset in the menu: its filter rows, then its sort order.</summary>
    public static string Describe(FilterPreset preset)
    {
        var lines = FilterLines(preset.Filters).ToList();
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
