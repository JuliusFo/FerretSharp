using FerretSharp.Core.Workspaces;
using FerretSharp.UI.Resources;

namespace FerretSharp.UI.State;

/// <summary>The views of a table tab with their labels, in the order of the tab's view switch (R6b, B11: written twice).</summary>
public static class TabModeLabels
{
    // A property, not a static field: the labels follow the UI language.
    public static IReadOnlyList<(TabMode Mode, string Label)> All =>
    [
        (TabMode.Data, SchemaViewText.Tab_ViewData),
        (TabMode.Structure, SchemaViewText.Tab_ViewColumns),
        (TabMode.Constraints, SchemaViewText.Tab_ViewConstraints),
        (TabMode.Indexes, SchemaViewText.Tab_ViewIndexes),
        (TabMode.Dependencies, SchemaViewText.Tab_ViewDependencies),
        (TabMode.Ddl, SchemaViewText.Tab_ViewDdl),
    ];

    public static string Of(TabMode mode) => All.FirstOrDefault(m => m.Mode == mode).Label ?? SchemaViewText.Tab_ViewData;
}
