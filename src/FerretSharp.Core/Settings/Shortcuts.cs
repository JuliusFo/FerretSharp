using FerretSharp.Core.Resources;

namespace FerretSharp.Core.Settings;

/// <summary>What a changeable shortcut does (WP-25). The names are the keys in <c>settings.json</c> – do not rename.</summary>
public enum ShortcutAction
{
    ConnectionSwitcher,
    PreviousConnection,
    Back,
    Forward,
    ApplyFilters,
    Refresh,
    FindColumn,
    ToggleForm,
    FormPrevious,
    FormNext,
    Flush,
    Commit,
    UndoEdit,
    RedoEdit,
    NewSql,
    NewLinq,
    RunScript,
}

/// <summary>Where a shortcut is caught.</summary>
public enum ShortcutScope
{
    /// <summary>Everywhere, before the grid and the editors (<c>shortcuts.js</c>, capture phase).</summary>
    Global,

    /// <summary>In the grid only (<c>grid.js</c>).</summary>
    Grid,

    /// <summary>In the grid and the form beside it.</summary>
    GridAndForm,

    /// <summary>In the form only.</summary>
    Form,
}

/// <summary>A changeable shortcut: its default and how the settings page shows it.</summary>
public sealed record ShortcutDefinition(ShortcutAction Action, string Group, string Label, string Default, ShortcutScope Scope, string? Hint = null)
{
    public KeyChord DefaultChord => KeyChord.Parse(Default)!;
}

/// <summary>A shortcut that cannot be changed – listed so the settings page is the one overview of all keys.</summary>
public sealed record FixedShortcut(string Group, string Label, string Keys, string? Hint = null);

/// <summary>Why a key combination cannot be used for an action, or what it would take from somewhere else.</summary>
/// <param name="Error">Not usable at all (taken by Windows, would block typing).</param>
/// <param name="Conflict">Another action has it; taking it over leaves that one without a shortcut.</param>
/// <param name="Warning">Usable, but something else loses the key (the editors' or the grid's own keys).</param>
public sealed record ShortcutCheck(string? Error, ShortcutAction? Conflict, string? Warning)
{
    public bool IsFine => Error is null && Conflict is null && Warning is null;
}

/// <summary>
/// The shortcuts in effect: the defaults plus the user's changes from <see cref="AppSettings.Shortcuts"/> (WP-25). Only
/// differences are stored, keyed by the action's name; an empty value means "no shortcut". Entries this version does
/// not know (from a newer one) or cannot parse stay in the dictionary untouched and are otherwise ignored.
/// </summary>
public sealed class ShortcutMap
{
    /// <summary>The changeable shortcuts; a property, not a field: labels and groups follow the UI language.</summary>
    public static IReadOnlyList<ShortcutDefinition> Definitions =>
    [
        new(ShortcutAction.ConnectionSwitcher, SettingsText.GroupNavigation, SettingsText.ActionConnectionSwitcher, "ctrl+shift+o", ShortcutScope.Global),
        new(ShortcutAction.PreviousConnection, SettingsText.GroupNavigation, SettingsText.ActionPreviousConnection, "alt+o", ShortcutScope.Global, SettingsText.HintPreviousConnection),
        new(ShortcutAction.Back, SettingsText.GroupNavigation, SettingsText.ActionBack, "alt+arrowleft", ShortcutScope.Global),
        new(ShortcutAction.Forward, SettingsText.GroupNavigation, SettingsText.ActionForward, "alt+arrowright", ShortcutScope.Global),
        new(ShortcutAction.ApplyFilters, SettingsText.GroupData, SettingsText.ActionApplyFilters, "ctrl+enter", ShortcutScope.Global, SettingsText.HintRunInEditors),
        new(ShortcutAction.Refresh, SettingsText.GroupData, SettingsText.ActionRefresh, "f5", ShortcutScope.Global, SettingsText.HintRunInEditors),
        new(ShortcutAction.FindColumn, SettingsText.GroupData, SettingsText.ActionFindColumn, "ctrl+f", ShortcutScope.Global, SettingsText.HintFindColumn),
        new(ShortcutAction.ToggleForm, SettingsText.GroupData, SettingsText.ActionToggleForm, "alt+enter", ShortcutScope.GridAndForm, SettingsText.HintToggleForm),
        new(ShortcutAction.FormPrevious, SettingsText.GroupData, SettingsText.ActionFormPrevious, "alt+arrowup", ShortcutScope.Form),
        new(ShortcutAction.FormNext, SettingsText.GroupData, SettingsText.ActionFormNext, "alt+arrowdown", ShortcutScope.Form),
        new(ShortcutAction.Flush, SettingsText.GroupEditing, SettingsText.ActionFlush, "ctrl+s", ShortcutScope.Global, SettingsText.HintFlush),
        new(ShortcutAction.Commit, SettingsText.GroupEditing, SettingsText.ActionCommit, "ctrl+shift+enter", ShortcutScope.Global, SettingsText.HintCommit),
        new(ShortcutAction.UndoEdit, SettingsText.GroupEditing, SettingsText.ActionUndoEdit, "ctrl+z", ShortcutScope.Grid, SettingsText.HintEditHistory),
        new(ShortcutAction.RedoEdit, SettingsText.GroupEditing, SettingsText.ActionRedoEdit, "ctrl+y", ShortcutScope.Grid, SettingsText.HintEditHistory),
        new(ShortcutAction.NewSql, SettingsText.GroupSqlAndLinq, SettingsText.ActionNewSql, "ctrl+shift+q", ShortcutScope.Global),
        new(ShortcutAction.NewLinq, SettingsText.GroupSqlAndLinq, SettingsText.ActionNewLinq, "ctrl+shift+l", ShortcutScope.Global, SettingsText.HintNewLinq),
        new(ShortcutAction.RunScript, SettingsText.GroupSqlAndLinq, SettingsText.ActionRunScript, "alt+x", ShortcutScope.Global, SettingsText.HintRunScript),
    ];

