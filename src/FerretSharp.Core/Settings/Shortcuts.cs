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
    public static readonly IReadOnlyList<ShortcutDefinition> Definitions =
    [
        new(ShortcutAction.ConnectionSwitcher, "Navigation", "Verbindung wählen", "ctrl+shift+o", ShortcutScope.Global),
        new(ShortcutAction.PreviousConnection, "Navigation", "Zur vorigen offenen Verbindung", "alt+o", ShortcutScope.Global, "Wechselt, ohne zu trennen."),
        new(ShortcutAction.Back, "Navigation", "Zurück zum Tab, aus dem ein FK-Sprung kam", "alt+arrowleft", ShortcutScope.Global),
        new(ShortcutAction.Forward, "Navigation", "Wieder vor", "alt+arrowright", ShortcutScope.Global),
        new(ShortcutAction.ApplyFilters, "Daten", "Filter anwenden", "ctrl+enter", ShortcutScope.Global, "Im SQL-Editor und in der LINQ-Konsole: ausführen."),
        new(ShortcutAction.Refresh, "Daten", "Neu laden", "f5", ShortcutScope.Global, "Im SQL-Editor und in der LINQ-Konsole: ausführen."),
        new(ShortcutAction.FindColumn, "Daten", "Spalte suchen", "ctrl+f", ShortcutScope.Global, "In der Spalten-Ansicht: filtern; im SQL-Editor, in der LINQ-Konsole und im Formular: suchen."),
        new(ShortcutAction.ToggleForm, "Daten", "Formular öffnen/schließen", "alt+enter", ShortcutScope.GridAndForm, "Bei mehreren markierten Zeilen: vergleichen. Nur im Grid und im Formular."),
        new(ShortcutAction.FormPrevious, "Daten", "Formular: vorige Zeile", "alt+arrowup", ShortcutScope.Form),
        new(ShortcutAction.FormNext, "Daten", "Formular: nächste Zeile", "alt+arrowdown", ShortcutScope.Form),
        new(ShortcutAction.Flush, "Editieren", "Ausstehende Änderungen schreiben", "ctrl+s", ShortcutScope.Global, "Schreibt in die Transaktion, ohne Commit."),
        new(ShortcutAction.Commit, "Editieren", "Commit", "ctrl+shift+enter", ShortcutScope.Global, "Auf Prod immer mit Bestätigung."),
        new(ShortcutAction.NewSql, "SQL und LINQ", "Neuer SQL-Editor", "ctrl+shift+q", ShortcutScope.Global),
        new(ShortcutAction.NewLinq, "SQL und LINQ", "Neue LINQ-Konsole", "ctrl+shift+l", ShortcutScope.Global, "Mit verknüpftem C#-Projekt."),
        new(ShortcutAction.RunScript, "SQL und LINQ", "Ganzes Skript ausführen", "alt+x", ShortcutScope.Global, "Bzw. die markierten Statements."),
    ];

    public static readonly IReadOnlyList<FixedShortcut> Fixed =
    [
        new("Daten", "Wert kopieren, mehrere markierte Zeilen als Tabelle", "Ctrl+C", "Im Grid."),
        new("Daten", "Markierte Zeilen zum Löschen vormerken", "Entf", "Im Grid."),
        new("Daten", "Zelle bearbeiten, LOB-Editor öffnen", "Enter", "Im Grid; auch Doppelklick."),
        new("Daten", "Eingabe abbrechen", "Esc", "Im Grid."),
        new("Daten", "Filter anwenden", "Enter", "Im Wertfeld eines Filters."),
        new("SQL und LINQ", "Vorschläge", "Ctrl+Leertaste", "Im Editor."),
        new("SQL und LINQ", "Ersetzen", "Ctrl+H", "Im Editor."),
        new("SQL und LINQ", "Rückgängig, wiederholen", "Ctrl+Z, Ctrl+Y", "Im Editor; nicht für geschriebene Änderungen."),
        new("Allgemein", "Menü oder Dialog schließen, Suchfeld leeren", "Esc"),
    ];

    /// <summary>Taken by Windows or the WebView before FerretSharp sees them.</summary>
    private static readonly Dictionary<string, string> Reserved = new()
    {
        ["alt+f4"] = "Alt+F4 schließt das Fenster.",
        ["alt+space"] = "Alt+Leertaste öffnet das Fenstermenü von Windows.",
        ["alt+tab"] = "Alt+Tab wechselt das Fenster.",
        ["ctrl+escape"] = "Ctrl+Esc öffnet das Startmenü.",
        ["ctrl+shift+escape"] = "Ctrl+Shift+Esc öffnet den Task-Manager.",
        ["f12"] = "F12 öffnet die Entwicklertools.",
        ["ctrl+shift+i"] = "Ctrl+Shift+I öffnet die Entwicklertools.",
    };

    /// <summary>Keys of the editors (Monaco) and the grid a global shortcut would take away from them.</summary>
    private static readonly Dictionary<string, string> TakenFromEditors = new()
    {
        ["ctrl+c"] = "Kopieren im Grid und in den Editoren",
        ["ctrl+v"] = "Einfügen",
        ["ctrl+x"] = "Ausschneiden",
        ["ctrl+a"] = "Alles markieren",
        ["ctrl+z"] = "Rückgängig in den Editoren",
        ["ctrl+y"] = "Wiederholen in den Editoren",
        ["ctrl+space"] = "Vorschläge in den Editoren",
        ["ctrl+h"] = "Ersetzen in den Editoren",
        ["ctrl+d"] = "Nächsten Treffer markieren in den Editoren",
        ["ctrl+g"] = "Gehe zu Zeile in den Editoren",
        ["ctrl+shift+k"] = "Zeile löschen in den Editoren",
        ["alt+arrowup"] = "Zeile nach oben verschieben in den Editoren",
        ["alt+arrowdown"] = "Zeile nach unten verschieben in den Editoren",
        ["delete"] = "Zeilen löschen im Grid",
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
        if (Reserved.TryGetValue(chord.Text, out var reserved))
        {
            return new ShortcutCheck(reserved, null, null);
        }

        if (chord.Ctrl && chord.Alt)
        {
            return new ShortcutCheck("Ctrl+Alt ist auf deutschen Tastaturen AltGr (@, €, {) – das würde Zeichen beim Tippen abfangen.", null, null);
        }

        if (!chord.Ctrl && !chord.Alt && !chord.IsFunctionKey)
        {
            return new ShortcutCheck("Ohne Ctrl oder Alt würde die Taste beim Tippen fehlen – nur F-Tasten gehen allein.", null, null);
        }

        var conflict = ignoreConflicts ? null : Definitions.FirstOrDefault(d => d.Action != action && _chords[d.Action] == chord)?.Action;
        var warning = Definition(action).Scope == ShortcutScope.Global && TakenFromEditors.TryGetValue(chord.Text, out var taken)
            ? $"{chord.Label} ist sonst „{taken}“ – das Kürzel gewinnt, die Funktion dort fällt weg."
            : chord.Shift && chord.Alt && !chord.Ctrl
                ? "Alt+Shift wechselt unter Windows die Tastatursprache, wenn beide allein gedrückt werden – mit einer weiteren Taste geht es meist, kann aber stören."
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
