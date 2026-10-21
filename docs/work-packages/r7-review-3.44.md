# R7 Fehler, Struktur, Tests und Doku nach dem Gesamt-Review 3.44.0 → Release 3.45.0
Teil A bis D des Gesamt-Reviews (`docs/review-3.44.0.md`) in **einem** Paket und einem Release (Entscheidung des Nutzers, 2026-10-21), dazu das nachgeholte E2E von WP-56. Branch `wp/r7-review-3.44` mit dem Review-Commit vorne, gebaut in einer Session ohne weitere Agenten. Reihenfolge: erst die reinen Verschiebungen (B9, C6), dann Teil A mit Tests, dann Struktur (B), Tests (C), Doku (D), zuletzt E2E.

## Ordner (B9, C6)
- `State/Export` (`AllRowsExports`, `BackgroundReads` – vorher `FileExports` –, `ExportActions`, `ColumnValueCopies`), `Query/Search` (`TextSearch`, `NameRank`, `ObjectSearch`, `ColumnSearch`); Namespaces bleiben. `ShellState.Tabs.cs` (600 Zeilen) geteilt: `WorkspaceTabs`/`TabCommand` nach `State/Tabs`, wartende Öffnungen in `ShellState.Pending.cs`.
- Tests spiegeln die Ordner von `src` (`UI.Tests/State/<Bereich>`, Core `Query/SqlEditor`, `ClrModel/Host`, `CodeGen`); eine Testklasse je Datei (zehn Dateien geteilt). Vier `IProgress<string>`-Rekorder → `tests/Shared/StepRecorder`, `SingleSecretStore` → `InMemorySecretStore`.