    /// <summary>The fixed shortcuts; a property for the same reason as <see cref="Definitions"/>.</summary>
    public static IReadOnlyList<FixedShortcut> Fixed =>
    [
        new(SettingsText.GroupData, SettingsText.FixedCopyValue, "Ctrl+C", SettingsText.HintInGrid),
        new(SettingsText.GroupData, SettingsText.FixedMarkForDeletion, KeyChord.Parse("delete")!.Label, SettingsText.HintInGrid),
        new(SettingsText.GroupData, SettingsText.FixedEditCell, "Enter", SettingsText.HintInGridDoubleClick),
        new(SettingsText.GroupData, SettingsText.FixedCancelInput, "Esc", SettingsText.HintInGrid),
        new(SettingsText.GroupData, SettingsText.ActionApplyFilters, "Enter", SettingsText.HintInFilterValue),
        new(SettingsText.GroupSqlAndLinq, SettingsText.FixedSuggestions, KeyChord.Parse("ctrl+space")!.Label, SettingsText.HintInEditor),
        new(SettingsText.GroupSqlAndLinq, SettingsText.FixedReplace, "Ctrl+H", SettingsText.HintInEditor),
        new(SettingsText.GroupSqlAndLinq, SettingsText.FixedUndoRedo, "Ctrl+Z, Ctrl+Y", SettingsText.HintInEditorNotWritten),
        new(SettingsText.GroupGeneral, SettingsText.FixedClose, "Esc"),
    ];

    /// <summary>Why a key combination is taken by Windows or the WebView before FerretSharp sees it; null if it is not.</summary>
    private static string? Reserved(string combo) => combo switch
    {
        "alt+f4" => SettingsText.ReservedAltF4,
        "alt+space" => SettingsText.ReservedAltSpace,
        "alt+tab" => SettingsText.ReservedAltTab,
        "ctrl+escape" => SettingsText.ReservedCtrlEsc,
        "ctrl+shift+escape" => SettingsText.ReservedCtrlShiftEsc,
        "f12" => TextFormat.Format(SettingsText.ReservedDevTools, "F12"),
        "ctrl+shift+i" => TextFormat.Format(SettingsText.ReservedDevTools, "Ctrl+Shift+I"),
        _ => null,
    };

    /// <summary>What a key does in the editors (Monaco) or the grid, which a global shortcut would take away; null if nothing.</summary>
    private static string? TakenFromEditors(string combo) => combo switch
    {
        "ctrl+c" => SettingsText.EditorCopy,
        "ctrl+v" => SettingsText.EditorPaste,
        "ctrl+x" => SettingsText.EditorCut,
        "ctrl+a" => SettingsText.EditorSelectAll,
        "ctrl+z" => SettingsText.EditorUndo,
        "ctrl+y" => SettingsText.EditorRedo,
        "ctrl+space" => SettingsText.EditorSuggestions,
        "ctrl+h" => SettingsText.EditorReplace,
        "ctrl+d" => SettingsText.EditorSelectNextMatch,
        "ctrl+g" => SettingsText.EditorGoToLine,
        "ctrl+shift+k" => SettingsText.EditorDeleteLine,
        "alt+arrowup" => SettingsText.EditorMoveLineUp,
        "alt+arrowdown" => SettingsText.EditorMoveLineDown,
        "delete" => SettingsText.GridDeleteRows,
        _ => null,
    };

    private readonly Dictionary<ShortcutAction, KeyChord?> _chords = [];

