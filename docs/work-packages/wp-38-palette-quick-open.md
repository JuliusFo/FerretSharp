# WP-38 Befehlspalette und Schnellsuche → Release 3.42.0

Branch `wp/38-palette-quick-open`, begonnen am 2026-10-19. Das Spaltenprofil war ursprünglich Teil des Pakets und ist seit 2026-10-19 WP-55 (Entscheidung des Nutzers: anderes Thema, Daten im Grid statt Navigation).

## Planung (aus `docs/roadmap.md`)
- **Befehlspalette:** Suche über alle Aktionen aus `ShortcutMap.Definitions`, mit der Taste daneben; nicht verfügbare Aktionen ausgegraut. Keine Standardtaste.
- **Schnellsuche (Ctrl+P-Stil, aus dem Backlog):** Tabellen, Views und PL/SQL der sichtbaren Verbindung aus dem `SchemaCache`, dazu C#-Entity-Namen (`Kunde` findet `KUNDEN`); Enter öffnet den Tab. Keine Standardtaste.
- Offen war die Gruppierung der Schnellsuche nach Präfix oder C#-Namespace.

## Entscheidungen des Nutzers (2026-10-19)
- **Ein Overlay für beides**, wie in VS Code: ohne Präfix sucht es Objekte, mit „>“ davor Aktionen. Tippen oder Löschen des „>“ wechselt. Begründung: dieselbe Mechanik (Suchfeld, Liste, Pfeiltasten, Enter, Esc), und der Hinweis „> Befehle“ unten in der Schnellsuche macht die Palette auffindbar.
- **Zwei Aktionen** `QuickOpen` und `CommandPalette`, beide ohne Standardtaste. Die zweite öffnet dieselbe Box mit „>“, so lassen sich beide Varianten auf eigene Tasten legen.
- Der Verbindungs-Umschalter bleibt eigenständig (anderer Ort, über alle Verbindungen); ein „@“-Modus wäre Backlog.
- Gruppierung nach Präfix oder Namespace: nicht in diesem Paket (bleibt im Backlog).

## Entscheidungen in der Umsetzung (einfachste Variante, Fragen unten)
- **Öffnen ohne Taste:** Lupe oben rechts in der Kopfleiste (vor „Statements der App“). Ohne Standardtaste wäre die Box sonst nur für Nutzer erreichbar, die in den Einstellungen eine Taste vergeben.
- **Eine Regel für Taste und Palette:** Die Prüfungen aus `Shell.OnShortcutCore` stehen jetzt in `ShellActions.Unavailable` (State, getestet). Die Taste tut nichts, wo die Palette ausgraut, und die Palette nennt den Grund („Kein Tab offen.“, „Der Workspace ist schreibgeschützt.“ …). Die Shell führt beides über `RunAction` aus.
- **Welche Aktionen die Palette zeigt:** die globalen, in der Reihenfolge und Gruppierung der Einstellungsseite, ohne die eigenen zwei. Grid- und Formulartasten (Undo/Redo im Grid, Formular, vorige/nächste Zeile) fehlen: Sie brauchen den Fokus im Grid.
- **Suche der Aktionen:** jedes Wort in Beschriftung, Gruppe oder Taste (`TextSearch`); „ctrl+s“ findet also auch „Ctrl+Shift+…“.
- **Rangfolge der Objekte** wie in der Spaltensuche: exakt, Präfix, Wortanfang, enthält (`NameRank`, aus `ColumnSearch` herausgelöst; `ObjectSearch` für Tabellen und PL/SQL). Ein Tabellenname zählt mit dem angezeigten Namen, dem echten (Synonymziel) und dem Entity-Namen, der beste Treffer gilt. Entity-Namen nur, wenn C#-Namen angezeigt werden (Einstellung, wie im Explorer).
- **Leere Eingabe:** die offenen Tabellen- und PL/SQL-Tabs des aktiven Workspaces. Höchstens 50 Treffer je Gruppe, darunter „N weitere – Suche eingrenzen“.
- **Treffer hervorgehoben** (Nachtrag, siehe unten): jedes Vorkommen jedes Worts in Name, Entity-Name bzw. Beschriftung. Ein Wort, das nur ohne die Unterstriche passt („lieferort“ in LIEFER_ORT), bleibt unmarkiert.

