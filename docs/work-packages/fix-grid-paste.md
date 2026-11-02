# Fix: Ctrl+V auf eine Zelle ohne Editor
Rückmeldung des Nutzers am 2026-11-02: Ein kopierter Wert ließ sich mit Ctrl+V nur einfügen, wenn ein Doppelklick vorher den Editor geöffnet hatte. Vom Nutzer am selben Tag als Fix entschieden. Branch `fix/grid-paste`.
- **Ursache:** AG Grid Community hat keine Zwischenablage (Enterprise-Modul). Auf einer fokussierten Zelle ohne Editor gab es niemanden, der das Einfügen annahm; im Editor fügt der Browser selbst ins Eingabefeld ein. In WP-58/59 war nur „mehrere Zellen aus Excel einfügen“ ausgenommen (Backlog), der einzelne Wert war nie gebaut.
- **Weg:** `grid.js` hört auf das `paste`-Ereignis am Grid (Capture-Phase; der Browser löst es an der fokussierten Zelle aus, auch ohne Eingabefeld) – nur in Tabellen, nicht während einer Zelleingabe, auf einem gesperrten Workspace still nichts (wie Entf und Ctrl+D). Den Text nimmt `event.clipboardData`, also ohne `navigator.clipboard` und ohne Berechtigungsabfrage. .NET entscheidet: `FerretGrid.OnPaste` → `GridEditing.PasteAsync`.
- **Ziele wie Ctrl+Enter (WP-59):** markierte Zellen oder Spalten → `FillAsync` (auch wenn der Fokus außerhalb der Markierung steht, wie Entf und Ctrl+C); sonst mehrere markierte Zeilen, zu denen die fokussierte gehört (`fillTargets`) → `FillRows` in der Spalte des Fokus; sonst die fokussierte Zelle → `Edit`, auch in einer neuen Zeile oben. Je Einfügen ein Undo-Schritt, Prüfung wie ein getippter Wert; was nicht geht (Primärschlüssel, LOB, zum Löschen markiert, ungültiger Wert), sagt die Statusleiste mit dem Grund des `ChangeTracker`.
- **Ein Wert aus dem Text:** `Core/Data/ClipboardCell.ValueOf` (rein, mit Tests; bis zum Code-Review `PastedCell`). Ein Zeilenumbruch am Ende fällt weg (Excel hängt ihn an eine einzelne Zelle). Ein Wert in Anführungszeichen, den Excel so geschrieben hat (Anführungszeichen innen verdoppelt, und innen ein Zeilenumbruch, Tab oder Anführungszeichen), verliert sie; `"abc"` bleibt wie es ist. Ein Tab oder ein Zeilenumbruch zwischen Werten heißt mehrere Zellen: nichts gesetzt, Hinweis `GridText.Fill_PasteSeveral`.
- **Entscheidung in der Umsetzung, vom Nutzer im Code-Review bestätigt („Der Rest passt so“):** Ein mehrzeiliger Text ohne Anführungszeichen ist nicht von einer kopierten Spalte zu unterscheiden. Er wird abgelehnt statt als ein Wert gesetzt – eine Spalte aus Excel stünde sonst als ein mehrzeiliger Wert in jeder markierten Zelle. Ausnahme seit dem Code-Review: der Text, den Ctrl+C hier zuletzt aus einer Zelle kopiert hat (`ShellState.CopiedCell`), ist genau dieser Wert. Im Zelleditor geht das Einfügen wie bisher. Das Verteilen mehrerer Zellen bleibt im Backlog („Editieren“).
- **Zelleditor:** Mehrzeilig ist er nur bei Textspalten über 200 Zeichen (`Multiline`); im einzeiligen Editor macht der Browser aus Zeilenumbrüchen Leerzeichen. Der Hinweis „im Zelleditor einfügen“ hilft also nur dort – für kürzere Spalten ist ein Zeilenumbruch selten gewollt.
- Kein ADR: kein neuer Schreibweg, nur ein weiterer Auslöser für `Edit`/`SetCells`.
- **Tests:** Core `ClipboardCellTests` (ein Wert, Excel-Zeilenumbruch und -Anführungszeichen, Anführungszeichen, die bleiben, mehrere Zellen oder Zeilen, die eigene Kopie); UI `GridEditingTests.A_pasted_value_goes_into_the_marks_the_selected_rows_or_the_focused_cell` (Ziele, neue Zeile, ein Schritt), `Several_cells_or_an_invalid_value_set_nothing_and_say_why`, `A_value_copied_here_pastes_back_with_its_line_breaks` und `An_empty_clipboard_is_null_only_right_after_copying_null`.