    public ShortcutMap(ShortcutOverrides? overrides = null)
    {
        Overrides = overrides ?? ShortcutOverrides.Empty;
        foreach (var definition in Definitions)
        {
            _chords[definition.Action] = definition.DefaultChord;
            if (Overrides.TryGetValue(definition.Action.ToString(), out var stored))
            {
                if (stored.Length == 0)
                {
                    _chords[definition.Action] = null;
                }
                else if (KeyChord.Parse(stored) is { } chord && Check(definition.Action, chord, ignoreConflicts: true).Error is null)
                {
                    _chords[definition.Action] = chord;
                }
            }
        }
    }

    /// <summary>The stored differences, as given (including entries this version ignores).</summary>
    public ShortcutOverrides Overrides { get; }

    public static ShortcutDefinition Definition(ShortcutAction action) => Definitions.First(d => d.Action == action);

    /// <summary>The action's key combination; null if the user removed it.</summary>
    public KeyChord? Chord(ShortcutAction action) => _chords[action];

    public bool IsDefault(ShortcutAction action) => _chords[action] == Definition(action).DefaultChord;

    public bool HasChanges => Definitions.Any(d => !IsDefault(d.Action));

    /// <summary>The text forms <c>shortcuts.js</c> catches everywhere.</summary>
    public IReadOnlyList<string> GlobalCombos =>
        [.. Definitions.Where(d => d.Scope == ShortcutScope.Global).Select(d => _chords[d.Action]?.Text).OfType<string>()];

    /// <summary>The action a key combination triggers where it was caught; with stored duplicates the first one wins.</summary>
    public ShortcutAction? ActionFor(KeyChord? chord, params ShortcutScope[] scopes) =>
        chord is null
            ? null
            : Definitions.FirstOrDefault(d => scopes.Contains(d.Scope) && _chords[d.Action] == chord)?.Action;

    public ShortcutAction? ActionFor(string combo, params ShortcutScope[] scopes) => ActionFor(KeyChord.Parse(combo), scopes);

    /// <summary>Whether <paramref name="chord"/> may become the shortcut of <paramref name="action"/>.</summary>
    public ShortcutCheck Check(ShortcutAction action, KeyChord chord) => Check(action, chord, ignoreConflicts: false);

    private ShortcutCheck Check(ShortcutAction action, KeyChord chord, bool ignoreConflicts)
    {
        if (Reserved(chord.Text) is { } reserved)
        {
            return new ShortcutCheck(reserved, null, null);
        }

        if (chord.Ctrl && chord.Alt)
        {
            return new ShortcutCheck(TextFormat.Format(SettingsText.CtrlAltIsAltGr), null, null);
        }

        if (!chord.Ctrl && !chord.Alt && !chord.IsFunctionKey)
        {
            return new ShortcutCheck(SettingsText.NeedsCtrlOrAlt, null, null);
        }

        var conflict = ignoreConflicts ? null : Definitions.FirstOrDefault(d => d.Action != action && _chords[d.Action] == chord)?.Action;
        var warning = Definition(action).Scope == ShortcutScope.Global && TakenFromEditors(chord.Text) is { } taken
            ? TextFormat.Format(SettingsText.TakenFromEditor, chord.Label, taken)
            : chord.Shift && chord.Alt && !chord.Ctrl
                ? SettingsText.AltShiftSwitchesLanguage
                : null;
        return new ShortcutCheck(null, conflict, warning);
    }

    /// <summary>
    /// The stored differences after giving <paramref name="action"/> the chord (null: no shortcut). If another action had
    /// it, that one loses it. Unknown entries stay.
    /// </summary>
    public ShortcutOverrides With(ShortcutAction action, KeyChord? chord)
    {
        var result = Overrides.ToDictionary();
        if (chord is not null && Definitions.FirstOrDefault(d => d.Action != action && _chords[d.Action] == chord) is { } other)
        {
            Store(result, other.Action, null);
        }

        Store(result, action, chord);
        return ShortcutOverrides.From(result);
    }

    /// <summary>The stored differences without the one of <paramref name="action"/> (back to its default).</summary>
    public ShortcutOverrides Without(ShortcutAction action)
    {
        var result = Overrides.ToDictionary();
        result.Remove(action.ToString());
        return ShortcutOverrides.From(result);
    }

    /// <summary>All defaults again; unknown entries (of a newer version) stay.</summary>
    public ShortcutOverrides Reset() =>
        ShortcutOverrides.From(Overrides.Where(e => !Enum.GetNames<ShortcutAction>().Contains(e.Key)));

    private static void Store(Dictionary<string, string> overrides, ShortcutAction action, KeyChord? chord)
    {
        if (chord == Definition(action).DefaultChord)
        {
            overrides.Remove(action.ToString());
        }
        else
        {
            overrides[action.ToString()] = chord?.Text ?? "";
        }
    }
}