## Teil A – Fehler und Sicherheit
- **A1 (S):** Arbeit auf der Session eines Workspaces ist eine Liste `Work(Started, Statement)` in `WorkspaceEditing` (Grid-Writes, Commit/Rollback **und** laufende Statements), mit eigener Uhr (`TimeProvider`). `HoldsSession` speist den Verlassen-Dialog, `IsStuck`/`StuckIn` das „Session trennen“; Trennen leert die Liste, spät endende Arbeit lässt die neue Session in Ruhe. Test mit `FakeTimeProvider` (C2).
- **A2:** `BackgroundRead` kennt den Workspace (`ReadingIn`); Schreiben, Commit, Undo und Statements dort lehnt `WorkspaceEditing.RefusedByExport` mit dem Namen des Exports ab (`Editing_ExportReading`), statt zu warten und nach 5 s wie eine hängende Verbindung auszusehen. ADR 0029 Nachtrag.
- **A3:** Das Zellmenü der Tabelle schließt beim Neuladen der Zeilen (wie seit WP-57 im SQL-Ergebnis).
- **A4:** `CodeUsageService`: ein erzwungener Bau während eines laufenden folgt ihm nach (`_rerun`), ein Ergebnis für eine andere Solution gilt als veraltet, die Auswahl ist während des Baus gesperrt.
- **A5:** `CanExit` kennt laufende Exporte; Beenden (Fenster, Update) fragt „Exporte abbrechen und beenden?“ (`ConfirmExitExports`), bricht ab, wartet kurz auf das Aufräumen der Temp-Dateien und fragt danach wie bisher nach offener Arbeit.
- **A6:** `ExplainActualAsync` liest mit `lobPrefetch: 0`.
- **A7/A13:** `OracleTypes.ValueChangeOf` (`ValueChange`): CHAR → VARCHAR2 warnt „Werte behalten ihre Leerzeichen“, ein kürzeres CHAR nennt Auffüllen und mögliche Fehler zusammen; `CHAR(1)` hat keine Auffüllung.
- **A8:** Die IN-Liste trimmt CHAR-Werte wie der FK-Sprung; ein Wert nur aus Leerzeichen und das Zeilenformat bleiben.
- **A9/A10:** mit B1: Ctrl+F im Ergebnis-Formular sucht die Felder; `IFormGrid.RowCount` und `FormCursor.PastTheEnd` – ohne Zeilen oder hinter der letzten steht „keine Zeile“ (`Form_NoRows`, `Form_PastLastRow`) statt eines ewigen Spinners, auch im Tabellen-Formular.
- **A11/A27:** Caches unter `%LOCALAPPDATA%\FerretSharp\cache` (`AppPaths.CacheRoot`, mit `--data-dir` darunter); der alte Ordner wird beim Start über `SafeDelete.TryDirectoryBelow` entfernt. Code-Usage-Cache ohne Einrückung (`JsonFiles.Compact`), Name `Assembly.mvid.sethash.json`, je Assembly die drei zuletzt benutzten Builds. `docs/architecture.md` 1.7.
- **A12:** `ExportWarnings.Read` meldet je Spalte, wenn TIMESTAMP(8/9) auf 100 ns gekürzt wird (INSERT-Skript, CSV, Excel, Zwischenablage); INSERT-Skript und Zeilenvergleich sagen im Kommentar, was sie wiedergeben bzw. vergleichen.
- **A14:** `SqlClassifier`: `DROP TABLESPACE/CLUSTER/PLUGGABLE DATABASE … INCLUDING …` wie TRUNCATE, `ALTER DATABASE`/`ALTER PLUGGABLE DATABASE` wie `ALTER SYSTEM` (Entscheidung des Nutzers); Liste in `ddl.md`.
- **A15/A16:** Die Statusleiste zählt bei DISTINCT keine Gesamtzahl mehr; `ColumnValues.MaxCharacters` = 20 Mio. Zeichen mit eigener Meldung (`ColumnValues_TooLong`).
- **A17:** `ISchemaReader.GetPrimaryKeysAsync`/`GetForeignKeysAsync` nehmen die Owner als Liste (`owner IN (…)` mit Binds); Integrationstests gegen Oracle 23.
- **A19:** `LeaveRequest.Shown` hält je Workspace, was beim Fragen geschrieben war; ein Prod-Commit aus dem Verlassen-Dialog lehnt ab, wenn danach noch etwas dazukam.
- **A20/A21:** Eine veraltete Zählung wird verworfen; `WorkspaceSaver` versucht ein gescheitertes Speichern nach 30 s erneut.
- **A22/A24:** `WorkClones.SetActiveAsync` und die Solution-Wahl behalten die Wahl im Speicher und melden „nicht gespeichert“; Solution-Zuordnung und Git-Wurzel laufen nicht mehr im Render-Thread.
- **A28:** Coverage-Job mit Actions per Commit-SHA.
- **A29:** Das Standardprogramm öffnet nur Quelltext-Endungen (`SourceFiles`), `WorkCloneMapping` lehnt `..` in deterministischen Pfaden ab.
- **A30:** Der Import behält den `TnsAdminPath` einer überschriebenen Verbindung, auch `null` (= Umgebung). `connection-transfer.md`.
- **A33–A36:** Schnellsuche hält den Fokus (Zeilen ohne Tab-Stopp); feste Tastenliste mit Shift+Bild↑/↓, Warnungen für Ctrl+↑/↓/Pos1/Ende und Ctrl+A der Zeilen (Shift-only-Kombinationen kann man ohnehin keiner Aktion geben); `UsageView` rendert nicht, solange versteckt; „Shadow“ und Entity-Tooltip aus den resx.

