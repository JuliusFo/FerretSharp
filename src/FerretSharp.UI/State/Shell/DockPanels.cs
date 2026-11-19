using FerretSharp.Core.Settings;
using FerretSharp.Core.Workspaces;
using FerretSharp.UI.Resources;

namespace FerretSharp.UI.State;

/// <summary>
/// The panels of the dock on the right (WP-92, ADR 0052): their order in the dock's bar and where they show. The dock is the
/// window's – its panel stays open when another tab is activated; a tab without that panel shows none, and the bar keeps the
/// choice for the next tab that has it.
/// </summary>
public static class DockPanels
{
    /// <summary>The buttons of the dock's bar, top to bottom.</summary>
    public static IReadOnlyList<DockPanel> InBar { get; } = [DockPanel.Inspector, DockPanel.Form, DockPanel.Changes, DockPanel.Help];

    /// <summary>The panel shows beside <paramref name="tab"/>; form and inspector need a grid of the tab.</summary>
    public static bool ShowsIn(DockPanel panel, WorkspaceTab? tab) => Unavailable(panel, tab) is null;

    /// <summary>Why the panel shows nothing beside the active tab (a tooltip of the bar); null if it shows.</summary>
    public static string? Unavailable(DockPanel panel, WorkspaceTab? tab) => panel switch
    {
        DockPanel.Inspector => tab is TableTab { Mode: TabMode.Data } ? null : ShellText.Dock_InspectorUnavailable,
        DockPanel.Form => tab is TableTab { Mode: TabMode.Data } or SqlTab or LinqTab ? null : ShellText.Dock_FormUnavailable,
        _ => null,
    };

    /// <summary>The shortcut that opens or closes the panel, for its tooltip.</summary>
    public static ShortcutAction ActionOf(DockPanel panel) => panel switch
    {
        DockPanel.Inspector => ShortcutAction.ToggleInspector,
        DockPanel.Form => ShortcutAction.ToggleForm,
        DockPanel.Changes => ShortcutAction.ToggleChanges,
        _ => ShortcutAction.ScriptHelp,
    };

    public static string LabelOf(DockPanel panel) => panel switch
    {
        DockPanel.Inspector => ShellText.Dock_Inspector,
        DockPanel.Form => ShellText.Dock_Form,
        DockPanel.Changes => ShellText.Changes_Title,
        _ => ShellText.Dock_Help,
    };
}
