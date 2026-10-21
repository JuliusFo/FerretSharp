# Gesamt-Review 3.44.0 (2026-10-21)

Auftrag des Nutzers (2026-10-21): wieder ein Gesamt-Review über die ganze Solution mit denselben Punkten wie 3.40.0 – toter Code, Duplikate, Lesbarkeit, Architektur, Sicherheit, Projekt- und Ordnerstruktur, Tests, Doku –, weil seit 3.40 mehrere Pakete ohne eigenes Review dazugekommen sind (R6a/R6b, WP-54, WP-38, WP-40, WP-56, WP-57, Sprünge über Basistabellen, Explorer-Views, Tastatur-Zeilenauswahl u. a.). Acht parallele Agenten nach Themen, nur lesend, auf einer festen Kopie von 09b2a05: DB-Zugriff (DB), Zustand/Nebenläufigkeit (ST), UI/JS/CSS (UI), Host/Prozesse/Build (PH), Sicherheit (SE), Wiederverwendung/toter Code (RE), Tests (TE), Doku (DO). Befunde aus `docs/review-3.40.0.md` und dem Backlog wurden nur gemeldet, wenn ihre Umsetzung fehlt oder fehlerhaft ist. Doppelte Befunde sind zusammengeführt (alle IDs genannt). **Nachgeprüft** = zusätzlich von Hand im Code bestätigt; sonst die Einschätzung des Agenten.

**Stand:** umgesetzt mit R7 in 3.45.0 (`docs/work-packages/r7-review-3.44.md`), außer A32 (Entscheidung 2) und den dort genannten Resten.

Schwere: **S** falsche Daten, Datenverlust, Sicherheitslücke, Hänger · **M** falsches Verhalten in realistischen Fällen oder klare Wartungslast · **K** Kleinkram.

## Ohne Befund geprüft (Auszug)

- Sperren (ADR 0006/0019/0021): alle neuen Lesewege seit 3.40 (`ExecuteStreamingReaderAsync`, `NestedReader.ReadAsync`, `ReadSqlAllAsync`, `ReadColumnAllAsync`, Sprünge über Basistabellen, Explorer-Views) prüfen `IsReadOnlyStatement`; neues SQL nur in `QueryBuilder` mit Binds und `OracleIdentifier.Quote`.
- Fixes aus 3.40 greifen: A1 (bis auf ST-1/ST-6), A3, A4, A5, A6, A7, A8, A9/A22 (bis auf DB2/DB5), A10, A11, A12 (bis auf DB1), A14, A15, A16, A17, A18 (Job Object), A19, A20, A21, A23, A24–A28 (Härtung), A29, A30.
- WP-40 startet weder git noch dotnet für den Arbeits-Klon und löscht nichts; Editor-Starts mit `ArgumentList`, `code.cmd` nur ohne cmd-Sonderzeichen; IL-Scan ohne Laden.
- WP-54: dieselbe Abfrage wie im Grid, Wiederholung nur vor dem ersten Block, `FetchManyRows` gedeckelt, Abbruch auf dem Pool-Thread, Temp-Datei je Export, gesperrtes Ziel vorab erkannt.
- WP-57: Literale aller Typen (N'…', TO_TIMESTAMP FF7, TSTZ, HEXTORAW, BigNumber, NaN/Inf, Intervalle), Warnung ab 1000 Werten, Obergrenze 100.000, LOB/LONG gesperrt.
- Kein XSS-Weg: kein `MarkupString` aus Daten, `Highlight`/Code-Verwendung rendern encodiert, `grid.js` nur `textContent`/`title`.
- Logs (Verbindungsaufbau, Fensterschließen, Link-Pipe, Editor) ohne Passwörter, Links und Werte; Fehlerdialog maskiert nach der ausführenden Verbindung.
- Keine toten CSS-Klassen (715), keine ungenutzten resx-Keys (2288), deutsche Übersetzungen vollständig, alle JS-Exporte und `[JSInvokable]` genutzt und über `Guard`; alle Dialoge auf `ModalFrame` mit Fokus-Falle, keine handgebauten Menüs.
- R6b-Gliederung von `Components/` und `State/` trägt, neue Dateien liegen fast alle richtig; Projektschnitte bleiben sinnvoll.
- Alle relativen Markdown-Links leben, alle 59 Regel-Globs treffen Dateien, CHANGELOG-Abschnitte und Tags 1:1, ADR 0001–0030 lückenlos; `Pkcs`-Pin weiter nötig.

