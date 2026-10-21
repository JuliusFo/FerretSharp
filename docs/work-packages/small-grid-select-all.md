# Klein: Ctrl+A und die übrigen Markier-Tasten im Grid → Release 3.44.0
Anlass (2026-10-21): Frage des Nutzers, ob Ctrl+A alle gezeigten Zeilen markiert – tat es nicht. Dazu die Bitte, die übrigen Markier-Tasten gleich mitzunehmen, „nicht dass wir nur halb arbeiten“. Branch `feat/grid-selection-keys`, baut auf [small-grid-keyboard-selection.md](small-grid-keyboard-selection.md) auf (Auswahl folgt dem Fokus wie im Explorer).

Bestand vorher (aus dem Quelltext von AG Grid 36.2 gelesen):
- Ctrl+A: AG Grid ruft `selectAllRowNodes`, das nur im Client-Side Row Model wählt (`canSelectAll` = `csrm`). Im Infinite Row Model passiert nichts, und weil AG Grid trotzdem `preventDefault` ruft, auch kein „Alles markieren“ des Browsers.
- Leertaste: AG Grid schaltet die fokussierte Zeile um, an `anchor` in `grid.js` vorbei; Shift+Leertaste nimmt AG Grids eigenen Startpunkt (letzter Klick).
- Pos1/Ende springen zur ersten/letzten Zeile (und Spalte), Ctrl+↑/↓ zum Rand; mit Shift wählten sie nur die Zielzeile. Ctrl+Pos1/Ende bewegt in AG Grid nichts.

Gebaut (nur `grid.js` und die Liste der festen Tasten):
- **Ctrl+A** wählt alle geladenen Zeilen. Zeilen, die später laden (weiterscrollen, ein Block unterwegs, ein aus dem Cache gefallener Block), kommen dazu, solange die Auswahl steht (`span` von 0 bis ∞, `selectArrived` in `getRows`).
- **Shift+Pos1/Ende, Ctrl+Shift+↑/↓**: Bereich vom Anker bis zur ersten/letzten Zeile. Bis zum Ende bleibt der Bereich offen: Bei unbekannter Zeilenzahl springt Ende nur bis zum nächsten Block, dessen Zeilen und alle danach kommen beim Laden dazu.
- **Leertaste** (auch Ctrl+Leertaste) schaltet die fokussierte Zeile um und setzt den Anker. Shift+Leertaste tut dasselbe: Einen eigenen Bereich ergäbe sie nicht, weil die Pfeiltasten den Anker immer mitnehmen.
- Ein Linksklick in die Zeilen, die Leertaste, Esc, Sortieren und `refresh` (Filter, F5) beenden den offenen Bereich; jeder neue Tastenbereich ersetzt ihn. Ein Rechtsklick auf eine markierte Zeile lässt ihn stehen (Export der Markierung).
- In einer Zelleingabe bleiben Ctrl+A und die Leertaste beim Editor, auf einer neuen Zeile oben tut die Leertaste nichts (sie lässt sich nicht wählen).
- Einstellungen › Tastenkürzel listet die neuen Tasten unter den festen (`ShortcutMap.Fixed`, Test `The_fixed_keys_list_the_grid_selection_keys`).

Bewusst nicht:
- Ctrl+A lädt nicht alle Blöcke nach. Entf, Ctrl+C und Alt+Enter treffen weiter nur geladene Zeilen; für alle Zeilen des Filters gibt es den Export „Alle Zeilen“ (WP-54). Vergleichen geht wie bisher höchstens mit 20 Zeilen.
- Ctrl+Pfeil ohne Auswahl zu ändern (Explorer): Ctrl+↑/↓ springt in AG Grid zum Rand und wählt wie bisher die Zielzeile.

Nachtrag (2026-10-21, Entscheidungen des Nutzers auf die offenen Fragen: beides ja):
- **Esc** hebt die Markierung auf, wenn es eine gibt; sonst läuft Esc weiter (Dialoge, Menüs). In einer Zelleingabe bricht Esc wie bisher die Eingabe ab.
- **Fußzeile** von Tabelle und Ergebnis: „500 markiert“ ab zwei Zeilen (eine Zeile ist nach jedem Klick markiert, das wäre Rauschen). Nach Ctrl+A oder bis zum Ende, solange nicht alle Zeilen geladen sind: „500 markiert (geladene Zeilen)“, hervorgehoben, im Tooltip, dass Löschen, Kopieren und Speichern nur diese nehmen und „Alle Zeilen“ im Kontextmenü alle. `GridSelection` (Text, Tests), `ISelectionGrid` an `FerretGrid` und `SqlResultGrid`, `SelectionCount` folgt dem Grid selbst und rendert nur sich (kein Neurendern des Tabs bei jeder Auswahl); grid.js meldet `OnSelectionChanged(count, toEnd)` nach 60 ms Ruhe.
- Falle: AG Grid ruft `onCellClicked` asynchron. Ein Ctrl+A direkt nach dem Klick verlor dort seinen offenen Bereich (im Test gefunden) – der Klick beendet ihn jetzt im `mousedown` (linke Taste, auf einer Zelle).

E2E Nachtrag: Esc mit und ohne Markierung (ohne läuft das Ereignis unverändert bis `document`), Meldung an .NET nach Ctrl+A (`500/true`), nach Esc (`0/false`), nach Shift+↓ ×5, Klick direkt vor Ctrl+A, Rechtsklick nach Ctrl+A (Bereich bleibt, Weiterscrollen markiert mit), Linksklick danach (Bereich endet). Die Fußzeile selbst nicht in der App gesehen (keine Test-DB).

E2E: `grid.js` mit AG Grid und einem Fake-.NET (Blöcke mit 150 ms Verzögerung, Zeilenzahl bekannt oder unbekannt) auf einer Testseite im eingebauten Browser, CDP-Eingabe (Stufe 2): Ctrl+A und Weiterscrollen bis 2000 Zeilen, Klick beendet das Mitmarkieren, Sortieren leert Auswahl und Bereich, Shift+Pos1, Shift+Ende zweimal bei unbekannter Zeilenzahl, Ctrl+Shift+↓ mit bekannter Zeilenzahl, Ctrl+Shift+↑, Leertaste und Ctrl+Leertaste, Shift+↑ ↓ wie zuvor, Ctrl+A und Leertaste im Zelleditor. Nicht geprüft: in der App selbst (Docker lief nicht, also keine Test-DB) und damit Tastatur durch den WPF-Host, das SQL-Ergebnis-Grid und Entf/Ctrl+C nach Ctrl+A auf echter Tabelle.
