# Klein: Explorer und Editoren in der Größe ziehen → Release 3.60.0

Branch `feat/resizable-panels` (von `main`), 2026-11-04.

## Anlass
Die Breite der Änderungsübersicht ließ sich ziehen, die des Explorers und die Höhe von SQL-Editor bzw. Ergebnis nicht. Wunsch des Nutzers: beides ziehbar machen.

Ohne Rückfrage entschieden (einfachste Variante, Fragen unten):
- **Explorer-Breite für die ganze App**, in den Einstellungen wie `ChangesPanelWidth`: `AppSettings.ExplorerWidth` (gilt für den Explorer jeder Verbindung).
- **Editorhöhe je Tab** (Entscheidung des Nutzers nach dem E2E: „Ich würde erstmal je Tab eine eigene Höhe machen.“; zuerst war es eine Einstellung für alle Tabs): `SqlTab`/`LinqTab.EditorHeight`, gespeichert mit dem Workspace in `SqlTabState`/`LinqTabState.EditorHeight` wie die Formularbreite. Ein neuer Tab beginnt mit der Standardhöhe.
- **Die LINQ-Konsole mit dabei:** Sie ist gebaut wie der SQL-Editor; ohne Griff verhielte sie sich anders. Gezogen wird nur der Abfrage-Editor, das Variablen-Feld bleibt 62 px.
- **Doppelklick stellt den Standard her**, an allen vier Griffen (auch Formular und Änderungsübersicht): Die Einstellung bzw. `Tab.FormWidth` wird null, der Standard steht wieder im CSS bzw. in der Komponente.
- Grenzen: Explorer 200 px bis 50 % von `.body` (Standard 280 px), SQL-Editor 80 px bis 75 % der Ansicht, LINQ-Editor 60 px bis 60 %. Wird das Fenster nach dem Ziehen kleiner, hält das CSS dieselben Grenzen: `max-width: 50%` am Explorer, `min(…, 75cqh)` bzw. `60cqh` an den Editoren (seit dem Code-Review, unten).

## Gebaut
- `ResizeHandle` (Common) für alle vier Griffe: `Edge` (links/rechts/unten), `Min`, `MaxFraction`, wahlweise `Variable` (CSS-Variable statt eigener Größe), `Measure`/`Within` (Selektoren für das gezogene Element und die Grenze); meldet `OnResized` und `OnReset`. Doppelklick über `@onclick` mit `Detail >= 2` (kein `dblclick` im WPF-Host).
- `dom.js`: `dragWidth` → `dragSize` (Breite oder Höhe, Rand links/rechts/unten) auf `trackDrag` (Ende wie bisher auch bei verlorenem Capture, A27). **Neu: erst ab 3 px Bewegung wird gezogen, sonst kommt 0 zurück** – ein Klick, auch jede Hälfte eines Doppelklicks, speichert keine Größe und kommt dem Zurücksetzen nicht in die Quere. `boxParent` überspringt `display: contents` (`.scope-explorer`, um jeden Explorer).
- Explorer: Die Breite steht als CSS-Variable (`--explorer-width`, `PanelSizes`) im `style` von `.app`. Die Shell rendert sie aus den Einstellungen und rendert bei `Settings.Changed` nur neu, wenn sich die Breite geändert hat. Beim Ziehen setzt `dragSize` dieselbe Variable an `.app`. Damit folgen die Explorer aller Verbindungen, ohne selbst zu rendern. Beim Zurücksetzen nimmt Blazor das ganze `style`-Attribut zurück, auch den Wert aus JS.
- SQL- und LINQ-Editor: dasselbe je Tab an seiner Ansicht – `--sql-editor-height`/`--linq-editor-height` im `style` von `.sqlview`/`.linqview` aus `Tab.EditorHeight`; `ResizeHandle VariableOn` sagt `dragSize`, an welchem Vorfahren die Variable steht.
- Monaco folgt der Höhe von selbst (`automaticLayout`).
- Formular und Änderungsübersicht auf `ResizeHandle` umgestellt. `GridText.Form_ResizeWidth` ist ersetzt durch `CommonText.Resize_Width`/`Resize_Height` (mit Hinweis auf den Doppelklick).