## Teil A – Fehler und Sicherheit

### Schwer

| # | IDs | Ort | Befund | Sicherheit |
|---|---|---|---|---|
| A1 | ST-1 | `Components/Shell/DialogHost.razor:61-66`, `WorkspaceActionsBar.razor:19-27`, `State/Workspaces/WorkspaceEditing.cs:105,114` | Rest von A2 (3.40): Verlassen-Dialog (`busy`) und „Session trennen“ (`IsStuck`) zählen nur Grid-Writes, nicht `IsStatementRunning`. Ein UPDATE aus SQL-Editor/LINQ hängt am stillen VPN, der Tab wird geschlossen → Commit/Rollback gesperrt, kein „Session trennen“; im Verlassen-Dialog werden Committen und Verwerfen abgelehnt → App nicht beendbar, bis TCP aufgibt. Vorschlag: laufende Statements in `busy` und `IsStuck` mitzählen. | nachgeprüft |

### Mittel

| # | IDs | Ort | Befund | Sicherheit |
|---|---|---|---|---|
| A2 | ST-2 | `Oracle/OracleSession.cs:257-301`, `WorkspaceEditing.cs:101-105`, `WorkspaceActionsBar.razor:23-27` | Ein Export aller Zeilen (WP-54) oder „alle Werte kopieren“ (WP-57) hält das Gate; Commit/Ctrl+S/↶ warten, nach 5 s heißt es „Verbindung hängt evtl.“ mit „Session trennen“ – das bricht den Export ab und rollt die ganze Transaktion zurück. Vorschlag: `FileExport` kennt den Workspace; Commit/Flush melden „Export läuft – warten oder abbrechen“. | nachgeprüft (Agent) |
| A3 | ST-3 | `Components/Grid/TabView.razor:137-145, 561-562`, `State/Grid/GridEditing.cs:189-218` | Das Zellmenü der Tabelle hält Zeilenindizes und überlebt ein Neuladen (Skript im SQL-Tab desselben Workspaces, Undo, Rollback vor DDL) → „Zeilen löschen“/„Zurücksetzen“ treffen andere Zeilen. Im SQL-Ergebnis mit 4c9e0ba behoben. Vorschlag: Menü bei Reload/Refresh schließen oder über Schlüssel arbeiten. | nachgeprüft (Code), Zeitfenster klein |
| A4 | ST-4 | `ClrModel/Usage/CodeUsageService.cs:110-151`, `Components/Schema/UsageView.razor:35-39, 314-320` | Wechsel der Solution (oder „Neu einlesen“) während des ersten Index-Baus wird verschluckt: Select zeigt B, Verwendungen stammen aus A. Vorschlag: erzwungenen Bau nachholen, `outdated` auch bei anderer Solution, Select während `Building` sperren. | nachgeprüft (Agent) |
| A5 | PH1, ST-5 | `State/Workspaces/WorkspaceLifecycle.Connections.cs:115`, `State/Grid/ExportActions.cs:154-195`, `App/App.xaml.cs:213-270` | `CanExit`/`GuardAsync` kennen keine laufenden Exporte: Fenster schließen oder „Update installieren“ verwirft einen langen Export ohne Rückfrage; hängt das Schließen am VPN, bleibt `<Ziel>.<guid>.tmp` liegen. Vorschlag: Rückfrage „Export abbrechen und beenden?“, Exporte vor `CloseConnection` abbrechen. | nachgeprüft (Rückfrage fehlt), Temp-Datei wahrscheinlich |
| A6 | DB1 | `Oracle/OracleDataAccess.cs:220` | Rest von A12: `ExplainActualAsync` („tatsächlicher Plan, ganzes Ergebnis“) liest mit `InitialLOBFetchSize = -1` und zählt nur – jeder CLOB/BLOB geht ganz über das VPN. Vorschlag: interne Überladung mit kleinem LOB-Vorabruf. | nachgeprüft (Code) |
| A7 | DB2 | `Schema/OracleTypes.cs:299-302` | `CHAR(n)` → `VARCHAR2(n)` gilt als frei; Oracle behält die Auffüllung, danach findet `WHERE code = 'AB'` (auch aus EF) nichts mehr. Gegenstück zu A22 (3.40). Vorschlag: Warnung „Werte behalten ihre Leerzeichen (`RTRIM`)“. | wahrscheinlich |
| A8 | DB3 | `Query/ColumnValues.cs:73` → `Query/InsertExport.cs:64` | Die IN-Liste (WP-57) übernimmt CHAR-Werte mit Auffüllung; in einer VARCHAR2-Spalte (Beziehung ohne Constraint) trifft sie nichts. `FkNavigation.FilterValue` trimmt deshalb (3.31 A32). Vorschlag: für `IsFixedChar` ohne Auffüllung schreiben, reiner Leerwert bleibt. | nachgeprüft |
| A9 | UI-2, RE1 | `Components/Sql/ResultFormPanel.razor:36`, `SqlView.razor:309-311`, `LinqView.razor:387-389` | Ctrl+F im Ergebnis-Formular (WP-56) öffnet die Monaco-Suche, obwohl der Platzhalter die Feldsuche verspricht; `ContainsFocusAsync`/`FocusSearch` aus `TabView` fehlen. | nachgeprüft |
| A10 | UI-3 | `ResultFormPanel.razor:76-79`, `Grid/RowFormPanel.razor:103-106`, `State/Grid/FormCursor.cs:114-117` | Formular offen, dann 0 Zeilen (Abfrage ohne Treffer, Filter) → dauerhaft „Zeile 1 von 0“ mit Spinner; `FormCursor` hält die nie geladene Zeile für „Block unterwegs“. Seit WP-21, mit WP-56 mitkopiert. Vorschlag: bei bekanntem Ende `Lost` mit „keine Zeile“. | wahrscheinlich |
| A11 | PH2 | `Core/AppPaths.cs:17-36`, `ClrModel/Usage/CodeUsageBuilder.cs:540, 594` | Modell- und Code-Usage-Cache liegen unter `%APPDATA%` (Roaming); der Code-Usage-Cache wird eingerückt geschrieben, je Build eine neue Datei je geänderter Assembly, aufgeräumt nach 30 Tagen → Sync beim An-/Abmelden. Vorschlag: `%LOCALAPPDATA%\FerretSharp\cache` (unter `--data-dir` darunter), ohne Einrückung, je Assembly nur die neuesten N. | Ort/Format nachgeprüft, Größe Verdacht |

