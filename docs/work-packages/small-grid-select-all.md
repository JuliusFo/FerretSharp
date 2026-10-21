# Klein: Ctrl+A und die übrigen Markier-Tasten im Grid → Release offen
Anlass (2026-10-21): Frage des Nutzers, ob Ctrl+A alle gezeigten Zeilen markiert – tat es nicht. Dazu die Bitte, die übrigen Markier-Tasten gleich mitzunehmen, „nicht dass wir nur halb arbeiten“. Branch `feat/grid-selection-keys`, baut auf [small-grid-keyboard-selection.md](small-grid-keyboard-selection.md) auf (Auswahl folgt dem Fokus wie im Explorer).

Bestand vorher (aus dem Quelltext von AG Grid 36.2 gelesen):
- Ctrl+A: AG Grid ruft `selectAllRowNodes`, das nur im Client-Side Row Model wählt (`canSelectAll` = `csrm`). Im Infinite Row Model passiert nichts, und weil AG Grid trotzdem `preventDefault` ruft, auch kein „Alles markieren“ des Browsers.
- Leertaste: AG Grid schaltet die fokussierte Zeile um, an `anchor` in `grid.js` vorbei; Shift+Leertaste nimmt AG Grids eigenen Startpunkt (letzter Klick).
- Pos1/Ende springen zur ersten/letzten Zeile (und Spalte), Ctrl+↑/↓ zum Rand; mit Shift wählten sie nur die Zielzeile. Ctrl+Pos1/Ende bewegt in AG Grid nichts.

Gebaut (nur `grid.js` und die Liste der festen Tasten):
- **Ctrl+A** wählt alle geladenen Zeilen. Zeilen, die später laden (weiterscrollen, ein Block unterwegs, ein aus dem Cache gefallener Block), kommen dazu, solange die Auswahl steht (`span` von 0 bis ∞, `selectArrived` in `getRows`).
- **Shift+Pos1/Ende, Ctrl+Shift+↑/↓**: Bereich vom Anker bis zur ersten/letzten Zeile. Bis zum Ende bleibt der Bereich offen: Bei unbekannter Zeilenzahl springt Ende nur bis zum nächsten Block, dessen Zeilen und alle danach kommen beim Laden dazu.
- **Leertaste** (auch Ctrl+Leertaste) schaltet die fokussierte Zeile um und setzt den Anker. Shift+Leertaste tut dasselbe: Einen eigenen Bereich ergäbe sie nicht, weil die Pfeiltasten den Anker immer mitnehmen.
- Ein Klick, die Leertaste, Sortieren und `refresh` (Filter, F5) beenden den offenen Bereich; jeder neue Tastenbereich ersetzt ihn.
- In einer Zelleingabe bleiben Ctrl+A und die Leertaste beim Editor, auf einer neuen Zeile oben tut die Leertaste nichts (sie lässt sich nicht wählen).
- Einstellungen › Tastenkürzel listet die neuen Tasten unter den festen (`ShortcutMap.Fixed`, Test `The_fixed_keys_list_the_grid_selection_keys`).

Bewusst nicht:
- Ctrl+A lädt nicht alle Blöcke nach. Entf, Ctrl+C und Alt+Enter treffen weiter nur geladene Zeilen; für alle Zeilen des Filters gibt es den Export „Alle Zeilen“ (WP-54). Vergleichen geht wie bisher höchstens mit 20 Zeilen.
- Ctrl+Pfeil ohne Auswahl zu ändern (Explorer): Ctrl+↑/↓ springt in AG Grid zum Rand und wählt wie bisher die Zielzeile.

Offene Fragen an den Nutzer:
- Soll Esc im Grid (ohne Zelleingabe) die Auswahl aufheben?
- Soll die Fußzeile die Zahl der markierten Zeilen zeigen? Nach Ctrl+A auf einer großen Tabelle sieht man sonst nicht, dass nur die geladenen markiert sind.

E2E: `grid.js` mit AG Grid und einem Fake-.NET (Blöcke mit 150 ms Verzögerung, Zeilenzahl bekannt oder unbekannt) auf einer Testseite im eingebauten Browser, CDP-Eingabe (Stufe 2): Ctrl+A und Weiterscrollen bis 2000 Zeilen, Klick beendet das Mitmarkieren, Sortieren leert Auswahl und Bereich, Shift+Pos1, Shift+Ende zweimal bei unbekannter Zeilenzahl, Ctrl+Shift+↓ mit bekannter Zeilenzahl, Ctrl+Shift+↑, Leertaste und Ctrl+Leertaste, Shift+↑ ↓ wie zuvor, Ctrl+A und Leertaste im Zelleditor. Nicht geprüft: in der App selbst (Docker lief nicht, also keine Test-DB) und damit Tastatur durch den WPF-Host, das SQL-Ergebnis-Grid und Entf/Ctrl+C nach Ctrl+A auf echter Tabelle.