### Verdachtsfälle (Entscheidung 6)
Keiner wurde nachgestellt – alle sind als billige Härtung ohne Nebenwirkung umgesetzt, die Begründung je Fall:
- **A16:** gerechnet statt nachgestellt: 100.000 Werte à 4.000 Zeichen sind 400 Mio. UTF-16-Zeichen (~800 MB) im Speicher und in der Zwischenablage. Die Grenze trifft keinen realistischen Fall, den die Wertgrenze durchlässt.
- **A18:** nur der Kommentar am INSERT-Skript: `N'…'` hilft nur, wenn der Statement-Text die Zeichen bis zur Datenbank trägt; bei einem Nicht-Unicode-Zeichensatz der DB gehen sie vorher verloren. Kein Code geändert.
- **A23:** Die Zwischenablage wird auf einem eigenen STA-Thread gesetzt; ob WPFs interne Wiederholung allein schon spürbar einfriert, wurde nicht gemessen – der UI-Thread wartet jetzt in keinem Fall mehr.
- **A25:** Jede VS-Instanz bekommt 2 s für ihren Solution-Namen (`SolutionOf`), danach wird sie übersprungen. Eine blockierende Instanz ließ sich ohne zweites VS nicht herstellen.
- **A26:** `dotnet build --disable-build-servers`; dass ein vom ModelHost-Build gestarteter VBCSCompiler im Job Object landet, folgt aus `KILL_ON_JOB_CLOSE` für alle Kindprozesse, wurde aber nicht mit einem parallelen Terminal-Build beobachtet.
- **A31:** `GitFolder` folgt `gitdir:` auf eine Freigabe nur, wenn die Arbeitskopie selbst dort liegt; der SMB-Zugriff wurde nicht mitgeschnitten.

### Nicht umgesetzt
- **A32** entfällt (Entscheidung 2: Kopien von Prod-Werten bleiben ohne Einschränkung).

## Teil B – Struktur
- **B1:** `FormPanelFrame` (Kopf, Suche, Zähler, „Referenziert von“, Zählungen, Breite, Tasten; Slot `FieldSlot`) für `RowFormPanel` und `ResultFormPanel`; `IncomingCounts`, `RowCompareRequest`, `FormMenuItems` mit Tests. Die kurzen `ToggleForm`-Methoden in `TabView`, `SqlView` und `LinqView` bleiben – ein Baustein dafür wäre länger; der Schalter selbst steht in `ResultFooter`.
- **B2:** `OracleSession.ReadInSnapshotAsync`, `GuardReadOnly`, `ShouldRetry` (rein, Theory für C4); Streaming und `NestedReader` in `OracleSession.Streaming.cs`.
- **B3:** `ShellState.RunInBackgroundAsync` für „alle Zeilen“ und „alle Werte“, `BackgroundRead(s)`; `OracleDataAccess` teilt den Rumpf der beiden Leser.
- **B4:** Explorer- und PL/SQL-Filter über `ObjectSearch.Matches` (Unterstriche weglassbar, Entity-Name zählt), Liste bleibt alphabetisch; `NameRank.Terms` aus `TextSearch` (Entscheidung 3).
- **B5:** `ObjectItemContent` für Explorer und Palette, `ExplorerList.Tag(TableKind)`. **Nicht** vereinheitlicht: die Tastennavigation der drei Listen-Popover (Palette, `ConnectionSwitcher`, `ColumnJump`) – sie unterscheiden sich in Auswahl, Scrollen und Enter so weit, dass ein gemeinsamer Baustein mehr Parameter als Zeilen spart (Backlog).
- **B6:** weg: `ColumnUsageRow.Counted`, `MenuButton.Disabled`, `SqlPreviewDialog.Title`, der doppelte Parameter in `DdlDraftPanel`, die Protokollfelder `ClrTypeFull`, `AutoDeclared`, `TypeKnown` (`ModelExport.CurrentFormatVersion` = 3). `DesignPlan.CanRun`/`DraftCheck.CanRun` bleiben: Die Tests halten die Regel darüber fest. `DdlDraftPanel` rechnet weiter selbst, weil es zusätzlich den laufenden Lauf und das Schreibrecht prüft (offen: dort den Core-Wert nehmen).
- **B7/B8:** veraltete Kommentare berichtigt; `ConnectionSearch.SameGroupKey` für die Arbeits-Klone, `ExecutableSearch.FindOnPath` für dotnet und VS Code.
- Nicht aufgenommen wie im Review: DB12 (lange Parser der Audit-Vorlagen).