### Klein

| # | IDs | Befund |
|---|---|---|
| A12 | DB4 | TIMESTAMP(8/9) wird im INSERT-Skript, CSV/Excel und Zeilenvergleich still auf 100 ns gekürzt (nur die IN-Liste warnt); der Vergleich nennt solche Werte „gleich“. TSTZ: Skript verliert die Region; der Vergleich nennt denselben Zeitpunkt mit anderem Offset „verschieden“ (Doku „as Oracle would“ falsch). |
| A13 | DB5 | `VARCHAR2(20)` → `CHAR(10)`: `MayFailOnValues` verdrängt den Hinweis auf das Auffüllen (A22). |
| A14 | DB10, SE-3 | `DROP TABLESPACE … INCLUDING CONTENTS`, `DROP CLUSTER … INCLUDING TABLES`, `DROP PLUGGABLE DATABASE … INCLUDING DATAFILES` laufen als normales DDL (löschen am Papierkorb vorbei); `ALTER DATABASE`/`ALTER PLUGGABLE DATABASE` nicht gesperrt, `ALTER SYSTEM` schon. → Entscheidung 1. |
| A15 | DB7 | „Als IN-Liste kopieren“ liest per DISTINCT, die Statusleiste nimmt die Zeilenzahl des Tabs als Gesamtzahl („71 von 150.000“). (nachgeprüft) |
| A16 | DB8 | Werte kopieren zählt nur Werte, nicht Zeichen: 100.000 lange Texte → mehrere hundert MB (OOM/Hänger). Zeichenobergrenze mit derselben Meldung. (Verdacht) |
| A17 | DB6 | Seit den Sprüngen über Basistabellen je beteiligtem Schema ein weiterer, sequenzieller Roundtrip auf `ALL_CONSTRAINTS` beim Verbinden/Neuladen – PK und FK in einer Abfrage bzw. `owner IN (…)`. |
| A18 | DB11 | Kommentar/Regel zu `N'…'` stimmt bei Nicht-Unicode-Zeichensatz der DB nicht (Statement-Text wird erst gewandelt). (Verdacht) |
| A19 | ST-6 | Rest von A1: der Verlassen-Dialog committet auf Prod ohne `shown`-Prüfung (ein Statement, das zwischen Öffnen und Klick fertig wird, geht ungesehen mit). |
| A20 | ST-7 | `TabFooter`: eine Zählung, die kurz vor Reset/Refresh fertig wird, setzt die alte Zahl (speist seit WP-54 Export-Überschrift und „x von n“). |
| A21 | ST-8 | `WorkspaceSaver`: nach einem Fehler nur `MarkDirty`, kein `Schedule` – gespeichert wird erst bei der nächsten Änderung oder beim Beenden. |
| A22 | PH3 | `work-clones.json` gesperrt beim Verschieben → `IOException` endet im Dialog „Unerwarteter Fehler“ (`DialogHost` außerhalb der ErrorBoundary); Solution-Wahl erbt Fehler von `ConnectionManager.SaveAsync`. Wie `recent.json`: Wahl im Speicher halten, Hinweis zeigen. |
| A23 | PH4 | `ClipboardService`: 5 eigene Versuche zusätzlich zu den WPF-internen (10 × 100 ms `Thread.Sleep`) → UI bis ~5 s eingefroren, wenn ein anderes Programm die Zwischenablage hält. (wahrscheinlich) |
| A24 | PH7, UI-10 | `CodeUsageOpener`/`WorkCloneDialog`: `GitFolder.FindRoot` und `RootOf` (Dateisystem bis zur Laufwerkswurzel) nach dem `await` auf dem Render-Thread – Klon auf toter Freigabe friert bis zum SMB-Timeout. |
| A25 | PH8 | `CodeEditorLauncher`: `dte.Solution.FullName` je VS-Instanz ohne Zeitgrenze, Token nicht geprüft. (Verdacht) |
| A26 | PH9 | `dotnet build … -nodeReuse:false` ohne `--disable-build-servers`: VBCSCompiler/Razor-Server landen im Job mit `KILL_ON_JOB_CLOSE` und werden beim Beenden mitten in fremden Terminal-Builds beendet. (Verdacht) |
| A27 | PH6 | Code-Usage-Cache nur nach MVID benannt, gilt aber nur für dieselbe Solution-Menge – zwei Verbindungen mit verschiedener Solution überschreiben sich. Hash der Menge in den Namen. |
| A28 | PH5 | CI-Job `coverage-badge` hat `contents: write`, `actions/checkout`/`download-artifact` nur per Tag (A35 hat Release/Pages gepinnt). |
| A29 | SE-1, DO14 | „Standardprogramm“ öffnet den PDB-Dokumentpfad per `UseShellExecute` ohne Endungsprüfung; `WorkCloneMapping.Relative` prüft im `/_/`-Zweig kein `..` → `.bat`/`.lnk`/`.url` würde ausgeführt, UNC löst SMB-Anmeldung aus. Nur Quelltext-Endungen unter der Wurzel. (nachgeprüft) |
| A30 | SE-2 | Import überschreibt eine TNS-Verbindung (gleicher Alias/User, Passwort bleibt): hat die vorhandene keinen `TnsAdminPath`, gewinnt mit „Pfade übernehmen“ der fremde → gespeichertes Passwort an fremden Listener. „null = Umgebung“ wie einen gesetzten Pfad behalten. (opt-in) |
| A31 | SE-4 | `GitFolder` folgt `gitdir:` aus einer `.git`-Datei auch auf UNC → alle 15 s SMB-Zugriff (NTLM). Nur lokal, neben/unter der Wurzel. (Verdacht) |
| A32 | SE-5 | WP-57 legt Prod-Werte ohne Einschränkung in die Zwischenablage (Windows-Verlauf, Cloud-Sync). → Entscheidung 2: bleibt so. |
| A33 | UI-4 | Schnellsuche/Palette ohne Fokus-Falle: Tab führt hinaus, Enter wirkt dahinter (A15-Szenario); ↑/↓ ohne `preventDefault`. Verdacht: Enter auf per Tab fokussierter Zeile löst zwei Aktionen aus (auch `ConnectionSwitcher`). |
| A34 | UI-6 | Feste Tastenliste nennt Shift+Bild↑/↓ der Zeilenauswahl nicht; `TakenFromGrid` warnt nicht bei Ctrl+↑/↓, Ctrl+Pos1/Ende; globales Ctrl+A warnt nur „Alles auswählen im Editor“, nicht „alle Zeilen“. |
| A35 | UI-7 | `UsageView` bleibt in jedem Tab gemountet und rendert bei jedem Modell-Ladeschritt ganz neu (auch versteckt) – Zeilen wie `UnusedReport` zwischenspeichern. |
| A36 | UI-8 | Fest codiert: „Shadow“ (`UsageView.razor:170`), „ · Entity {name}“ (`TabBar.razor:307`, Key `Explorer_EntityTooltip` existiert). |