## Gebaut
- **Core:** `NameRank`/`NameMatch` (Rangfolge der Namenssuche, `ColumnSearch` nutzt sie), `ObjectSearch.FindTables`/`FindPlSql`; `ShortcutAction.QuickOpen`/`CommandPalette` mit Einträgen in `ShortcutMap.Definitions` (Gruppe Navigation, ohne Standardtaste), Texte `ActionQuickOpen`, `HintQuickOpen`, `ActionCommandPalette`, `HintCommandPalette`.
- **UI State:** `ShellActions` (`InPalette`, `Unavailable`) mit `ShellActionContext`; `QuickPaletteSearch` (Präfix, Suche der Aktionen, `PaletteCommand`), `PaletteRequest`; `ShellState.Palette`, `TogglePalette`, `ClosePalette` (schließt den Umschalter und umgekehrt), `BackTarget`/`ForwardTarget`.
- **UI:** `QuickPalette` (Components/Shell) oben mittig, Zeilen wie im Explorer (Symbol, Name, Entity-Name, SYN/VIEW/MVIEW/PKG …) bzw. Aktion mit Grund und Taste; Lupe in der Kopfleiste; `Shell.RunAction`. Texte `Palette_*` in `ShellText`. CSS `.palette-pop`, `.palette-row`, `.palette-command`, `.palette-more`, `.palette-footer`.
- **Tests:** Core `ObjectSearchTests`, `ShortcutTests` (neue Aktionen ohne Taste); UI `ShellActionsTests` (jede Aktion der Palette mit Regel, Gründe), `QuickPaletteSearchTests` (Präfix, Suche nach Beschriftung, Gruppe und Taste, Öffnen/Schließen/Umschalter).

## Geprüft
- `dotnet build -warnaserror`, Core.Tests, UI.Tests und Integration.Tests grün.
- **E2E** (eigener Container `ferret-wp38` mit dem Beispielschema, Testinstanz mit `--data-dir`, `--lang=de`, danach entfernt; Log ohne `[ERR]`/`[WRN]`):
  - Lupe öffnet die Box mit Fokus im Feld; leer zeigt sie die offenen Tabs, „kunde“ findet `KUNDEN`, `V_KUNDEN_AUFTRAEGE` (View) und `KUNDE_UMSATZ` (Funktion). ↓ und Enter öffnen die View als Tab und schließen die Box.
  - „>“ zeigt die Aktionen nach Gruppen mit Taste; ausgegraut mit Grund u. a. „Keine vorige Verbindung offen.“, „Kein C#-Projekt verknüpft.“, „Nur in einem SQL-Tab.“, auf der Einstellungsseite „Nur im Explorer.“. „> neuer sql“ + Enter öffnet „SQL 1“. Die Markierung beginnt auf der ersten ausführbaren Zeile (im E2E nachgebessert: sie stand auf einer ausgegrauten, Enter tat dann nichts); Enter auf einer ausgegrauten Zeile tut nichts.
  - Esc mit Text leert das Feld, das nächste Esc schließt.
  - Mit Ctrl+P / Ctrl+Shift+P in der `settings.json`: Ctrl+P öffnet und schließt, Ctrl+Shift+P öffnet mit „>“; die Lupe nennt die Taste im Tooltip, das Suchfeld zeigt sie; die Einstellungsseite listet beide Aktionen mit Hinweis.
  - Hell und dunkel (`Emulation.setEmulatedMedia`) angesehen.
  - **Nicht geprüft:** Klick mit echter Windows-Eingabe – `SendInput` meldete Erfolg, in der WebView kam aber kein `mousedown` an (vermutlich hält die Sandbox der Shell die Eingabe auf). Der Klick auf die Lupe ist mit CDP-Mauseingabe (Stufe 2) geprüft; die Box nutzt nur `click` und `mouseenter`, keine der Eingaben, die nur Stufe 3 zeigt. Entity-Namen nur in den Unit-Tests: Die Testinstanz hatte kein C#-Projekt verknüpft.

## Nachtrag: offene Fragen entschieden (2026-10-19, Empfehlungen übernommen)
- **Lupe bleibt immer**, auch mit vergebener Taste: einziger sichtbarer Weg ohne Standardtaste, ihr Tooltip nennt die Taste; ein Knopf, der je nach Einstellung verschwindet, wirkt wie ein Fehler.
- **Grid- und Formulartasten bleiben draußen:** Sie brauchen den Fokus im Grid und stünden in der Palette immer ausgegraut da. Erst wenn sie im Alltag fehlen, prüfen, welche sich über den Tab ausführen lassen (Backlog).
- **Treffer hervorheben: gebaut.** `TextSearch.Ranges` liefert die Fundstellen (wie `Matches` verglichen, alle Vorkommen, überlappende und angrenzende zusammengefasst), `Highlight` (Components/Common) setzt sie als `<mark>`; in der Box für Tabellen- und Entity-Namen, PL/SQL und Beschriftungen der Aktionen (CSS `.palette-row mark`: Akzentfarbe, fett). Tests `TextSearchRangesTests`; in der App hell und dunkel angesehen (CDP, Befehlspalette ohne Verbindung).
- **„Zuletzt benutzt“ für Aktionen: nein** (17 Aktionen, mit zwei, drei Buchstaben gefunden). Stattdessen im Backlog: „Zuletzt geöffnet“ in der Schnellsuche – auch geschlossene Tabellen.

