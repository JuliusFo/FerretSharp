# Klein: Spalten als ALTER TABLE in eine andere Datenbank übertragen → Release offen

Branch `feat/column-add-ddl` (von `main`), 2026-10-23.

## Anlass
Wunsch des Nutzers beim Planen von WP-61: in der Spaltenansicht eine oder mehrere Spalten markieren und dafür das `ALTER TABLE` erzeugen – vor allem, um Spalten in eine andere Datenbank zu übertragen.

Entscheidungen des Nutzers:
- Zweck: Spalten übertragen, also `ADD` und Kommentare. Keine FKs, Indizes oder Constraints.
- Ein eigenes kleines Paket mit den Vorschlägen aus der Machbarkeitsprüfung: Kopieren und „im SQL-Editor einer anderen Verbindung öffnen“, Owner im Ziel wie bei Links.

Ohne Rückfrage entschieden (Fragen unten):
- **Ein `ALTER TABLE … ADD` je Spalte** statt eines gemeinsamen `ADD (a, b)`: Das Skript besteht aus den Schritten von `ColumnChange` (wie Schema-Vergleich und Designer), jede Spalte behält ihre eigene Warnung, und scheitert eine (Spalte gibt es schon), laufen die anderen im SQL-Editor trotzdem. Reihenfolge nach Position im Schema – eine virtuelle Spalte steht hinter ihren Basisspalten –, Kommentare zuletzt (`DdlScript.Order`).
- **Owner im Ziel:** die passende Tabelle dort, gefunden wie ein Link sie findet (`ShellState.LinkedTable`: gleicher Owner, gleicher Name im Schema der Verbindung, Synonym). Ist die Verbindung nicht offen oder findet sich nichts, der Name im Schema, das die Verbindung zeigt (`EffectiveSchema`). Den reinen Tabellennamen ohne Owner habe ich verworfen: Die App setzt kein `CURRENT_SCHEMA`, er würde im Schema des angemeldeten Users gesucht – bei einem User mit reinen SELECT-Grants und `DefaultSchema` daneben.
- **Ziele:** alle anderen Verbindungen in der Reihenfolge der Liste, offen oder nicht – wie der Vorschlag des Schema-Vergleichs: `ShellState.OpenSqlIn` und `WorkspaceLifecycle.ConnectAsync` zeigen und verbinden sie. Prod ohne zusätzliche Sperre: Das Skript läuft erst, wenn der Nutzer es dort ausführt, über den Schemaweg mit Bestätigung (und ist dort nur nach dem Freischalten möglich).
- **Auswahl** nur in der Spaltenansicht einer Tabelle (nicht einer View): Klick, Ctrl+Klick, Shift+Klick, Rechtsklick außerhalb der Auswahl wählt nur diese Zeile. Zeilen, die der Filter ausblendet, zählen nicht mit. Neben der Überschrift steht „3 Spalten gewählt – Rechtsklick für ALTER TABLE“, sobald etwas gewählt ist (ein `title` an der ganzen Tabelle hätte über jeder Zelle aufgeploppt).

## Gebaut
- Core `AddColumnsScript.Write(source, target, columns)` (`Core/Ddl`): Kopfzeile mit der Quelltabelle, Schritte aus `ColumnChange.Steps(target, null, column, Known)`, `DdlScript.Order` und `Render`. Kein neuer DDL-Text.
- UI State `ListSelection` (Auswahl in einer Liste ohne Grid: Klick, Ctrl, Shift mit Anker, Rechtsklick, `In(shown)`), `AddColumnsTargets` (`Of`: andere Verbindungen; `Table`: Tabelle im Ziel). `ShellState.LinkedTable` gibt es zusätzlich für eine `TableRef`.
- `ColumnsView`: Zeilen wählbar (`table.columns.pickable`, `tr.selected`), `PopoverMenu` mit „Als ALTER TABLE … ADD kopieren“ und „Im SQL-Editor öffnen von“ (je Verbindung mit Art-Kennung, Tooltip nennt die Zieltabelle). `dom.js clearTextSelection`: Shift+Klick zog sonst die Textauswahl des Browsers über die Zeilen.
- Tests: Core `AddColumnsScriptTests`; UI `ListSelectionTests`, `AddColumnsTargetsTests`.

## E2E (2026-10-23)
Eigene Instanz (`--data-dir`, eigener WebView2-Ordner, deutsch, dunkel), eigener Container `ferret-e2e-coladd` mit dem Beispielschema und einem zweiten User `FERRET2`, dessen `KUNDEN` nur `KUNDE_ID` und `NAME` hat (danach entfernt). Zwei Verbindungen auf denselben Container: „E2E Quelle“ (FERRET, Test) und „E2E Ziel“ (FERRET2, Dev). Bedient per CDP, Passwort ohne Credential Manager.
- Spaltenansicht KUNDEN: Klick, Shift+Klick, Ctrl+Klick wählen wie erwartet, ohne Textauswahl des Browsers; Hinweis „4 Spalten gewählt – Rechtsklick für ALTER TABLE“.
- „Als ALTER TABLE … ADD kopieren“: Skript mit Kopfzeile, vier `ADD` (CHAR(3 BYTE), `DEFAULT SYSDATE NOT NULL`, NUMBER(12,2), `DEFAULT 0 NOT NULL`) und drei Kommentaren; Hinweis in der Statusleiste.
- „Im SQL-Editor öffnen von E2E Ziel“, Ziel nicht verbunden: Tooltip und Skript nennen `FERRET2.KUNDEN` (Schema der Verbindung), Passwortabfrage, SQL-Tab dort mit dem Skript. „Skript“ ausführen → Bestätigung mit 7 Statements → in `USER_TAB_COLUMNS`/`USER_COL_COMMENTS` von FERRET2 alle vier Spalten mit Default, NOT NULL und Kommentaren.
- Ziel verbunden: Zuordnung über das geladene Schema, ebenfalls `FERRET2.KUNDEN`. Rechtsklick außerhalb der Auswahl wählt nur diese Zeile. Die Auswahl bleibt beim Wechsel der Verbindung.
- Log ohne `[ERR]`/`[WRN]`.
- **Nicht mit echter Eingabe geprüft:** Der Klick per `SendInput` kam nicht als `mousedown` im WebView an (wie in `e2e-testing.md` beschrieben), nach der Regel dort nicht weitergeklickt. Klick mit Ctrl/Shift und Rechtsklick sind gewöhnliche `click`/`contextmenu` auf Tabellenzeilen, wie im Beziehungsdiagramm.

## Offen / Fragen
- Ob die Spalte im Ziel schon existiert, prüft das Skript nicht – Oracle meldet ORA-01430 beim Ausführen, für den echten Abgleich bleibt der Schema-Vergleich.
- Der Eintrag im Spaltenkopf-Menü des Grids (in WP-58 markierte Spalten) kommt mit WP-61 (dort vermerkt).
- Tastatur in der Spaltenansicht (Ctrl+A, Pfeile mit Shift) gibt es nicht; bisher nur Maus.
