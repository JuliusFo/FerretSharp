using FerretSharp.Core.Query;
using FerretSharp.Core.Settings;

namespace FerretSharp.UI.State;

/// <summary>One opening of the quick open box (a class: each opening is a new one, the box starts fresh).</summary>
/// <param name="commands">Opened as command palette: the text starts with the command prefix.</param>
public sealed class PaletteRequest(bool commands)
{
    public string InitialText { get; } = commands ? QuickPaletteSearch.CommandPrefix : "";

    /// <summary>
    /// What the box shows now – the command palette or the quick open; the box keeps it current as the text changes, so
    /// the key of the mode shown closes it (<see cref="ShellState.TogglePalette"/>).
    /// </summary>
    public bool ShowsCommands { get; set; } = commands;
}

/// <summary>An action in the command palette: what it is, its key (null if none) and why it cannot run now (null if it can).</summary>
public sealed record PaletteCommand(ShortcutDefinition Definition, string? Key, string? Unavailable);

/// <summary>
/// What the quick open box (WP-38) searches: without a prefix tables, views and PL/SQL of the shown connection
/// (<see cref="ObjectSearch"/>), with <see cref="CommandPrefix"/> in front the actions (command palette). One box for both,
/// decision of the user: typing or deleting the prefix switches.
/// </summary>
public static class QuickPaletteSearch
{
    public const string CommandPrefix = ">";

    /// <summary>At most this many objects per group; more means the search should be narrowed.</summary>
    public const int Limit = 50;

    /// <summary>The text starts with the command prefix (leading spaces ignored).</summary>
    public static bool IsCommand(string? text) => (text ?? "").TrimStart().StartsWith(CommandPrefix, StringComparison.Ordinal);

    /// <summary>The query without the command prefix.</summary>
    public static string QueryOf(string? text)
    {
        var trimmed = (text ?? "").Trim();
        return IsCommand(trimmed) ? trimmed[CommandPrefix.Length..].Trim() : trimmed;
    }

    /// <summary>
    /// The palette's actions the query finds by label, group or key (<see cref="TextSearch"/>), in the order of the
    /// settings page; all without a query.
    /// </summary>
    /// <param name="keyOf">The key label of an action in effect; null if it has none.</param>
    public static IReadOnlyList<PaletteCommand> Commands(string? query, ShellActionContext context, Func<ShortcutAction, string?> keyOf)
    {
        var search = new TextSearch(query);
        return
        [
            .. ShellActions.InPalette
                .Select(d => new PaletteCommand(d, keyOf(d.Action), ShellActions.Unavailable(d.Action, context)))
                .Where(c => search.Matches(c.Definition.Label, c.Definition.Group, c.Key)),
        ];
    }
}
