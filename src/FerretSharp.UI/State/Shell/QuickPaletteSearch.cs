using FerretSharp.Core.Query;
using FerretSharp.Core.Schema;
using FerretSharp.Core.Settings;
using FerretSharp.Core.Workspaces;

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

/// <summary>A table, view or PL/SQL unit the empty quick open offers (WP-91): exactly one of the two is set.</summary>
public sealed record PaletteObject(TableSummary? Table = null, PlSqlObjectSummary? Unit = null)
{
    public string DisplayName => Table?.DisplayName ?? Unit!.DisplayName;
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

    /// <summary>At most this many favorites and recently opened objects in the empty box (WP-91).</summary>
    public const int StartLimit = 10;

    /// <summary>
    /// The favorites the schema has, tables and PL/SQL together by the name shown (WP-91), and how many more there are than
    /// shown ("N more", review WP-91, 4).
    /// </summary>
    public static (IReadOnlyList<PaletteObject> Shown, int More) Favorites(
        IReadOnlyList<TableSummary> tables, IReadOnlyList<PlSqlObjectSummary> units, Func<TableSummary, bool> isTable, Func<PlSqlObjectSummary, bool> isUnit)
    {
        List<PaletteObject> all =
        [
            .. tables.Where(isTable).Select(t => new PaletteObject(Table: t))
                .Concat(units.Where(isUnit).Select(u => new PaletteObject(Unit: u)))
                .OrderBy(o => o.DisplayName, StringComparer.Ordinal),
        ];
        return ([.. all.Take(StartLimit)], Math.Max(0, all.Count - StartLimit));
    }

    /// <summary>
    /// The objects last opened that the schema still has, newest first, without those <paramref name="skip"/> leaves out
    /// (the open tabs, shown above them).
    /// </summary>
    public static IReadOnlyList<PaletteObject> Recent(
        IReadOnlyList<RecentObject> recent, Func<TableRef, TableSummary?> findTable, Func<PlSqlRef, PlSqlObjectSummary?> findUnit, Func<PaletteObject, bool> skip) =>
    [
        .. recent.Select(r => SchemaObjects.PlSqlKindOf(r.Kind) is { } kind
                ? findUnit(new PlSqlRef(r.Owner, r.Name, kind)) is { } unit ? new PaletteObject(Unit: unit) : null
                : findTable(new TableRef(r.Owner, r.Name)) is { } table ? new PaletteObject(Table: table) : null)
            .OfType<PaletteObject>()
            .Where(o => !skip(o))
            .Take(StartLimit),
    ];
}