## Teil B – Struktur und Wiederverwendung

| # | IDs | Vorschlag | Aufwand |
|---|---|---|---|
| B1 | UI-1, RE2, RE4 | **Formular und Vergleich einmal statt zweimal:** `ResultFormPanel` ist zu ~80 % `RowFormPanel` (Kopf, Suche, Zähler, „Referenziert von“, Zählungen, Resize, Tasten); die Kopie driftet schon (A9, A10). Gemeinsamer `FormPanelFrame` über `IFormTab` mit Slots für Tabellen-Teile, `IncomingCounts` in `State/Grid`; Vergleichsregel (`MaxRows`, zu wenige, fehlende) als `RowComparison.Pick` mit Tests; `FormMenuItems` für `GridContextMenu`/`ResultMenu`; `ToggleForm` in `ResultFooter`; Kommentar-Logik aus dem Markup nach `RowForm.ResultFields`. | M |
| B2 | RE3, DB9 | **`OracleSession`:** Snapshot-Wiederholung zweimal fast gleich, Lesesperre dreimal (389 → 544 Zeilen). `ReadWithSnapshotRetryAsync(…, canRetry)` + `GuardReadOnly`, Streaming und `NestedReader` nach `OracleSession.Streaming.cs`. Die Wiederholungsentscheidung als reine Funktion (trägt C4). | S–M |
| B3 | RE5 | „Alle Zeilen“ (WP-54) und „alle Werte“ (WP-57): eine Quelle `AllRows(Read, Total, Heading)`, ein `BackgroundJobs.RunAsync`; `FileExport(s)` → `BackgroundRead(s)`; Rumpf von `ReadAllAsync`/`ReadColumnAllAsync` teilen. | S |
| B4 | RE6 | Explorer- und PL/SQL-Filter nach der Regel der Palette (`NameRank`), Liste bleibt alphabetisch; `NameRank.Terms` nimmt die Wörter von `TextSearch`. Beispiel: „kundennr“ findet `KUNDEN_NR` mit Ctrl+P, im Explorer nicht. → Entscheidung 3. | S |
| B5 | UI-5, RE8 | Listen-Popover (Palette, `ConnectionSwitcher`, `ColumnJump`: Hintergrund, Markierung, Scrollen, Tasten) einmal; `ObjectItemContent` für Explorer und Palette, `ExplorerList.Tag(TableKind)` statt zweimal „VIEW“/„MVIEW“ inline. | S–M |
| B6 | RE7, UI-9 | Tot: `ColumnUsageRow.Counted`, `DesignPlan.CanRun`/`DraftCheck.CanRun` (UI rechnet selbst nach – Core-Wert nehmen), Parameter `MenuButton.Disabled`, `SqlPreviewDialog.Title`, doppelter Parameter `Statements` in `DdlDraftPanel`; Protokollfelder `LinqRunResult.AutoDeclared`, `LinqUnknownName.TypeKnown`, `PropertyExport.ClrTypeFull` nur mit `FormatVersion`-Erhöhung. | S |
| B7 | RE9, ST-9 | Veraltete Kommentare: `GridOptions.Table`, `SqlResultGrid.ExportName`, `WorkCloneMapping.FindTypeFile`, `ShellState.ShowIncomingLink` (sagt „replaces“, A25 wartet), doppelte `<summary>` über `OpenedByName`. | S |
| B8 | RE10 | `WorkClones` vergleicht Gruppen anders als `ConnectionSearch` („keine Gruppe = keine Gruppe“) – eine Regel; PATH-Suche zweimal (`CodeEditorLauncher`, `DotNetCli`) → `ExecutableSearch.FindOnPath`. | S |
| B9 | ST-9, PH10 | Ordner (reiner `git mv`, Namespace bleibt): `State/Export` (`AllRowsExports`, `FileExports`, `ExportActions`, `ColumnValueCopies` aus `State/Grid`), `Query/Search/` (`TextSearch`, `NameRank`, `ObjectSearch`, `ColumnSearch`); `Restore`/`Vanished`/`OpenedByName` aus `ShellState.Tabs.cs` (600 Zeilen) nach `WorkspaceTabs`. | S |