## E2E (2026-11-02)
Eigene Instanz (`tools/e2e`, `--data-dir` im Scratchpad, `--lang=de`, dunkel), eigener Container `ferret-paste` auf Port 1541 (danach entfernt). Klicks, Ziehen und Tasten mit **echter Windows-Eingabe** (`Invoke-RealClick.ps1`, Ctrl+V per `SendKeys` nach Prüfung des Vordergrundfensters), Zwischenablage per `Set-Clipboard` (vorher gesichert, danach zurück), Zustand über die Grid-API.
- **Weg:** Ctrl+V auf einer Zelle ohne Editor löst `paste` an der Zelle aus (Ziel ist der `span` des Werts in `.ag-cell`), `clipboardData` liefert den Text ohne Berechtigungsabfrage – auch durch den WPF-Host.
- **Eine Zelle:** KUERZEL (CHAR(3)) in KUNDEN angeklickt, „X1“ eingefügt → ausstehend. „E2E-1“ → Hinweis „Zu lang: 5 Bytes, erlaubt sind 3 (CHAR(3))“, nichts gesetzt.
- **Bereich:** drei Zellen KUERZEL per Ziehen markiert, „X2“ mit Zeilenumbruch (wie aus Excel) → alle drei „X2“; Ctrl+Z nimmt alle drei in einem Schritt zurück.
- **Markierte Zeilen:** Klick auf UMSATZ, zweimal Shift+↓, „12,5“ → drei Zeilen 12,50, auch die mit NULL; „3 Zellen gesetzt.“
- **Mehrere Zellen:** „A⇥B / C⇥D“ → Hinweis `Fill_PasteSeveral`, NAME unverändert.
- **Neue Zeile:** „+ Zeile“, Esc, Klick auf NAME der neuen Zeile, Ctrl+V → Wert ausstehend in der neuen Zeile.
- **Im Editor:** Doppelklick auf NAME, Ctrl+A, Ctrl+V mit zweizeiligem Text → der Browser fügt ins Feld ein (einzeilig: „Zeile 1 Zeile 2“), kein Setzen durch das Grid; Esc lässt die Zeile unverändert.
- Log der Testinstanz ohne `[ERR]`/`[WRN]`.
- **Nicht geprüft:** gesperrter Workspace (Ctrl+V tut dort nichts, `editState.enabled` wie Entf und Ctrl+D), ganze Spalten (Weg von `FillAsync`, Unit-Tests aus WP-59), Ergebnis-Grids von SQL-Editor und LINQ-Konsole (nur Tabellen).

