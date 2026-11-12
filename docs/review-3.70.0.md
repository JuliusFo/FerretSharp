# Gesamt-Review 3.70.0 (2026-11-12)

Auftrag des Nutzers (2026-11-12): `/complete-code-review` nach dem Release 3.70.0. Ein Gesamt-Review über die ganze Solution mit denselben Punkten wie 3.54.0 – toter Code, Duplikate, Lesbarkeit, Architektur, Sicherheit, Projekt- und Ordnerstruktur, Tests, Doku –, ohne eigenen Schwerpunkt. Seit 3.54 (R8 in 3.55.0) sind 19 Arbeitspakete und rund 30 kleine Pakete und Fixes ohne Gesamt-Review dazugekommen:
- WP-67–79: Tablespaces/Index-Richtung, Synonyme, Diagramm-Abfrage, Explorer-Menüs, Rechte, Modellbericht, SQL-Editor, Wege, Serverlast mit Code-Herkunft, SQL-Skripte, Zeitreise, SQL-Bedingung
- WP-44 (Daten-Diff), WP-81 (Ergebnisse behalten), WP-82/83 (View-Definitionen, DDL-Vorschlag ausführen), WP-84 (Skripte im Zellmenü), WP-41 (Designer-Änderungen ins C#-Projekt)
- PK-Werte ändern, ziehbare Panels, Tab-Leiste, WebView-/RDP-/GPU-Fixes u. a.

Acht parallele Agenten nach Themen, nur lesend, auf einer festen Kopie von `213bd507`: DB-Zugriff (DB), Zustand/Nebenläufigkeit (ST), UI/JS/CSS (UI), Host/Prozesse/Build (PH), Sicherheit (SE), Wiederverwendung/toter Code (RE), Tests (TE), Doku (DO). Befunde aus `docs/review-3.54.0.md` und dem Backlog wurden nur gemeldet, wenn ihre Umsetzung fehlt oder fehlerhaft ist. Doppelte Befunde sind zusammengeführt (alle IDs genannt). **Nachgeprüft** = zusätzlich von Hand im Code bestätigt; sonst die Einschätzung des Agenten.

**Stand:** in Umsetzung mit R9 (Branch `wp/r9-review-3.70`).

Schwere: **S** falsche Daten, Datenverlust, Sicherheitslücke, Hänger · **M** falsches Verhalten in realistischen Fällen oder klare Wartungslast · **K** Kleinkram.

Build und Tests auf dem Snapshot: `dotnet build -warnaserror` ohne Warnung; 4848 Tests grün (Core 3438, UI 1044 inkl. JS, Integration 366 mit Docker), +999 seit R8; Coverage Zeilen 90,8 %, Zweige 85,2 % (Core 93,4 %, UI 84,2 %, AppTrace 77,8 %) – je knapp ein halber Punkt unter 3.54.

## Ohne Befund geprüft (Auszug)

- **Sperren (ADR 0006/0019/0021):** Alle neuen Lesewege seit 3.54 laufen über `ExecuteReaderAsync`/`GuardReadOnly` und stehen in den `ReadOnlyTests`:
  - `AS OF SCN` in `QueryBuilder.From`, Daten-Diff mit `MINUS`, SQL-Bedingung, `V$SQL`/`V$SQLSTATS`/Cursor-Pläne, Synonyme, Grants, Tablespaces, View-Texte, `ReadSession`.
  - Synonym-/Grant-DDL, Trigger an/aus und der DDL-Lauf des Schema-Vergleichs gehen über `ExecuteDdlAsync`, jeder Schritt einzeln.
  - PK-Ändern und Zurücksetzen (WP-78) gehen über Flush, ohne neuen Schreibweg. `OracleCommand` entsteht nur in `OracleSession`.
  - Umgehungsversuche scheitern: Kommentare zwischen `FOR`/`UPDATE`, `;` in Literalen, `WITH FUNCTION`, Systemtrigger, `DROP … PURGE/CASCADE/INCLUDING`. Lücken: A3, A17.
- **SQL und DDL am richtigen Ort:** Neues SQL nur in `Core/Query`/`Core/Oracle`, DDL nur in `Core/Ddl`. Werte als Binds mit eindeutigen Namen, Namen gequotet, Sortierung PK → ROWID auch in Diff, Design und Top-Liste. `WhereCondition.Problem` lehnt alles ab, was die Lesesperre ablehnt.
- **SQL-Skripte in geteilten Ordnern (WP-77/84):**
  - Pfade nur über `ScriptPaths.Resolve` (unter der Wurzel, keine Links, Tiefe ≤ 8); Grenzen 1 MB, 2000 Dateien, 10.000 Einträge.
  - Werte aus der Zelle immer als Binds (höchstens 1000).
  - Auto-Run auch auf Prod nur bei `IsReadOnly`, erneut geprüft auf dem Editortext; DML wird nur geöffnet. Gemerkte Parameterwerte bleiben im Speicher.
- **Schreiben ins C#-Projekt (WP-41):**
  - Nur unter der Klon-Wurzel, ohne Links; Hash unverändert seit der Vorschau, Sperre geprüft.
  - Neue Dateien über `File.Move(overwrite: false)`, alles oder nichts. Der verknüpfte Klon wird abgelehnt.
  - `git status` mit `ArgumentList`, `--literal-pathspecs`, fsmonitor aus, Zeitgrenze, Job Object.
- **Nebenläufigkeit:**
  - Serverlast (eigene Session, Zeitgrenzen je Blick, kein Wiederholen nach Ablehnung, Dispose), `ReadSession` (Open/Detach unter Lock, Leerlauf-Timer), `DataDiffTab`, `SchemaCompareService` über den Neuaufbau, `ScriptLibrary`, WP-81, WP-69/74.
  - Alle neuen JSON-Dateien über `AtomicJsonFile`/`VersionedJsonFile`, Tabelle `architecture.md` 1.7 vollständig.
- **Host:**
  - Startreihenfolge in `Program.Main` mit In-Band-Breaks (ADR 0041) und RDP-GPU-Schalter (ADR 0043) vor `new App`; kein Link setzt einen Schalter.
  - `WebViewCapture`/`CopyFence` (vtable-Plätze geprüft), ModelHost-Grenzen, Builds ohne Build-Server, alle Prozessstarts mit `ArgumentList`.
  - Kein anfälliges Paket, Pkcs-Pin weiter nötig, Roslyn 5.9/4.11 wie ADR 0050. CI-Concurrency, Release-Tagprüfung und SHA-Pins stimmen.
- **Redaction und Darstellung:** SQL-Verlauf, Fehlerlog und Fehlerdialog auf Prod ohne Werte bzw. Literale; neue Logzeilen ohne Werte. Kein `MarkupString` aus Daten, `grid.js` nur `textContent`. Fremde SQL-Texte der Serverlast nur als Text.
- **Bestandsaufnahme UI:**
  - 3799 resx-Keys alle genutzt, Deutsch vollständig, Platzhalter gleich.
  - 92 JS-Exporte und 42 `[JSInvokable]` alle genutzt und über `Guard`; 79 Icons genutzt; 794 Parameter gesetzt; keine ungenutzten privaten Member.
  - 1057 CSS-Klassen, tot nur eine (B10); alle `var(--…)` definiert. Jede Einstellung wirkt.
  - WebView2-Fallen eingehalten (kein `@ondblclick`/`@onauxclick`, Capture beim Ziehen). Alle Dialoge auf `ModalFrame`, alle neuen Menüs auf den Bausteinen.
- **Doku:** 169 relative Markdown-Links leben, 99 von 100 Regel-Globs treffen (D2), ADR 0001–0050 lückenlos, 115 CHANGELOG-Abschnitte = 115 Tags. Jede „Added“-Funktion seit 3.55 steht im README (bis auf D8). Kein Name aus der Kundendatenbank gefunden.
- **Fixes aus 3.54 greifen:** A1–A29 (DB-, ST-, UI-, PH- und SE-Anteile einzeln geprüft); B1–B10 wie entschieden (B2-Rest im Backlog); C1–C7, C9, C10, C13, C14, C8 bis auf die entschiedene Sekunde; D1–D13 bis auf D4, D7 und D9, die mit neuen Paketen wieder auftreten (D1, D3, D4 hier). Nur teilweise: C11 (C8 hier).

## Teil A – Fehler und Sicherheit

### Schwer

Keine.

### Mittel

| # | IDs | Ort | Befund | Sicherheit |
|---|---|---|---|---|
| A1 | ST1 | `Components/Grid/FerretGrid.razor:29`, `Components/Sql/RunProgress.razor:174, 217-234`, `State/Workspaces/WorkspaceLifecycle.cs:75-87`, `Core/Connections/ReadSession.cs:162-168`, `WorkspaceLifecycle.TimeTravel.cs:31-33, 112`, `Shell/DataDiffView.razor:24-27` | **„Session trennen“ im Zeitreise-Tab trennt die falsche Session.** Der Zeitreise-Tab liest auf der Lese-Session (`Reads.AsOf`). Sein `RunProgress` ist aber an die Workspace-ID gebunden.<br>Szenario: Der Workspace hat geflushte Änderungen, das VPN wird still, der Zeitreise-Tab hängt. Nach 5 s bietet er „Session trennen“ an; nach der Rückfrage ist die Schreib-Transaktion des Workspaces zurückgerollt, und der Zeitreise-Tab hängt weiter. Ohne Änderungen fehlt sogar die Rückfrage.<br>Die Lese-Session selbst hat weder Zeitgrenze noch Trennen (`ReadAsync` ohne `WaitAsync(token)`). Alle Diff- und Zeitreise-Lesewege warten hinter ihr; `PointAsync` läuft mit `CancellationToken.None`.<br>Vorschlag: `ReadSession.ResetAsync()` (Session sofort heraus, im Hintergrund schließen). `RunProgress` bekommt das Ziel des Trennens als Parameter; das Grid setzt es bei `TimeTravel` auf die Lese-Session, ohne Verlust-Rückfrage. Im Diff-Tab dasselbe Angebot. `PointAsync`/`CheckTimeTravelAsync` mit Token. Test mit einer Fake-Session, die den Abbruch ignoriert. | nachgeprüft (Code) |
| A2 | PH1 | `App/Views/MainWindow.xaml.cs:21-27, 53-71`, `UI/Shell.razor:341-342` | **Absturz des WebView2-Renderers: Fenster tot, mit offenen Änderungen nicht mehr schließbar.** Kein Handler für `CoreWebView2.ProcessFailed`.<br>Szenario: Eine Transaktion ist offen, `msedgewebview2.exe` stürzt ab (oder `Page.crash`). Die Seite ist leer, .NET läuft weiter. Beim Schließen liefert `CanExit` false, und `RequestExit` stellt die Commit/Rollback-Frage in der toten Seite. Das Fenster geht nur noch über den Task-Manager zu, die offene Transaktion ist verloren. `ui.md` hält „die App behandelt `ProcessFailed` nicht“ als Tatsache fest.<br>Vorschlag: `ProcessFailed` loggen; bei `RenderProcessExited`/`Unresponsive` `Reload()` (BlazorWebView hängt die Root-Komponenten neu an, der Zustand liegt in .NET), sonst nativ fragen und über `StartAgain` neu starten. In `Closing` bei toter WebView eine native Frage (Commit/Rollback/Abbrechen). → Frage 2. | nachgeprüft (Code), Verhalten nach dem Absturz nachzustellen (`Page.crash`) |
| A3 | DB1 | `Core/Query/SqlEditor/SqlClassifier.cs:105-115, 129`, `Core/Oracle/StatementGuard.cs:28-40` | **Nie-Liste des Schemawegs unvollständig.** Zwei DDL-Formen entfernen Daten unwiderruflich und laufen trotzdem als normales DDL:<br>• `ALTER TABLE … MOVE [ONLINE] INCLUDING ROWS WHERE …` (ab 12.2), ebenso `MOVE/SPLIT PARTITION` und `MERGE PARTITIONS … INCLUDING ROWS`. Zeilen außerhalb der Bedingung fallen weg, Flashback scheitert danach mit ORA-01466. `DropsPartition` sucht nur `TRUNCATE`/`DROP`.<br>• `DROP TYPE … FORCE`: abhängige Spalten anderer Tabellen werden UNUSED. Ähnlich `ALTER TYPE … DROP ATTRIBUTE … CASCADE`.<br>Szenario: Eine Tabelle mit den Zeilen 1–10, `ALTER TABLE t MOVE INCLUDING ROWS WHERE id <= 5` im SQL-Editor, normale DDL-Bestätigung – danach sind 5 Zeilen weg.<br>Vorschlag: Zuerst im eigenen Container messen (Integrationstest). Dann nach Entscheidung als `Truncate` einordnen, mit Tests in `SqlScriptTests`/`ReadOnlyTests`, `ddl.md` und ADR 0019 nachziehen. → Frage 1. | nachgeprüft (Code); Oracle-Verhalten laut Doku, nicht gemessen |
| A4 | RE1 | `Core/ClrModel/Usage/WorkCloneMapping.cs:106-134, 158-188` gegenüber `ClrModel/CodeEdit/CodeScan.cs:395-431` | **Die Dateisuche der Code-Verwendung folgt Junctions und Symlinks.** Die Kopie in WP-41 (`CodeScan`) überspringt Links („a junction in the work clone could lead anywhere“), das Original `SourceFiles` nicht.<br>Szenario: Im Arbeits-Klon liegt eine Junction auf ein anderes Repository oder ein `mklink /D` auf eine Freigabe. In „Verwendung“ führt ein Klick auf eine verschobene Datei zu `FindTypeFile`. Das liest alle `.cs` hinter dem Link und öffnet die erste passende Klasse außerhalb des Klons in VS bzw. VS Code. Auf einer Freigabe meldet sich `File.ReadAllText` dort an – die Lücke, die A9/A29 in `Resolve` geschlossen haben.<br>Vorschlag: ein gemeinsamer Ordnerlauf ohne Links (B2), Test mit Junction in `WorkCloneMappingTests`. | nachgeprüft (Code); SMB-Anmeldung Verdacht |
| A5 | RE2 | `UI/State/Connections/ServerLoadService.cs:543-560` gegenüber `Core/Connections/ReadSession.cs:231-258`, `ActiveConnection.cs:151-188`, `Compare/SchemaCompareLoader.cs:62-87` | **Die Serverlast vergisst ein falsches Sitzungspasswort nicht.** Drei Wege vergessen ein nur für den Lauf eingegebenes Passwort nach ORA-01017 (`ForgetSessionPassword`), `ServerLoadService` tut es nicht.<br>Szenario: Das Passwort wurde inzwischen geändert, die Hauptsession lebt weiter. Die Serverlast öffnet ihre Session: Ablehnung, die Schleife steht. Jeder Klick auf „Aktualisieren“ versucht es mit demselben falschen Passwort (`FAILED_LOGIN_ATTEMPTS`); nach wenigen Klicks ist das Konto gesperrt, auch für die Kollegen.<br>Fehlt das Passwort, meldet die Seite das mit dem Text des Schema-Vergleichs (`CompareText.NoPasswordFor`).<br>Vorschlag: ein Core-Baustein für „eigene Session öffnen“ (B1) mit ORA-01017 → vergessen. Test: ORA-01017, dann fragt der zweite Versuch nach dem Passwort. | nachgeprüft (Code) |
| A6 | UI1 | `UI/Shell.razor:121-128`, `Components/Schema/GrantListView.razor:54, 87, 176-180`, `SynonymListView.razor:45` | **Versteckte Grant- und Synonym-Tabs rendern bei jeder Shell-Änderung ganz neu.** Sie haben kein `ShouldRender` und berechnen alles im Markup neu: alle Grants des Schemas, `GrantRow` je Grant und eine `<datalist>` mit allen DB-Usern.<br>Szenario: „Berechtigungen im Schema“ mit einigen tausend Grants ist offen. Jede Zellbearbeitung in einem anderen Tab baut die versteckte Tabelle samt `datalist` neu auf.<br>Vorschlag: wie `DataDiffView.razor:246` (`ShouldRender => Shell.ActiveTab == Tab`, `Shell.Changed` selbst abonnieren); Zeilen und Empfänger je Daten- und Filterstand halten. | nachgeprüft (Code); Kosten nicht gemessen |

### Klein

| # | IDs | Befund |
|---|---|---|
| A7 | ST2 | Schema-Vergleich: Wird die Seite während des DDL-Laufs neu aufgebaut (z. B. Verbindungswechsel per `ferretsharp://`-Link), zeigt das neue `DdlDraftPanel` wegen `ConfirmAtOnce` sofort wieder „Ausführen“. `SchemaCompareService.DdlRunning` wird dabei nicht geprüft. Bestätigt der Nutzer, läuft dieselbe Schrittliste ein zweites Mal. → `ConfirmAtOnce && !Compare.DdlRunning`, während des Laufs „läuft auf …“. (Auslöser Verdacht) |
| A8 | ST3 | WP-41: In den 400 ms nach einer Änderung ist der alte Plan noch schreibbar. Ein Klick auf „In Projekt schreiben“ schreibt den alten Plan, danach plant der neue gegen den halb geschriebenen Klon. `Dispose` bricht `CodeChangeWriter` zwischen zwei Dateien ab, entgegen „ganz oder gar nicht“. → Sofort auf `Planning` setzen, nach der Pause erneut gegen `Writing` prüfen, das Token nur bis zur ersten geschriebenen Datei wirken lassen. (Häufigkeit Verdacht) |
| A9 | SE1 | `Scripts/ScriptHeader.cs:188, 205, 278`: Beim Speichern eines Skripts kann eine Kopfzeile in den Rumpf übergehen. `ParameterName` läuft ohne `OneLine`, und `OneLine` ersetzt nur CR/LF. Ein gequoteter Bind `:"a⏎DELETE FROM t"` oder eine `Other`-Zeile mit U+2028 bricht den Kopf; die Zeile landet in der geteilten Datei als Rumpf. Der Auto-Run bleibt geschützt (`IsReadOnly`, `StatementGuard`). Muster von A11 (3.54). → `OneLine = ReplaceLineEndings(" ")` auf Name und `Other`; Test `Parse(Compose(…)).Body` unverändert. |
| A10 | SE (offen) | `ServerLoad/ServerLoadExport.cs:69` und die Detailansicht übernehmen den SQL-Text fremder Sessions aus `V$SQL` ohne `SqlRedaction.Redact`. Behält Oracle `IDENTIFIED BY …` im Text und landet das Statement in der Top-Liste, steht ein Passwort im Export. → Im Container prüfen; falls ja, `Redact` beim Holen des Texts. (Verdacht) |
| A11 | PH2 | `ClrModel/CodeEdit/CodeTarget.cs:77`: `git status --untracked-files=no` – ungetrackte Dateien, etwa von WP-41 gerade angelegte Entities, bekommen keine Warnung „nicht committet“. Rename-Einträge mit `-z` werden falsch geschnitten (`e[3..]`). → `--untracked-files=all`, `??` werten, R/C richtig parsen, Test. (nachgeprüft vom Agenten im Scratch-Repo) |
| A12 | PH3 | `CodeTarget.cs:69-76`: ohne `StandardOutputEncoding = UTF8` (anders als `DotNetCli`). Ist der `CodePagesEncodingProvider` registriert (beim Lesen eines ANSI-Skripts), wird ein Pfad mit Umlaut falsch gelesen, und die Warnung fehlt still. (Verdacht) |
| A13 | PH4 | Modellseite „Neu bauen“ wartet still hinter der app-weiten Build-Sperre (z. B. „Projekte bauen“ einer anderen Verbindung) und lässt sich nicht abbrechen (`ClrModelManager.cs:379` ohne Token). → Meldung „wartet auf anderen Build“ und Abbrechen. |
| A14 | PH5 | `global.json` (`10.0.400`, `latestFeature`): Ein lokaler self-contained Publish mit SDK 10.0.401 bündelt Runtime 10.0.12, ohne den Brotli-Fix aus 10.0.14. Die CI ist richtig. → Frage 5. |
| A15 | PH6 | `App.xaml.cs:273-311`: `UiHangWatch` probt beim Beenden weiter, während `OnExit` bis zu 13 s blockiert. Danach steht eine falsche Hänger-Warnung im Log. → Watch und `UiStallMonitor` als Erstes stoppen. (Verdacht) |
| A16 | PH7 | `CodeEditorLauncher.cs:230-231`: `ReadToEnd()` vor `WaitForExit(5000)` – die 5-s-Grenze für vswhere wirkt nicht. → `ReadToEndAsync().WaitAsync(5 s)`, dann `Kill`. (Verdacht) |
| A17 | DB2 | `CREATE INDEX … ONLINE`/`ALTER INDEX … REBUILD ONLINE` ohne die Sperrprobe aus ADR 0019; sie warten auf offene Transaktionen anderer Sessions. Betrifft nur getipptes SQL. → Im Container nachstellen, bei Hänger die Probe ausweiten. (Verdacht) |
| A18 | DB3 | `OracleServerLoadReader.cs:139-145`: Die Statement-Info liest mit einer IN-Liste variabler Länge – bis zu 100 Varianten eines Statements im Shared Pool der überwachten DB. → Auf feste Größen auffüllen wie `KeyIn`. |
| A19 | DB4 | `Data/DataDiffErrors.cs:21`: ORA-22924 (LOB-Version zu alt) fehlt in `UndoTooShort` → roher Oracle-Fehler statt Erklärung. (Verdacht) |
| A20 | UI2 | `Grid/ScriptTargetSection.razor:72`: `$"{ScriptViewText.Root_Group} {root.Group}"` statt `TextFormat.Format` → Tooltip „Gruppe {0} Team · …“. |
| A21 | UI3 | `Shell/TimePointDialog.razor:24, 72`: Die Markierung der Schnellwahl vergleicht mit `Preset(m, now)` je Render. Sie verschwindet, sobald der Hinweis zur Undo-Reichweite nachkommt. → Gewählte Schnellwahl merken. |
| A22 | UI7 | `Schema/ProjectCodeChangeView.razor:226, 228`, `Shell/TabBar.razor:445`: verworfenes `InvokeAsync(() => Replan(0))` statt `Fire` – Ausnahmen unbeobachtet. (Verdacht) |
| A23 | RE6 | `Scripts/ScriptFiles.cs:225`: Ein Skript gilt als CRLF, sobald **eine** CRLF-Zeile vorkommt. Ein LF-Skript mit einer eingefügten CRLF-Zeile wird beim Speichern ganz CRLF, `git diff` zeigt die ganze Datei. `CodeFile` nimmt die Mehrheit. → Gemeinsame Regel (B4). |
| A24 | RE5 | `ServerLoadPage.razor:205`: Uhrzeit fest `HH:mm:ss` – bei en-US 24 h, im Rest der App 12 h mit AM/PM. → `TimeText` (B8). |
| A25 | PH8 | `tools/e2e/Invoke-RealClick.ps1:21-23, 69-70` nimmt jede PID. Mit der PID der echten Instanz klickt es echt in deren Fenster (Verbindungen, Commit, Prod), entgegen CLAUDE.md §6. → Pflichtparameter `-Root`, Pfadprüfung wie `Stop-TestInstance.ps1`. |

## Teil B – Struktur und Wiederverwendung

| # | IDs | Vorschlag | Aufwand |
|---|---|---|---|
| B1 | RE2 | **„Eigene Session öffnen“ einmal:** Passwort, `SessionOpening`, Ablehnung gegen Netzfehler, ORA-01017 → vergessen, optional Read-only-Snapshots. Heute fünf Kopien (`ReadSession`, `ActiveConnection`, `SchemaCompareLoader`, `ServerLoadService`, teils `OracleConnectionTester`), die schon auseinanderlaufen (A5). Core-Baustein `OwnSession.OpenAsync`; der Backlog-Punkt „`SchemaCompareLoader` ohne `SessionOpening`“ geht darin auf. | S–M |
| B2 | RE1 | **Ein Ordnerlauf für Quelltextdateien** (`bin`/`obj`/Punktordner und Links übersprungen), z. B. `UserFolderPaths.SourceFiles`, für `WorkCloneMapping` und `CodeScan` (A4). | S |
| B3 | RE3 | **Entprellter `FileSystemWatcher` dreimal** (`BuildOutputWatcher`, `DisplayColumnSource`, `ScriptLibrary`), mit abweichender Puffergröße und Fehlerbehandlung. Der Name `BuildOutputWatcher` passt nicht mehr (beobachtet auch die Modelldatei). → `IO/QuietWatcher`. | M |
| B4 | RE6 | **Textdatei-Format einmal:** `IO/TextFileFormat` (BOM, Kodierung, Mehrheits-Zeilenende) für `ScriptFiles` und `CodeFile` (A23). Dazu `AtomicFile.CreateInUserFolderAsync` für „neu anlegen“: `ScriptWriteMode.New` prüft und überschreibt dann, `CodeChangeWriter` legt ohne Überschreiben an. | S–M |
| B5 | RE7 | **Projektdatei-Logik von WP-41 zusammen:** Zwei Läufe zur nächsten `.csproj` mit verschiedener Regel (mehrere `.csproj` im Ordner: „unbekannt“ bzw. die erste in unbestimmter Reihenfolge); `IsSdkProject` sucht im Text statt im XML. → `CodeEdit/CodeProject.cs` mit `Of`, `Nullable` und `IsSdk` über `XDocument`. | S |
| B6 | RE9 | **Aktionen der zwei Berechtigungs-Ansichten** (`GrantListView`, `GrantsView`: `NextAction`, `_reading`/`_closing`/`_action`, „Synonyme vor Grant/Revoke lesen“, der Fix aus dem dritten WP-71-Durchgang zweimal) → State-Klasse `GrantActions` mit Tests. | S–M |
| B7 | RE4 | `TableKind` → `"TABLE"/"VIEW"/"MATERIALIZED VIEW"` fünfmal, obwohl `OracleDdl.ObjectKeyword` existiert → `TableKind.ObjectType()` in `Core/Schema`. | S |
| B8 | RE5, RE11 | Formatierer in Komponenten: `ConditionBar.OneLine` ist `SqlScript.OneLine`; `ServerLoadPage.Clock`, `ReportPage`, `PlanTable.Time` → `TimeText.Short`/`WhenAt` (A24). Der Serverlast-Filter (`ServerLoadModel.cs:222`) sucht mit `Contains` statt `TextSearch` („kunden verwaltung“ findet `Kundenverwaltung` nicht, im Skriptbaum schon). | S |
| B9 | RE8 | `AuditTemplateSource.GroupEntry` ist die allgemeine Suche „Datei je Verbindungsgruppe“ (Skripte, Bericht, Gruppen-Umbenennen) → nach `Connections`. | S |
| B10 | RE10, UI5 | Tot: `.mismatch-toolbar` (seit WP-72). Nur für Tests öffentlich: `ScriptPaths.RelativeOf`; ungenutzt: `ReadSession.HasSession` → `internal` oder weg. `.mismatch-sev`/`.report-tag` schreiben den Font-Stack von `body` aus → `inherit`. | S |
| B11 | ST4 | `QueryDesignPanel.CancelPreview` baut `CancelOffThread` von Hand nach (B10 aus 3.54) → `CancelOffThread.Run`. | S |
| B12 | RE12 | Gleiche Sätze in zwei Bereichen, entgegen `localization.md`:<br>• `ShellText.DataDiff_NotConnected` = `ConnectionText.ReadSessionNotConnected`<br>• `ScriptText.ErrorInLine` = `AuditText.ErrorInLine` (beide → `CoreText`)<br>• `SchemaViewText.TablePicker_More*` = `ShellText.Palette_More*`<br>Optional die kurzen Beschriftungen (`Explorer_Count*`, `Paths_CopyText`, `Query_OpenInLinq`). | S |

Nicht aufgenommen: lange Methoden (`NewEntityCode.Plan` 130 Zeilen, `RestoreAsync` 106, `EntityCode.Write` 106) und große Dateien (`EntityCodeChanges.cs` 667, `CodeScan.cs` 435) – laut RE in kommentierte Schritte gegliedert und zusammenhängend; nur die Projektdatei-Helfer gehören heraus (B5).

## Teil C – Tests

| # | IDs | Befund / Vorschlag |
|---|---|---|
| C1 | TE1 | **M** WP-41: Die Sperren gegen kaputtes Schreiben ins Projekt sind ungetestet. Betroffen: überlappende Änderungen und „mehr Syntaxfehler als vorher“ (`CodeChangePlan.cs:181-207`), `!CanWrite`, eine Datei, die selbst ein Link ist, und das Aufräumen der Temp-Datei (`CodeChangeWriter.cs:40, 99, 168`), Größe/UTF-16/binär (`CodeFile.cs:91-130`). Fällt die Syntaxprüfung weg, schreibt eine falsche Spanne ungültiges C# in den Klon. → Fälle in `CodeChangePlanTests`/`CodeChangeWriterTests`, Round-Trip UTF-16. |
| C2 | TE2 | **M** WP-41-Änderungspfade ohne Test:<br>• `RemoveAttribute` (`EntityCodeChanges.cs:455-490`)<br>• `[Required]` weg bei NOT NULL → NULL (`:219`, Review-Fix M4 nur zur Hälfte gehalten)<br>• NULL → NOT NULL (`:321`), `[Precision]`/`[Column(TypeName)]`<br>• `using` im Kontext bei anderem Namespace (`NewEntityCode.cs:326`), Kollision des DbSet-Namens, Hinweise „registrieren“<br>Ein fehlendes `using` oder ein doppeltes DbSet lässt das Projekt nicht mehr bauen. |
| C3 | TE3 | **M** WP-78: Die Sperre „Struktur geändert“ beim Zurücksetzen (`WorkspaceLifecycle.TimeTravel.cs:195-198`), die Zählungen „übersprungen“ und der Abbruch sind ungetestet. Ohne die Sperre legt `tracker.Restore` Werte nach Position in falsche Spalten. → UI-Test mit neuer Spalte. |
| C4 | TE4 | **M** `ScriptLibrary`: `MaxEntries` (Review-Fix M5), `MaxFiles` und die Watcher-Regeln (Fix aus dem dritten Durchgang) ohne Test; Timer und Watcher auf der echten Uhr. → `TimeProvider`, Grenzen `internal` setzbar. |
| C5 | TE5 | **M** `ServerLoadService`: `LoadSinceLoadAsync` ganz ungedeckt; „eine nach dem Abbruch geöffnete Session wird geschlossen“ ungetestet (ohne das bleibt eine Session auf Prod offen); `LookupTimeout` auf der echten Uhr. → Connector, der nach dem Abbruch liefert; Grenze über `_time`. Dazu der Test zu A5. |
| C6 | TE6 | Fixes ohne Test, der ohne sie scheitert:<br>• WP-78 #7 (`ExplainActualAsync` mit `AS OF`, `OracleDataAccess.cs:240`)<br>• WP-78 #10 (Zeitreise-Tab lädt nach Commit nicht neu, `ShellState.Tabs.cs:890`) und der Abbruch des Zurücksetzens<br>• fix-grid-paste R1 (LOB-Vorschau zählt nicht als „NULL kopiert“), nur E2E<br>• WP-77 M5 und WP-41 M4 (C2, C4)<br>Bewusst ohne Test bleiben die App-Host-Fixes und Razor-Fixes, wie in den Protokollen begründet. |
| C7 | TE7 | `Integration.Tests/AppTraceSourceTests.cs:187-191` liest den Plan eines fremden Cursors direkt – dasselbe Muster wie der geflackerte Serverlast-Test. → Über `CursorPlans.ReadAsync` mit erneutem Lesen, oder den Assert auf „Plan oder Hinweis“ lockern. (Verdacht) |
| C8 | TE8 | C11 aus 3.54 nur teilweise umgesetzt: Sieben eigene Warteschleifen statt `Wait.UntilAsync`:<br>• `WorkspaceManagerTests:606`, `ActivityLogTests:49`, `CodeUsageServiceTests:60`, `LinqConsoleRestartTests:113`<br>• Integration: `WorkspaceSessionTests:95`, `EditingTests:264`, `ModelReloadTests:85`<br>Dazu `KeyChangeQuestionTests.cs:256-260` (`Advance` + `Task.Delay(20)` statt `CountingTimeProvider`). |
| C9 | TE9 | Eine Regression hängt die Suite auf, statt zu scheitern: `CancelOffThreadTests.cs:16-26` (`release.Wait(Ct)`), `SchemaCompareServiceTests` (sechs `WaitAsync(Ct)` ohne Limit). Die CI bricht erst nach 30 min ab, ohne Testnamen. → `WaitAsync(Wait.Limit, Ct)`, in `ci.yml` `--long-running`. |
| C10 | TE10 | `SafeDeleteTests.cs:152` sieht `.js`/`.mjs` nicht (`tools/e2e/cdp.mjs`). → Endungen und das Muster `rmSync`/`fs.rm` mit `recursive` aufnehmen. |
| C11 | TE11 | Echte Uhr in neuem State-Code: `TimePoints.Back(chosen, DateTimeOffset.Now)` (`WorkspaceLifecycle.TimeTravel.cs:23`), `DataDiffState.StartedAt`; die Tests rechnen mit `DateTimeOffset.Now`. → `TimeProvider` durchreichen. |
| C12 | TE12 | Unnötig langsam:<br>• OOB `…loses_urgent_data…` 23 s (`LEVEL <= 100` → 40, Verdacht)<br>• `LinqFilterGroupTests` 21 s für 6 Fälle (ModelHost je Fall zweimal → Class-Fixture)<br>• `ServerLoadTests.A_waiting_session…` 4 s (`command.Cancel()`)<br>• `RowExportTests` volles Blatt 3,1 s (Blattgrenze setzbar) |
| C13 | TE13 | Weitere entscheidende Zweige ohne Treffer:<br>• `DataDiffTab` (kein Vergleich während des Zurücksetzens und umgekehrt)<br>• Persistenz des Diff-Tabs und Wiederherstellen des LINQ-Tabs<br>• „Tabelle aus Diff entfernen“<br>• `ProjectCodeChange.ForNewEntity` (Planen, Fehler, Abbruch)<br>• `ReadSessionData` (Theory per Reflexion über alle `IDataAccess`-Methoden) |
| C14 | TE14 | `Core.Tests/Query/SqlBindListsTests.cs` → `Query/SqlEditor/` (Quelle liegt dort). Sonst eine Klasse je Datei, sprechende Namen. |
| C15 | UI6 | Zwei Ziehgriffe nie mit echter Maus geprüft (`RelationPathsPanel.razor:10`, `QueryDesignPanel.razor:21`; Nachtrag 3.62.0 in `small-resizable-panels.md`: „kein E2E“), weder in Backlog noch Roadmap. → Im E2E von R9 mit echter Maus. |

**Überflüssig**

| # | IDs | Test(s) | Vorschlag | Fängt den Fehler danach |
|---|---|---|---|---|
| C16 | TE | `Settings/SettingsStoreTests.cs:38-47` `Theme_round_trips` (3 Fälle) – prüft die Enum-Serialisierung der Bibliothek | entfernen | `Every_setting_round_trips` (Theme vom Standard weg), `File_is_versioned_and_readable` (`"theme": "dark"`) |
| C17 | TE | `Compare/SchemaDiffTests.cs:227, 236`; `Workspaces/ColumnPinningTests.cs:20, 24, 28, 58`; `Query/Search/ObjectSearchTests.cs:44, 51, 55`; `Data/SnapshotsTests.cs:10, 34, 42` – je gleiches Arrange/Assert mit anderem Wert | je ein `[Theory]` | dieselben Fälle als InlineData |

Keiner dieser Tests hält einen Review-Befund oder Bugfix fest. Die seit 3.54 neuen Tests verstoßen nicht gegen „Redundante Tests“ in `tests.md` (Skript über Rümpfe, die sich nur in Literalen unterscheiden: kein Treffer).

Zehn langsamste Tests: OOB 23,0 s / 7,9 s (C12), `ModelReloadTests` 12,2 s (echter Build), `SessionActivityTests` 9,1 s (R8 entschieden), `LinqFilterGroupTests` 6,6 / 4,8 s (C12), `ViewCompareTests` 4,1 s (erster Snapshot), `ModelHostProcessTests` 4,0 s (R8 entschieden), `ServerLoadTests` 4,0 s (C12), `ObjectDetailsTests` 3,8 s (DBMS_METADATA).

## Teil D – Doku und CLAUDE.md

| # | IDs | Befund / Vorschlag |
|---|---|---|
| D1 | DO1 | **M** Der Release-Stand im Index und in den Protokollen wird nicht gepflegt (D7 aus 3.54 wieder). Im Einzelnen:<br>• WP-41 und WP-84 stehen mit „–“ statt 3.70.0.<br>• Neun Köpfe nennen einen falschen Stand: wp-68/70/71 → 3.57.0; wp-82/83, fix-cursor-plan-child, fix-ci-concurrency → 3.69.0; wp-41/84 → 3.70.0.<br>• Sieben Protokolle haben gar keinen Release.<br>• Die v4-Zeile endet bei WP-66.<br>Ursache: Weder die Release-Skill noch `release.md` hat einen Schritt dafür; Schritt 7 der Skill zählt nach genau dieser Spalte. → Die Stellen berichtigen, Schritt in Skill und `release.md` aufnehmen (Frage 6). |
| D2 | DO2, RE13 | **M** Regeln erreichen ihre Dateien nicht:<br>• Der Glob `UI/State/Schema/ColumnDesigner*` in `ddl.md` ist seit R8 tot (`TableDesignerDraft`, `Design*`, `AuditDesignRules`).<br>• Die Flashback-Fallen in `oracle.md:51-57` laden nicht für `DataDiff*`/`DataRestore`/`TimePoints`/`ChangeTracker.Restore`, die Serverlast-Fallen (`oracle.md:70, 74`) nicht für `Core/ServerLoad/**`.<br>• Ohne Regel: `Core/Relations`, `Core/AppTrace`, `Core/Settings`, Projekt AppTrace.<br>→ Glob berichtigen, neue Regeln `flashback.md` und `server-load.md` (aus `oracle.md` heraus). |
| D3 | DO3 | **M** Die Regeln wachsen wieder: 115,8 KB (~33k Tokens, +44 % seit R8). `ui.md` 17,9 KB, `oracle.md` 16,9 KB. Schlimmster Fall: `LobEditorDialog.razor` lädt 37 KB (~10,6k Tokens, 3.55: 23 KB). `ui.md:52` ist eine Zeile mit 2,8 KB; der Host-Abschnitt `ui.md:49-55` (4,9 KB) betrifft nur `FerretSharp.App`. → `app-host.md` mit Glob `src/FerretSharp.App/**`; `oracle.md:69` (Keyset-Paging, doppelt zum Backlog) streichen. Mit D2: `oracle.md` ~11 KB, `ui.md` ~13 KB. |
| D4 | DO4, PH9, RE13 | `CLAUDE.md` (12,97 KB, ~3,7k Tokens, +0,4 KB seit R8; D9 aus 3.54 wieder):<br>• §4 ohne `Core/Scripts/`, `ClrModel/Report/`, `ClrModel/CodeEdit/`, `tools/e2e/`, `tools/linux/`<br>• §3 ohne Roslyn (5.9 im Core nur für Syntax, 4.11 im ModelHost, ADR 0050)<br>• `docs/sql-scripts.md` weder in „Wo steht was“ noch in `user-files.md`<br>Was umziehen könnte (~1 KB): die Zeilen „Excel-Export“ und „App-Mitschnitt“ aus §3, sobald eine Regel diese Bereiche abdeckt; die TRUNCATE-Liste in §2 (steht in `ddl.md`). |
| D5 | DO5 | `architecture.md:246ff`: `SaveScript`, `TimeTravel`, `ToggleCondition` fehlen in der Tastentabelle. |
| D6 | DO6 | `audit-templates.md:169`: `ColumnDesigner.AuditAtStart` → `AuditDesignRules.AuditAtStart`. |
| D7 | DO7 | `backlog.md:22` (Indexnamen schemaweit prüfen) ist seit R8 erledigt → streichen; `:215` (Code ins Projekt einfügen) teils durch WP-41 erledigt → einschränken. |
| D8 | DO8 | README:<br>• Bilder aus 3.48.0 (Synonyme, Rechte, Bericht, Serverlast, Skripte, neue Tab-Leiste fehlen); Diagrammbild weiter offen (Backlog).<br>• „LINQ-Konsole nimmt `using`-Zeilen“ (3.58.0) fehlt.<br>• Z. 412 nennt Roslyn nur für die LINQ-Konsole.<br>• Z. 175 nennt „manual“ relationships, die niemand anlegen kann (`FkSource.Manual` ohne Erzeuger).<br>→ Sätze anpassen; Bilder nach `e2e-testing.md` › README-Screenshots neu (Aufwand M). |
| D9 | DO9 | Der Index steht nicht in Release-Reihenfolge (fix-ui-hang-diagnostics, fix-webview-rdp-front-buffer, fix-ci-concurrency) → umsortieren. |
| D10 | DO10, SE2 | `branch-code-review/checklist.md`:<br>• Quellen ohne SQL-Skripte aus geteilten Ordnern und `report-exceptions.json`<br>• Senken ohne „Schreiben ins Repository des Nutzers“<br>• Prozesse ohne `CodeTarget.DirtyAsync` (git), `FileReveal`, `CodeEditorLauncher`<br>• der Abschnitt Docs ohne README<br>→ ergänzen. |
| D11 | UI4 | 44 englische Texte mit Platzhaltern ohne `<comment>`, rund 25 davon neu seit 3.55 (`TimePoint_*`, `Restore_*`, `TimeTravel_*`, `Synonyms_DropTitle`, `DataText.TimeTravel*` …), dazu 16 ältere `Usage_*` (D14 aus 3.54 wieder). → Nachtragen und einen Test in `ResourceTests`, damit es nicht ein drittes Mal passiert. |
| D12 | DO11 | ADR 0038 und 0039 (WebView: Reflection auf private Member, WebView2-Pin, `CopyFence`) stehen auf „akzeptiert“, ohne dass eine Entscheidung des Nutzers zum Weg festgehalten ist; ADR 0040 „(Entscheidung, 2026-11-02)“ ohne „des Nutzers“. → Frage 3. |
| D13 | DO12 | `work-packages/fix-rdp-gpu.md:8` nennt Modell und Windows-Build des Laptops des Nutzers (öffentliches Repo). → Frage 4. |

## Fragen an den Nutzer

1. **A3** – `ALTER TABLE … INCLUDING ROWS WHERE …` (Move/Split/Merge), `DROP TYPE … FORCE` und `ALTER TYPE … DROP ATTRIBUTE … CASCADE` auf die Nie-Liste wie TRUNCATE (nach Messung im Container)?
2. **A2** – Nach einem Absturz des WebView2-Renderers: automatisch neu laden (Zustand bleibt in .NET) und nur beim Scheitern nativ fragen, oder gleich nativ fragen (Neu laden / Neu starten)? Beim Schließen mit toter WebView eine native Frage Commit/Rollback/Abbrechen?
3. **D12** – ADR 0038/0039 (Reflection auf private WebView-Member, WebView2-Pin, `CopyFence`) und ADR 0040: Wege bestätigen („Entscheidung des Nutzers“ nachtragen) oder auf „Vorschlag (nicht bestätigt)“ setzen?
4. **D13** – Laptop-Modell und Windows-Build in `fix-rdp-gpu.md` durch eine neutrale Angabe ersetzen?
5. **A14** – Lokale Release-Builds: `global.json` auf ein SDK mit gepatchter Runtime heben (lokal scheitert der Build bis zum SDK-Update), oder ein Target, das nur den self-contained Publish unter einer Mindestversion abbricht?
6. **D1** – Den Schritt „Release-Spalte im Index und Kopf der Protokolle setzen, v4-Zeile nachziehen“ in die Release-Skill und `release.md` aufnehmen?
7. **Umfang und Reihenfolge** – wie bei 3.44/3.54 alles (Teil A bis D) in **einem** Paket R9 und einem Release, oder Fehler (Teil A) als Patch und Struktur/Tests/Doku als Minor?

## Entscheidungen des Nutzers (2026-11-12)

1. **Umfang** – Alles (Teil A bis D) in **einem** Paket R9 und einem Release.
2. **A3** – `ALTER TABLE … INCLUDING ROWS WHERE …` (Move/Split/Merge), `DROP TYPE … FORCE` und `ALTER TYPE … DROP ATTRIBUTE … CASCADE` kommen nach der Messung im Container auf die Nie-Liste wie TRUNCATE. `DROP TYPE` ohne `FORCE` und `MOVE` ohne `INCLUDING ROWS` bleiben normales DDL.
3. **A2** – Nach einem Absturz des Renderers lädt die App einmal automatisch neu (Workspaces, Sessions und Transaktionen liegen in den Scopes der Verbindungen am Singleton `ConnectionHub`, nicht im Scope der Seite). Stürzt er kurz danach erneut ab, fragt sie nativ „Neu laden / Neu starten“. Beim Schließen mit toter Seite fragt sie nativ Commit / Rollback / Abbrechen.
4. **D12** – ADR 0038 und 0039 sind bestätigt. ADR 0040: Steht die Entscheidung im Protokoll von WP-69, wird nur „des Nutzers“ ergänzt, sonst „Vorschlag (nicht bestätigt)“ und Rückfrage.
5. **D13** – Laptop-Modell und Windows-Build werden durch eine neutrale Angabe ersetzt (Hybrid-Grafik, Windows 11).
6. **A14** – `global.json` bleibt; ein self-contained Publish bricht ab, wenn die mitgelieferte Runtime unter einer Mindestversion liegt (eine Stelle in `Directory.Build.props`).
7. **D1** – Die Release-Skill und `release.md` bekommen den Schritt „Release-Spalte im Index und Kopf der Protokolle setzen“; zusätzlich zieht Schritt 1 zuerst `main`, bevor er die Commits zusammenfasst.

Für alle übrigen Punkte gelten die Vorschläge oben. Verdachtsfälle werden vor einer Änderung nachgestellt bzw. gemessen; was sich nicht bestätigt, kommt mit Begründung ins Protokoll.
