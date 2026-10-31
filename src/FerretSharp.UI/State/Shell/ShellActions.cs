using FerretSharp.Core.Resources;
using FerretSharp.Core.Settings;
using FerretSharp.UI.Resources;

namespace FerretSharp.UI.State;

/// <summary>What decides whether a global action can run now; the shell fills it from its state.</summary>
/// <param name="InExplorer">The explorer with the tabs is shown (not settings, a comparison, the connections).</param>
/// <param name="Connected">The shown connection is connected.</param>
/// <param name="ActiveTab">The active tab of the shown connection's active workspace.</param>
/// <param name="Writable">The active workspace may write (not read-only, or unlocked).</param>
/// <param name="LinkedProject">The shown connection has a C# project (LINQ console).</param>
/// <param name="CanGoBack">An FK jump to go back from (<see cref="ShellState.BackTarget"/>).</param>
/// <param name="CanGoForward">A tab to go forward to (<see cref="ShellState.ForwardTarget"/>).</param>
/// <param name="PreviousConnection">The connection shown before is still open.</param>
public sealed record ShellActionContext(
    bool InExplorer,
    bool Connected,
    WorkspaceTab? ActiveTab,
    bool Writable,
    bool LinkedProject,
    bool CanGoBack,
    bool CanGoForward,
    bool PreviousConnection);

/// <summary>
/// When the global actions can run (WP-38): one rule for their shortcuts and the command palette, which greys out what
/// would do nothing now and says why.
/// </summary>
public static class ShellActions
{
    /// <summary>
    /// The actions of the command palette, in the order of the settings page: the global ones without the palette's own.
    /// Those of the grid and the form need the focus there and are left out.
    /// </summary>
    public static IReadOnlyList<ShortcutDefinition> InPalette =>
        [.. ShortcutMap.Definitions.Where(d => d.Scope == ShortcutScope.Global && d.Action is not (ShortcutAction.QuickOpen or ShortcutAction.CommandPalette))];

    /// <summary>Why the action cannot run now; null if it can.</summary>
    public static string? Unavailable(ShortcutAction action, ShellActionContext context) => action switch
    {
        ShortcutAction.ConnectionSwitcher or ShortcutAction.CommandPalette => null,
        ShortcutAction.QuickOpen => context.Connected ? null : WorkspaceText.NoActiveConnection,
        ShortcutAction.PreviousConnection => context.PreviousConnection ? null : ShellText.Palette_NoPreviousConnection,
        ShortcutAction.Back => InExplorer(context) ?? (context.CanGoBack ? null : ShellText.Palette_NothingToGoBackTo),
        ShortcutAction.Forward => InExplorer(context) ?? (context.CanGoForward ? null : ShellText.Palette_NothingToGoForwardTo),
        ShortcutAction.CloseTab or ShortcutAction.CloseOtherTabs or ShortcutAction.CloseAllTabs or ShortcutAction.PinTab => WithTab(context),
        ShortcutAction.ApplyFilters => WithTab(context) ?? (context.ActiveTab is TableTab or SqlTab or LinqTab ? null : ShellText.Palette_NotInThisTab),
        ShortcutAction.Refresh =>
            WithTab(context) ?? (context.ActiveTab is TableTab or SqlTab or LinqTab or PlSqlTab or RecordTab or SynonymListTab ? null : ShellText.Palette_NotInThisTab),
        ShortcutAction.FindColumn =>
            WithTab(context) ?? (context.ActiveTab is TableTab or SqlTab or LinqTab or PlSqlTab ? null : ShellText.Palette_NotInThisTab),
        ShortcutAction.RunScript => WithTab(context) ?? (context.ActiveTab is SqlTab ? null : ShellText.Palette_OnlyInSqlTab),
        ShortcutAction.DuplicateTab => WithTab(context) ?? (context.ActiveTab is TableTab ? null : ShellText.Palette_NotInThisTab),
        ShortcutAction.RevealInExplorer => WithTab(context) ?? (context.ActiveTab is TableTab or PlSqlTab ? null : ShellText.Palette_NotInThisTab),
        ShortcutAction.NewSql or ShortcutAction.ToggleChanges or ShortcutAction.SchemaSynonyms => InExplorer(context),
        ShortcutAction.NewLinq => InExplorer(context) ?? (context.LinkedProject ? null : ShellText.Palette_NoLinkedProject),
        ShortcutAction.SwitchWorkClone => !context.Connected ? WorkspaceText.NoActiveConnection : context.LinkedProject ? null : ShellText.Palette_NoLinkedProject,
        ShortcutAction.Flush or ShortcutAction.Commit => InExplorer(context) ?? (context.Writable ? null : ShellText.Palette_ReadOnly),
        // The grid's and the form's own keys never reach the shell (not global) and are not in the palette.
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Not a global action."),
    };

    private static string? InExplorer(ShellActionContext context) =>
        !context.Connected ? WorkspaceText.NoActiveConnection
        : !context.InExplorer ? ShellText.Palette_OnlyInExplorer
        : null;

    private static string? WithTab(ShellActionContext context) =>
        InExplorer(context) ?? (context.ActiveTab is null ? ShellText.Palette_NoTab : null);
}