## Nachtrag: Code-Review (2026-11-02)
Review mit einem unabhängigen Reviewer-Agent (`/branch-code-review`), fünf Runden. Entscheidungen des Nutzers: zuerst „Zu 2 – Mach das ruhig direkt richtig, wie in Excel. Der rest passt so.“, nach der Nebenwirkung von #2 „Rohwert + Merken“, danach zu R1/R2 „Beide beheben“.
- **#1 (M) Zwischenablage ohne Text setzte NULL:** behoben. Ein Bild oder Dateien geben `null` nach .NET, Hinweis `Fill_PasteNoText`, nichts gesetzt (`a227f68b`).
- **#2 (K) Ctrl+C und Ctrl+V veränderten Werte (Zeilenumbruch am Ende, `"A""B"`):** erst wie Excel mit Anführungszeichen gelöst (`169983f5`); die zweite Runde fand die Nebenwirkung (N1, M): Anführungszeichen auch in Visual Studio, Zelleditor und Formular. Danach „Rohwert + Merken“ (`afe310f7`): Ctrl+C kopiert roh, `ShellState.CopiedCell` merkt den Text, und genau dieser Text ist bei Ctrl+V ein Wert. Im E2E gefunden: Die Zwischenablage macht aus LF CRLF, verglichen wird mit `ReplaceLineEndings`.
- **#3 (K) Fokus nach Ctrl+V in einer neuen Zeile:** nicht nachgestellt. Im E2E mit echter Eingabe blieb der Fokus auf der Zelle, ↓ und → wanderten weiter, ein zweites Ctrl+V wirkte. Keine Änderung.
- **#4 (K) Ctrl+V fehlte in der Tastenübersicht und in architecture.md:** behoben (`FixedPasteValue`, Warnung `TakenFromGrid`, `e72f2aeb`).
- **#5 (K) deutscher Hinweis nicht in du-Form:** behoben (`d3407fac`).
- **#6 (K) Test für den ungültigen Wert prüfte die PK-Sperre:** behoben, auch der ältere `An_invalid_value_says_why_and_draws_nothing` (vorher schon da, `917d72d0`).
- **N2 (K, Verdacht) NULL kopieren und einfügen:** im E2E nachgestellt. `writeText('')` hinterlässt keinen Text (`types` `[]`, bei einem Bild `["Files"]`). Behoben: Eine leere Zwischenablage ist NULL nur direkt nach Ctrl+C auf NULL (`66d0119a`).
- **R1 (M) Ctrl+C auf eine LOB-Vorschau galt als „NULL kopiert“:** behoben. Gemerkt wird nur ein ganz kopierter Wert (`fa5053ab`), im E2E mit AUFTRAG.NOTIZ geprüft.
- **R2 (K) eine einzelne markierte Zelle kopierte anders als Ctrl+C:** behoben. Sie kopiert wie Ctrl+C auf die Zelle; andere Kopien vergessen die gemerkte Zelle (`fa5053ab`).
- **V1 (M) die einzelne markierte Zelle nahm den Anzeigetext (Enum-Member) statt des Werts:** behoben (`4a300bf9`).
- **V2 (K) ein gescheitertes Kopieren vergaß die gemerkte Zelle:** behoben, vergessen wird erst nach dem Kopieren (`4a300bf9`).
- **V3 (K) Regel für neue Kopierwege fehlte:** steht am Kommentar von `ShellState.CopiedCell` (`4a300bf9`).
- **W1 (K) architecture.md beschrieb das Kopieren einer Markierung ohne die Ausnahme für eine Zelle:** nachgezogen. Fünfte Runde sonst ohne Befund.
- **E2E nach dem Review** (eigene Instanz, eigener Container `ferret-paste`, danach entfernt; echte Eingabe):
  - Bild in der Zwischenablage → Hinweis.
  - Wert mit Zeilenumbruch: Ctrl+C liefert ihn roh, Ctrl+V setzt ihn in der nächsten Zeile unverändert.
  - NULL kopieren und auf eine gefüllte Zelle einfügen → NULL; ein Bild danach → Hinweis.
  - CLOB-Vorschau kopieren („Wert nicht kopiert.“), dann Ctrl+V → Hinweis „kein Text“.
  - Eine einzelne markierte Zelle mit `A⏎B` → roh kopiert, „1 Zelle kopiert.“, in der nächsten Zeile wieder `A⏎B`.
  - Logs ohne `[ERR]`/`[WRN]`.
- **Folge der Entscheidung „Rohwert“:** Eine Zelle mit Zeilenumbruch, mit Ctrl+C nach Excel kopiert, verteilt sich dort über mehrere Zeilen. Bereiche ab zwei Zellen, „Als Tabelle“ und ganze Spalten setzen weiter Anführungszeichen wie Excel.