## Prüfung
- E2E 2026-11-04 mit echter Maus (`SendInput`, Testinstanz 1440×900, eigener Container), Ereignisse per CDP mitgeschnitten:
  - Explorer: Der Griff liegt bis 3 px über dem Inhalt rechts daneben oben. Ziehen auf 461 px, Grenze bei 712 px (50 % von 1424), Minimum 200 px. Eine zweite Verbindung öffnet mit derselben Breite, und nach dem Ziehen dort hat auch die erste die neue Breite.
  - SQL-Editor: Monaco folgt (Container 452, Layout 450), Grenze 560 px (75 % von 747), Minimum 80 px, auch mit offenem Verlauf. LINQ-Editor: folgt, Grenze 448 px (60 % von 747), das Variablen-Feld bleibt 62 px.
  - Doppelklick an allen vier Griffen: Standard wieder da (280 px, 30vh, Formular 440, Änderungsübersicht 380), die Einstellung ist entfernt. Der WPF-Host schickt wie erwartet zweimal `mousedown` (`detail` 2 und 3); dabei wird keine Größe gespeichert. Auch der `click` nach jedem Ziehen speichert nichts.
  - Neustart: Explorer, SQL, LINQ, Änderungsübersicht (Einstellungen) und Formular (Workspace) haben dieselben Größen. Log ohne `[ERR]`/`[WRN]`.
- **Gefunden und behoben:** Die Verlaufsspalte war höchstens `clamp(180px, 42vh, 560px)` hoch (`.sql-history`). Ist der Editor höher (bis 75 %), endete ihr Rand neben dem Editor. Mit vielen Einträgen wäre der Bereich bei kleinem Editor dagegen auf 42vh gewachsen. Entscheidung des Nutzers: Die Spalte ist immer so hoch wie der Editor, die Liste scrollt. Umgesetzt mit `contain: size` statt `max-height`: Die Spalte trägt nichts zur Höhe bei und wird auf die Höhe der Zeile gestreckt. Im E2E mit 30 Einträgen geprüft: 352, 560 und 80 px Editor, die Spalte jeweils gleich hoch. Bei 80 px bleiben das Suchfeld und ein Eintrag sichtbar.

## Fragen an den Nutzer
- ~~Reicht eine Größe für alle Tabs, oder soll die Editorhöhe je Tab gelten (wie die Formularbreite)?~~ Entschieden: je Tab (Nachtrag unten).
- Soll ein neuer SQL- oder LINQ-Tab mit der Standardhöhe beginnen (so gebaut) oder mit der zuletzt gezogenen Höhe?

## Nachtrag: Code-Review (2026-11-04)
Ein unabhängiger Reviewer-Agent (`/branch-code-review`), vier Durchgänge. Entscheidungen des Nutzers: „Alle, 1 mit (a)“, danach „A2 mit clip, B fixen“, danach „Ja, scrollbar“.