Nicht aufgenommen: DB12 (lange Parser in `AuditTemplates`/`AuditPattern`) – laut RE linear und ohne Gewinn beim Schneiden.

## Teil C – Tests

| # | IDs | Befund / Vorschlag |
|---|---|---|
| C1 | TE-1 | **M** A24 und A25 (3.40, sicherheitsrelevant) ohne Test, Logik in Razor (`Shell.razor` `TakeLinks`, `ErrorDialog.CopyAsync`). `TakeLinks` an `ShellState`/`LinkInbox`, Kopiertext als `ErrorReport.Text(error, mask)` nach State, je ein Test. |
| C2 | TE-2 | **M** `IsStuck`/`_busySince` mit `DateTimeOffset.Now` – der wahre Zweig von A2 ist nie getestet. `TimeProvider` in `WorkspaceEditing`, Test mit `FakeTimeProvider.Advance(StuckAfter)`. |
| C3 | TE-3 | **M** Lesesperre in `NestedReader.ReadAsync` ohne Test, die `ReadOnlyTests` kennen die Streaming-Wege nicht (CLAUDE.md §2). Unit-Test ohne DB (`new NestedReader(null!)` mit DELETE), beide Methoden in den Reflexionstest. |
| C4 | TE-4 | **M** „Nach `HandingOn` nicht wiederholen“ ungetestet – fällt die Bedingung bei einem Umbau weg, schreibt der Export Zeilen still doppelt. Entscheidung als reine Funktion (B2), Theory. |
| C5 | TE-5 | **M** Tastatur-Zeilenauswahl: ~170 Zeilen Bereichslogik in `grid.js` (Anker, Shift-Bereiche, `toEnd`, 250-ms-Fenster) ohne Test; sie bestimmt, welche Zeilen Entf markiert. Bereichsrechnung als reines Modul abtrennen und testen (Node oder C#-Spiegel), mindestens E2E-Ablauf in `docs/e2e-testing.md`. |
| C6 | TE-6 | Struktur: `UI.Tests/State` (69 Dateien) flach, `src` seit R6b in Unterordnern; im Core fehlen `Query/SqlEditor`, `ClrModel/Host`, `CodeGen`. Zehn Dateien mit mehreren Testklassen (`PlSqlTests` fünf, `RowComparisonTests` in `RowFormTests`, `ExplorerListTests` in `PlSqlTabTests`, `CodeUsageOpener` in `CodeUsageRowsTests`, `SqlHistoryStoreTests` unter Query). Vier gleiche `IProgress<string>`-Rekorder, `SingleSecretStore` neben `Shared/InMemorySecretStore`. Reiner `git mv`-Commit. |
| C7 | TE-7 | `SessionGateTests.An_error_while_closing_is_the_closing_not_a_failure` hängt an `Task.Delay(200)` – `TaskCompletionSource` nach `BeginCloseAsync`. |
| C8 | TE-8 | App-Logik ohne Test: `CodeEditorLauncher.Match` (Wahl der VS-Instanz), Backoff der `LinkPipe` (A27) – wie `ProtocolCommand` (C11/3.40) nach `UI/State/Host` ziehen. |
| C9 | TE-9 | WP-54: UI-Weg mit gesperrter Zieldatei (Meldung, `Exports.End`) und Abbruch mitten im Streaming (Cursor, Nested-Reader, Session danach nutzbar) ungetestet. |
| C10 | TE-10 | `SqlRedaction.WithoutLiterals`: Fall für unterminiertes Literal (`'Müller` ohne Ende, ORA-01756) fehlt. |
| C11 | TE-11 | `ArchitectureTests` prüfen nur „Core ohne WPF“; Quelltext-Scan mit Allow-List für „SQL nur in Query/Oracle, DDL nur in Ddl, kein SQL in UI“ (heute kein Verstoß). |

## Teil D – Doku und CLAUDE.md

| # | IDs | Befund / Vorschlag |
|---|---|---|
| D1 | DO1 | **M** README ohne alles seit 3.42: Schnellsuche/Palette, Export aller Zeilen im Hintergrund, Verwendung im Code/„ungenutzt“/Arbeits-Klon, Formular und Vergleich für Ergebnisse, Spaltenwerte/IN-Liste, Tastatur-Zeilenauswahl/Ctrl+A, Views im Explorer; Alt+↑/↓ fehlt in den Standardtasten (DO11). |
| D2 | DO2 | **M** `CLAUDE.md` 14,8 KB (~4,2k Tokens je Session), Ziel aus 3.40 D1 ~11 KB nie erreicht; §8 zählt wieder Versionen auf. Kürzen um ~2 KB: §8 auf eine Zeile, Regelnamen in „Wo steht was“ streichen, Details aus §3 (DevExpress/Source Mapping, SpreadCheetah, AppTrace-Feldliste) in Regeln/ADRs, §4 straffen, §1-Überschneidung mit README. |
| D3 | DO4 | **M** Regeln 46 → 60 KB (+31 %): `oracle.md` (10 KB) lädt bei allen `Data/**`-Dateien (auch Export) – auf die Oracle-nahen verengen (Rest von 3.40 D8); `ui.md` (12 KB, lädt bei 393 Dateien) in allgemein und Shell/State teilen. |
| D4 | DO3 | **M** WP-56 ohne E2E released; offene Fragen (Platz des Schalters, Titel im Vergleich) und die Materialized-Views-Frage aus `small-explorer-views.md` stehen nicht in der Roadmap. → Entscheidung 5; E2E in R7 nachholen. |
| D5 | DO5, DO6, DO9 | Protokoll-Überschriften auf „Release offen“ (wp-56, wp-57, small-grid-keyboard-selection, small-grid-select-all, small-tab-bar-scroll) oder ohne Release (wp-38, wp-40, wp-54, small-explorer-views, small-fk-jumps-base-tables, fixes-2026-10-21); R6b im Index falsch einsortiert; Versionstabelle v4 endet bei WP-54; Roadmap-Stummel erledigter Pakete (WP-40, WP-57, Abschnitt WP-52 … WP-54). |
| D6 | DO7 | `architecture.md`: `SwitchWorkClone` fehlt in der Tastentabelle, `ShortcutOverrides` gibt es nicht mehr (`SettingsMap`), Kopf „bis 3.40.1“. |
| D7 | DO8, PH10 | `CLAUDE.md` §4 und `ui.md:13`: `ClrModel/Usage/` fehlt, „State/ (gleiche Ordner)“ stimmt nicht (State-Ordner aufzählen); Kommentar `FerretSharpConnections.cs:18-20` nennt Audit-Vorlage und `CodeSolution` nicht. |
| D8 | DO10 | `review-3.31.0.md:1`: Überschrift mit einzelnem CR und angehängter Tabellenspalte (seit afceb63), Datei als einzige mit CRLF → Git hält sie für binär. |
| D9 | DO12, DO13 | `connection-transfer.md:14`: `CodeSolution` fehlt bei den lokalen Pfaden; ADR 0029 ohne Nachtrag zu `ReadColumnAllAsync` (WP-57). |
| D10 | DO15 | Ein Spaltenname aus dem Projekt des Nutzers steht als Beispiel für „gemischte Schreibweise → ORA-00904“ in `.claude/rules/clr-model.md`, `wp-11-clr-model.md` und zwei Tests (`PropertyCodeTests`, `SchemaDiffTests`). → Entscheidung 4. |

## Fragen an den Nutzer

1. **A14** – `DROP TABLESPACE … INCLUDING CONTENTS`, `DROP CLUSTER … INCLUDING TABLES`, `DROP PLUGGABLE DATABASE … INCLUDING DATAFILES` wie TRUNCATE sperren, `ALTER DATABASE` wie `ALTER SYSTEM`?
2. **A32** – Kopien von Prod-Werten aus Windows-Verlauf und Cloud-Sync der Zwischenablage heraushalten?
3. **B4** – Explorer-Filter nach der Regel der Palette?
4. **D10** – Stammt der Spaltenname aus dem Kundenprojekt, ersetzen?
5. **D4** – WP-56: Schalter „Formular“ in der Fußzeile lassen? Titel im Vergleich (SQL: gelesene Tabelle bzw. Tab-Titel, LINQ: „Ergebnis“) lassen? Materialized Views unter Views lassen?
6. **Umfang und Reihenfolge** – wie 3.40 in zwei Paketen (Fehler als Patch, Struktur/Tests/Doku als Minor) oder anders?

## Entscheidungen des Nutzers (2026-10-21)

1. **A14** – Ja: die drei DROP-Formen werden wie TRUNCATE behandelt (nie ausgeführt), `ALTER DATABASE`/`ALTER PLUGGABLE DATABASE` wie `ALTER SYSTEM`.
2. **A32** – Nein: Die Person, die Werte kopiert, weiß, was sie tut; die Zwischenablage bleibt ohne Einschränkung, auch auf Prod. A32 entfällt.
3. **B4** – Ja: Explorer- und PL/SQL-Filter nach `NameRank`, die Liste bleibt alphabetisch.
4. **D10** – Ja: der Name wird überall durch ein neutrales Beispiel ersetzt (die Git-Historie bleibt).
5. **D4** – Wie gebaut: Schalter in der Fußzeile, Titel im Vergleich wie gebaut, Materialized Views unter Views. Die Fragen gelten damit als beantwortet; das E2E von WP-56 wird in R7 nachgeholt.
6. **Umfang** – Alles (Teil A bis D) in **einem** Paket R7 und einem Release 3.45.0, nicht aufgeteilt in Patch und Minor. Für die übrigen Punkte gelten die Vorschläge oben. Verdachtsfälle (A16, A18, A23, A25, A26, A31) werden wie bei 3.40 vor einer Änderung nachgestellt bzw. begründet; was sich nicht bestätigt, kommt mit Begründung ins Protokoll.