## Teil C – Tests
- C1 `ShellState.TakeLinks`, `ErrorReport.Text`; C2 `FakeTimeProvider` für `IsStuck`; C3 `NestedReader` lehnt DML ab, Streaming-Wege in den `ReadOnlyTests`; C4 `ShouldRetry`; C5 `gridSelection.js` als reines Modul mit `node:test`-Tests in `tests/js`, gestartet von `JsTests` (ADR 0031); C7 Gate-Test ohne feste Wartezeit; C8 `LinkPipeRetry`, `VisualStudioMatch` nach `State/Host`; C9 gesperrtes Exportziel (UI) und Abbruch mitten im Streaming (Integration); C10 unterminiertes Literal; C11 `ArchitectureTests` durchsucht `src` nach SQL-Text außerhalb von Query/Oracle/Ddl (Allow-List als (Datei, Fragment)).

## Teil D – Doku
- D1 README (Funktionen seit 3.42, Alt+↑/↓); D2 `CLAUDE.md` 14,6 → 13,1 KB (Regelnamen, Details aus §3 nach `build-release.md`/`ddl.md`, §1, §4, §8) – das Ziel von ~11 KB ist weiter nicht erreicht, der Rest sind Invarianten und Stack; D3 `oracle.md` lädt nur bei den Oracle-nahen `Data`-Dateien, `ui.md` geteilt in Komponenten (`ui.md`) und Zustand/Statements (`ui-state.md`); D4 Fragen in `wp-56` und `small-explorer-views` als beantwortet vermerkt; D5 Release-Überschriften, R6b im Index nach R6a, v4 bis WP-57, Roadmap-Stummel entfernt; D6 `architecture.md`; D7–D10 wie im Review (D10 Entscheidung 4).

## E2E
Eigene Instanz (`--data-dir`, eigener WebView2-Ordner, `--lang=de`), eigener Container `ferret-e2e-r7` (danach entfernt, die Verbindung samt Credential-Eintrag über die App gelöscht), verknüpftes `samples`-Projekt, CDP-Eingabe (Stufe 2).
- **Gefunden und behoben:** Tab verließ die Schnellsuche trotz A33 weiter. `trapFocus` (dom.js) zählte die Zeilen als Tab-Ziele – Buttons mit `tabindex="-1"`, die der Browser überspringt –, fing also nie ab. Jetzt nur Elemente mit `tabIndex >= 0`; betrifft auch Dialoge, deren letztes Feld eine `SearchBox` ist (deren ✕ hat `tabindex="-1"`).
- WP-56 nachgeholt, ohne Befund: Alt+Enter im SQL- und LINQ-Ergebnis (Formular mit C#-Namen, Enum-Membern, FK-Links und „Referenziert von“ mit Zählung, Schalter in der Fußzeile aktiv), ▼ (Alt+↓) 502-mal über die Blockgrenze 500/501 bis Zeile 503 und ▲ zurück auf 500 (Grid-Auswahl folgt), drei Zeilen per Shift+↓ markiert → Vergleich „3 Zeilen · AUFTRAG“ mit markierten Unterschieden, Breite ziehen (440 → 560 px), Neustart stellt SQL- und LINQ-Tab mit offenem Formular wieder her, Höhe des Grids in der LINQ-Konsole wie das Formular.
- R7 ohne Befund: Ctrl+F im Ergebnis-Formular fokussiert die Feldsuche, die Monaco-Suche bleibt zu (A9); Abfrage ohne Treffer bei offenem Formular zeigt „Keine Zeilen zum Anzeigen“ ohne Spinner (A10); Explorer-Filter `auftragposition`, `auftrag pos`, `kundenauftraege` und der Entity-Name `KundeAuftraegeView` finden die Objekte, `xyz` meldet „Nichts gefunden“ (B4); Schnellsuche: ↓ markiert, Enter öffnet genau einen Tab (A33). Log ohne `[ERR]`/`[WRN]`.
- Nicht geprüft: Windows-Eingabe (Stufe 3); A2 und A5 (Ablehnung während eines Exports, Rückfrage beim Beenden) – ein Lesen aller Zeilen gegen den lokalen Container endet zu schnell, um dazwischen zu klicken, beides ist in `WorkspaceEditingTests`/`WorkspaceLifecycleTests` getestet; Speichern bis in den nativen Dialog (unverändert); hell/dunkel (kein CSS geändert).