- **B1 (M) Gespeicherte Editorhöhe in einem kleineren Fenster:** behoben. Die Grenze beim Ziehen galt nur beim Ziehen, im CSS stand `70vh` des Fensters. Wurde das Fenster kleiner, drückte der Editor die Ergebnisse weg, und der Griff lag außerhalb des Fensters. Jetzt sind `.sqlview`/`.linqview` Größen-Container, die Höhe ist `min(gespeichert, 75cqh / 60cqh)`. Im WebView geprüft: `container-type: size` macht die Ansicht nicht zum Bezugsrahmen für `position: fixed`, Menüs bleiben, wo sie waren. E2E: SQL auf 560 und LINQ auf 448 px gezogen, Fenster 900 → 700 px: 410 bzw. 328 px, beide Griffe in der Ansicht.
- **A (M, Rest von B1) Der Block über den Ergebnissen wuchs weiter:** behoben (Variante A2). Bind-Variablen bzw. Labels und das LINQ-Variablenfeld kamen zur Grenze dazu. Jetzt schrumpfen `.sql-main`/`.linq-editors` (`flex: 0 1 auto`), der Editor gibt nach, das LINQ-Variablenfeld bleibt 62 px. Die Ergebnisse behalten mindestens 48 px, und die Ansichten schneiden ab (`overflow: clip`, kein Scroll-Container), statt über die Statusleiste zu malen. E2E bei 700 px mit 6 Variablen und offenem Verlauf: Editor 314 px, Variablen ganz sichtbar, Griff in der Ansicht. Bei 521 px: SQL 174, LINQ 185 px, den LINQ-Griff dort mit echter Maus gezogen. Das Zellmenü im Ergebnis öffnet unbeschnitten.
- **D (K) Laufanzeige im schmalen Ergebnisstreifen abgeschnitten:** behoben. Der Ergebnisbereich scrollt jetzt (`overflow-y: auto`). E2E: lange Abfrage bei 48 px Streifen, bis „Abbrechen“ gescrollt und mit echter Maus abgebrochen. Bei 900 px und Standardhöhe kein Scrollbalken. Bewusst so gelassen: Ist der Editor groß gezogen, der Streifen also unter etwa 190 px, scrollt der Streifen außen zusätzlich zum Grid (das Grid hält `min-height: 160px`).
- **B2 (K) Klick und gleich danach Ziehen setzte die gezogene Größe zurück:** behoben. Vor dem Fix mit echter Maus nachgestellt: Der `click` nach dem Ziehen kam mit `detail` 3. Jetzt setzt nur ein Doppelklick dort zurück, wo die Taste gedrückt wurde (`ResizeClick`, Tests in `ResizeClickTests`). Nach dem Fix: Die gezogene Größe bleibt, ein Doppelklick setzt weiter zurück.
- **B (K, Rest von B2) Quer verrutschter Doppelklick:** behoben. `ResizeClick` prüfte beide Achsen, `dom.js` nur die Zugachse; jetzt zählt auch dort nur die Zugachse (Testfall „quer verrutscht“).
- **B3 (K) README:** behoben, eine Zeile unter „Browse“.
- **B4 (K) Griff-CSS mitten im Formularabschnitt:** behoben, der Block steht hinter dem Formularabschnitt.
- Werkzeug: `Invoke-RealClick.ps1` kann vor einem Ziehen klicken (`-Clicks 2` mit `-ToX/-ToY`).
- Vorher schon da, nicht geändert: `ChangesPanelWidth` und `FormWidth` werden ohne Prüfung auf > 0 gerendert (ein negativer Wert von Hand ergibt nur die automatische Breite). Viele Bind-Variablen konnten den SQL-Bereich schon mit der alten Standardhöhe über die Ansicht schieben; das fängt jetzt A ab.

## Nachtrag: Editorhöhe je Tab (2026-11-04)
Entscheidung des Nutzers nach dem Code-Review: „Ich würde erstmal je Tab eine eigene Höhe machen.“
- `AppSettings.SqlEditorHeight`/`LinqEditorHeight` entfernt (nie veröffentlicht; ein Wert in einer vorhandenen `settings.json` wird beim Lesen übergangen, im E2E geprüft).
- Die Höhe steht in `SqlTab`/`LinqTab.EditorHeight` und in `SqlTabState`/`LinqTabState` (optionaler letzter Parameter, alte Workspace-Dateien laden mit Standardhöhe). Die Ansicht rendert sie als CSS-Variable an `.sqlview`/`.linqview` (`PanelSizes.Property`); `ResizeHandle VariableOn` setzt sie beim Ziehen dort. Geändert wird über `Shell.MarkDirty()` wie die Formularbreite.
- Tests: `FormTabsTests` (Tab ↔ Zustand), `WorkspaceStoreTests.Sql_tabs_keep_script_typed_variables_and_editor_height` (Datei), `PanelSizesTests`.
- E2E mit echter Maus: zwei SQL-Tabs und ein LINQ-Tab; Ziehen in „SQL 1“ lässt die anderen auf dem Standard. Drei eigene Höhen (411, 181, 298 px) stehen im Workspace; der Doppelklick in „SQL 1“ setzt nur diesen Tab zurück; nach dem Neustart sind alle drei wieder da. Log ohne `[ERR]`/`[WRN]`.