## Nachtrag: Code-Review des Branches (2026-10-19)
Ein Review-Agent über `origin/main...HEAD` meldete 13 Befunde; der Nutzer hat alle außer 13 übernommen. Vorher `origin/main` (PR #19, Sprünge über Basistabellen) gemergt.
- **Offene Tabs:** Eine Zeile der leeren Schnellsuche führt ihren Tab; Enter zeigt genau diesen (`ShellState.ShowTab`). Vorher nahm `OpenTable` den ersten Tab derselben Tabelle – nach einem FK-Sprung gibt es zwei.
- **Esc in der Befehlspalette:** `clearOnEscape` (dom.js) bekommt über `SearchBox.EscapeKeeps` das Präfix: „> sql“ wird zu „>“, und bei nur „>“ schließt Esc die Box. Vorher wurde aus der Befehlspalette mit dem ersten Esc die Schnellsuche.
- **Suche nicht bei jedem Render:** Die Box merkt sich die Treffer zu Text, Listen des Schemas (ein Neuladen ersetzt sie) und Version der Präsentation; Hover und Änderungen der Shell rechnen nicht mehr über das ganze Schema.
- **Toggle nach angezeigtem Modus:** `PaletteRequest.ShowsCommands` folgt dem Text; die Taste des angezeigten Modus schließt, die andere öffnet im anderen neu.
- **Fehler aus der Palette** laufen wie die der Tasten über `Failed` (Log und Meldung).
- **Regel je Tab-Art:** „Filter anwenden“ nur in Tabellen-, SQL- und LINQ-Tabs, „Neu laden“ und „Spalte suchen“ auch in PL/SQL-Tabs, nichts davon im Tab für eine neue Tabelle – sonst „Nicht in diesem Tab.“ (wie die Ansichten `TabCommand` behandeln).
- **Tote Texte entfernt** (`Palette_CommandPlaceholder`, `Palette_OnlyInGrid`); `ShellActions.Unavailable` wirft für Aktionen, die nicht global sind (erreichen die Shell nie, ein Test hält es fest).
- **`aria-disabled`** als `"true"`/`"false"`; Taste in der Zeile über `<Kbd>`.
- **Lupe** geht über `RunAction(QuickOpen)` und ist ohne Verbindung deaktiviert, mit Grund im Tooltip – dieselbe Regel wie die Taste.
- **Tasten hinter der offenen Box:** Eine globale Aktion außer den beiden der Box schließt sie vorher (Ctrl+F, F5 … wirken sonst auf den Tab dahinter, während die Box offen bleibt).
- **`NameRank.Of` ohne Wörter** liefert `Contains` statt einer Ausnahme.
- Nicht übernommen: Neurendern, wenn Entity-Namen bei offener Box fertig laden (kleine Wirkung, erscheinen beim nächsten Tastendruck).
- Tests: `ShellStateTabsTests` (zwei Tabs derselben Tabelle), `QuickPaletteSearchTests` (Taste des angezeigten Modus), `ShellActionsTests` (Regel je Tab-Art, Grid-Aktionen werfen), `ObjectSearchTests` (ohne Wörter). Geprüft in der App ohne Verbindung: Esc in der Befehlspalette, Lupe deaktiviert mit Tooltip, Toggle nach Löschen des Präfixes, `aria-disabled`. Das Schließen vor anderen Aktionen braucht eine Verbindung und ist nur im Code nachvollzogen.

## Nachtrag: Bilder im Changelog (2026-10-19, Wunsch des Nutzers)
- Die ersten Bilder im CHANGELOG überhaupt (WP-49): `docs/images/changelog/3.42.0-quick-open.png` („kunde“ mit Entity-Namen, View, Funktion und Markierung) und `3.42.0-command-palette.png` (Aktionen mit Tasten, ausgegraut mit Grund), eingerückt unter dem Punkt von WP-38. Dunkel, englisch, Beispiel-DB in eigenem Container mit verknüpftem `samples`-Projekt, CDP-Ausschnitt genau um die Box (17 und 38 KB). Die View war in der frischen DB ungültig (rot); einmal geöffnet und das Schema neu geladen.
- Die Version im Dateinamen ist geraten (nächstes Minor-Release mit neuen Features). Wird es eine andere, Dateien und Verweise umbenennen – der Release-Workflow prüft nur Ordner und Dateinamen, nicht die Version.

