# Gesamt-Review 3.40.0 (2026-10-17)

Auftrag des Nutzers (2026-10-17): Gesamt-Review über die ganze Solution, nicht nur über die Pakete seit dem letzten Review (3.31.0) – toter Code, Duplikate, Lesbarkeit, Architektur, Sicherheit, Projekt- und Ordnerstruktur, Tests, Doku. Acht parallele Agenten nach Themen, nur lesend: DB-Zugriff (DB), Zustand/Nebenläufigkeit (ST), UI/JS/CSS (UI), Host/Prozesse/Build (PH), Sicherheit (SE), Wiederverwendung/toter Code (RE), Tests (TE), Doku (DO). Doppelte Befunde sind zusammengeführt (alle IDs genannt). **Nachgeprüft** = zusätzlich von Hand im Code bestätigt; sonst die Einschätzung des Agenten.

**Stand:** Teil A wird als R6a umgesetzt (Branch `fix/r6a-review-3.40`, Patch 3.40.1), dann Teil D (Doku), dann R6b (Teil B und C, 3.41.0), dann WP-54. Entscheidungen des Nutzers unten.

Schwere: **S** falsche Daten, Datenverlust, Sicherheitslücke, Hänger · **M** falsches Verhalten in realistischen Fällen oder klare Wartungslast · **K** Kleinkram.

## Ohne Befund geprüft (Auszug)

- Statement-Sperren (`StatementGuard`, `SqlClassifier`, `SqlTokenizer`): Kommentare, q-Quotes, Mehrfach-Statements, `WITH FUNCTION`, Hints, PL/SQL, System-Trigger – keine Umgehung. Die V$-Abfragen von WP-52 laufen über den Leseweg, WP-48/51/53 öffnen keinen neuen Weg.
- WP-50: ODER/NICHT korrekt geklammert, NULL-Semantik wie C# `!(a||b)`, 1:1 nach LINQ übersetzbar. WP-53: keine Wertverfälschung (> 15 Stellen, BigNumber, NaN, vor 1900, Sub-ms, DTO → Text; nie Formeln).
- Links (WP-37): `TableLink.Parse` mit Grenzen, Spalten gegen das Schema, Werte als Binds, Adresse nur zur Zuordnung; `CommandLine.Effective` gegen eingeschleuste Optionen; Link-Pipe und ModelHost-Pipe `CurrentUserOnly`; `pages/` mit CSP (Hashes nachgerechnet), keine Ausgabe des Link-Inhalts.
- Update-Notizen ohne HTML, `IsOwnImage` dicht, `UrlLoading` nur https nach außen; Prozessstarts nur mit `ArgumentList`; Import verwirft lokale Pfade inkl. Audit-Vorlage.
- Keine ungenutzten resx-Keys, deutsche Übersetzungen vollständig, keine toten Komponenten/JS-Exporte/`[JSInvokable]`, eine tote CSS-Klasse von 688, keine Event-Abo-Lecks.
- Alle relativen Markdown-Links leben; CHANGELOG-Abschnitte und Git-Tags 1:1; ADR-Nummern lückenlos; `pkcs`-Pin weiter nötig (Oracle 23.26.301 bringt 8.0.1).

## Teil A – Fehler und Sicherheit

### Schwer

| # | IDs | Ort | Befund | Sicherheit |
|---|---|---|---|---|
| A1 | ST-1 | `State/WorkspaceEditing.cs:134-161`, `Oracle/OracleDataEditor.cs:146-163`, `WorkspaceLifecycle.cs:268` | Prod-Commit nimmt ein **gerade laufendes** Statement ungesehen mit: Die R4a-Prüfung `shown` vergleicht `editor.Actions` vor dem Warten auf `_writing`; ein UPDATE im SQL-Tab, das auf eine Sperre wartet, steht noch nicht im Log, `CommitAsync` wartet auf `_writing` und committet es danach mit (N bestätigt, N+1 committet). `StatementRunner.WriteAsync` prüft `IsBusy` nicht. Ebenso „Committen“ im Verlassen-Dialog (ohne `shown`) und der Rollback vor DDL. Vorschlag: erwartete neueste Aktion an `IDataEditor.CommitAsync` geben und innerhalb von `_writing` prüfen; Commit/Rollback verweigern, solange ein Statement des Workspaces läuft. | nachgeprüft |
| A2 | ST-2 | `WorkspaceEditing.cs:551-571` (`CancellationToken.None`), `LeaveDialog.razor:18-21`, `MainWindow.xaml.cs:39-46` | Ein hängender Grid-Write/Commit (stilles VPN) hat weder Abbrechen noch „Session trennen“, `CommandTimeout` ist nirgends gesetzt. Fenster schließen → Verlassen-Dialog mit **allen drei Knöpfen deaktiviert, auch Abbrechen**, ohne Esc → App unbedienbar, bis TCP aufgibt. Vorschlag: `ResetSessionAsync` auch für Grid-Writes (Laufanzeige bzw. „Session trennen und schließen“ im Verlassen-Dialog), „Abbrechen“ nie deaktivieren. | nachgeprüft (Code), Dauer hängt an TCP |

### Mittel

| # | IDs | Ort | Befund | Sicherheit |
|---|---|---|---|---|
| A3 | ST-5 | `Oracle/OracleSession.cs:319`, `SqlView.razor:715,721`, `LinqView.razor`, `TabFooter.razor`, `RowFormPanel.razor`, `DetailViewBase.cs`, `PlanDialog.razor`, `SessionWatch.cs:103` | `OracleCommand.Cancel()` hängt synchron an der Token-Registrierung; `_cts.Cancel()` in Komponenten läuft also auf dem UI-Thread – entgegen der eigenen Regel in `oracle.md`. Auch „Session trennen“ (`DisposeAsync` → `CancelRunningCommand`) und `SessionWatch.Dispose` bei jedem Statement-Ende. Möglicher Zusammenhang mit dem Backlog-Eintrag „Abbrechen über VPN“. Vorschlag: zentral `c => Task.Run(((OracleCommand)c).Cancel)`. | nachgeprüft (Registrierung), Wirkung wahrscheinlich |
| A4 | ST-6 | `Connections/ConnectionKeepAlive.cs:39-46`, `Oracle/SessionGate.cs:56-57` | Der 30-s-Ping-Timeout löst nur `Cancel()` aus; kommt der Break nicht durch, hängt die Runde und **keine** der übrigen Verbindungen wird gepingt. Vorschlag: `.WaitAsync(PingTimeout)`. | wahrscheinlich |
| A5 | ST-3 | `State/FkCounts.cs:208`, `ShellState.Tabs.cs:469-474, 538`, `WorkspaceLifecycle.Schema.cs:83` | Tabs auf Tabellen außerhalb des Schema-Caches (FK-Sprung in ein fremdes Schema ohne Synonym) schließt jedes DDL/„Schema neu laden“ sofort – ohne Blick auf ausstehende Änderungen (still weg, geschriebene ohne Tab in der Transaktion); beim Neustart nicht wiederhergestellt. Ebenso alle Tabellen-Tabs nach Änderung von `EffectiveSchema`. Vorschlag: Tabs mit Änderungen nie automatisch schließen, nicht auflösbare im gespeicherten Stand behalten. | Agent nachgeprüft |
| A6 | ST-4 | `OracleSession.Transactions.cs:103-108`, `WorkspaceEditing.cs:163-168` | Verbindungsabbruch **während** COMMIT (ORA-03113/03135) wird wie ORA-02091 als „nichts geschrieben“ behandelt → Änderungen wieder ausstehend; war der Commit durch, entstehen beim erneuten Schreiben doppelte INSERTs. Vorschlag: „Ausgang unbekannt, bitte prüfen“ getrennt melden. | wahrscheinlich |
| A7 | SE-1 | `Oracle/OracleConnectionStringFactory.cs:51-56`, `Connections/ConnectionProfileValidator.cs:36-58` | Host/Service/SID gehen ungeprüft in Descriptor bzw. EZConnect. Eine untergeschobene Importdatei (DBeaver, SQL Developer, FerretSharp) mit `host = "db01)(PORT=1))(ADDRESS=(PROTOCOL=TCP)(HOST=evil"` erzeugt eine zweite ADDRESS → Failover, Anmeldung mit gespeichertem Passwort an fremden Listener; Liste zeigt „db01…“. Vorschlag: Validator erlaubt nur Hostname-/IP-Zeichen, Service/SID `[A-Za-z0-9._$#-]`, im Alias kein `(`/`)`/`=`. | nachgeprüft (Code), Failover wahrscheinlich |
| A8 | SE-2 | `DialogHost.razor:35`, `ShellState.cs:86,180`, `DbCalls.cs:82` | Der Fehlerdialog maskiert nach der **sichtbaren** Verbindung (`Active.Profile.MasksValues`), nicht nach der fehlgeschlagenen: Prod-Commit scheitert, während Dev gezeigt wird (oder Verlassen-Dialog über mehrere Verbindungen) → DML mit Prod-Bind-Werten unmaskiert, „Kopieren“ legt sie ab. Vorschlag: `ShowError` bekommt das Profil der ausführenden Verbindung. | nachgeprüft |
| A9 | DB-1 | `Schema/OracleTypes.cs:337-344`, `Ddl/ColumnChange.cs:170-176`, `Design/TableDesign.cs:533-559` | `TIMESTAMP(n)` → `DATE` gilt als `Free`: läuft mit Daten durch und verliert Sekundenbruchteile **still**, ohne Warnung in Designer und Vergleich; DDL committet sofort. `FitOf` (WP-51) nennt denselben Übergang `Lossy`. Vorschlag: eigene Stufe „verliert Werte“ mit Warnung (Anzahl betroffener Werte). | nachgeprüft |
| A10 | DB-3 | `Oracle/OracleSchemaReader.Diagnostics.cs:32-40, 125-134` | WP-52 zeigt jedes `BLOCKING_SESSION <> NULL` als „Wartet auf eine Sperre auf {Tabelle}, gehalten von …“ – auch bei `log file sync`, `read by other session`, `buffer busy waits`, `library cache lock`; `BLOCKING_SESSION_STATUS` ungeprüft. Auf belebter Prod irreführend. Vorschlag: nur `enq: TX/TM` bzw. `wait_class = 'Application'` als Sperre, sonst Event-Name neutral. | wahrscheinlich |
| A11 | DB-2 | `OracleSchemaReader.Diagnostics.cs:41-45` | Fortschritt nimmt den jüngsten unfertigen `V$SESSION_LONGOPS`-Eintrag nur nach sid/serial# → nach einem abgebrochenen Full Scan zeigt das nächste Statement „43 % · noch 20 s“ des alten Laufs. Vorschlag: zusätzlich `sql_id`/`sql_exec_id`. | wahrscheinlich |
| A12 | DB-4 | `OracleSession.cs:367`, `OracleDataAccess.cs:77-94` | SQL-Editor blättert durch erneutes Ausführen und Überspringen; mit `InitialLOBFetchSize = -1` überträgt jede übersprungene Zeile ihre ganzen LOBs → Seite N kostet die LOBs aller Seiten davor (quadratisch, VPN), behalten werden 200 Zeichen. Vorschlag: kleine `InitialLOBFetchSize` für `ReadSqlAsync`. | wahrscheinlich (nicht gemessen) |
| A13 | ST-7 | `State/ExportActions.cs:124-139`, `Data/XlsxExport.cs:63ff` | Excel/CSV entstehen synchron im UI-Thread **vor** dem Speichern-Dialog, ohne Abbruch, alles im Speicher; mit WP-54 (alle Zeilen) sicher spürbar. Vorschlag: erst Dialog, dann Bau per `Task.Run` mit Fortschritt/Abbruch. | wahrscheinlich |
| A14 | UI-1, RE-2 | `ConnectionDialog.razor:173, 269-276, 348-353` | `File.Exists` auf C#-Projekt und Audit-Vorlage bei **jedem Render**, also jedem Tastendruck (`oninput`) → beim Tippen von `\\server\…` Namensauflösung/SMB im UI-Thread; umgeht `CachedFileRead`. Vorschlag: erst bei `onchange` prüfen bzw. Ergebnis je Pfadtext merken. | nachgeprüft (Code) |
| A15 | UI-2 | `ModalFrame.razor:5-10` | Dialoge halten den Fokus nicht: Tab wandert hinter den Hintergrund, Enter löst dort z. B. „Commit“ der Statusleiste aus (Kürzel sind gesperrt, Enter/Leertaste nicht). Vorschlag: Fokus-Falle in `ModalFrame` oder App-Inhalt `inert`. | wahrscheinlich |
| A16 | UI-3 | `WorkspaceBar.razor:36-89, 253` | A22 (letztes Review) ist hier nicht umgesetzt: ⋯-Menü des Workspace-Chips handgebaut (läuft am rechten Rand aus dem Fenster), „+“-Popover ohne Fokus/Esc, `DomModule` ungenutzt. Vorschlag: `PopoverMenu`, danach `.menu`/`.overlay` löschen. | nachgeprüft |
| A17 | ST-9, RE-1 | `ShellState.Tabs.cs:103-108, 154-171, 204-224`, `ShellState.cs:253-261` | „Im SQL-Editor öffnen“ (Schema-Vergleich, App-Statements) auf eine getrennte Verbindung wartet ohne die Verwerf-Regeln, die WP-37 für Links eingeführt hat → nach abgebrochener Passwortabfrage öffnet sich der Tab Stunden später beim nächsten Verbinden. Vorschlag: ein `PendingOpen` für Link und SQL (siehe B8). | nachgeprüft |
| A18 | PH-1 | `ClrModel/DotNetCli.cs:21-155`, `ModelHostRunner.cs:59, 206-208` | ModelHost, LINQ-Konsole und `dotnet build` hängen an keinem Job Object: Nach Absturz/Beenden laufen Modell-Export (ohne Zeitgrenze) und Build (bis 10 min) weiter; ein verwaister Host hält DLLs unter `current\modelhost` (Velopack-Update, Verdacht). Vorschlag: Job Object mit `KILL_ON_JOB_CLOSE` hinter einem Interface. | nachgeprüft (fehlt), Update-Folge Verdacht |
| A19 | RE-8 | `SettingsPage.razor:663-681`, `AuditTemplateSource.cs`, `AuditGroupTemplates.cs:46`, `ConnectionForm.cs:172,176` | Fünf Pfadfelder normalisieren dreifach verschieden: Gruppen-/Verbindungs-Vorlage entfernen `"`, globale Vorlage und Anzeigespalten-Datei nicht → „Als Pfad kopieren“ geht für eine Gruppe, global „Datei fehlt“. Vorschlag: `PathInput.Normalize`. | wahrscheinlich |

### Klein

| # | IDs | Befund |
|---|---|---|
| A20 | DB-6 | Sperrhalter-Liste (WP-52) nennt die wartende Workspace-Session selbst („dein Workspace X“), wenn sie die Tabelle schon geschrieben hat – nach `client_info` filtern. |
| A21 | DB-7 | NCLOB mit Enthält/Beginnt/Endet bindet das Muster als VARCHAR2 (A18-Rest; Kommentar „NCLOB never compared with LIKE“ falsch). |
| A22 | DB-10 | `VARCHAR2(n)` → `CHAR(n)` gilt als frei; Werte werden still aufgefüllt – Hinweis im Designer. |
| A23 | DB-5 | `tabelle@dblink` wird gegen das lokale Schema aufgelöst → FK-Sprung (WP-48) zeigt ggf. lokale Zeilen als „zugehörig“; Referenzen mit Link ohne Herkunft. (Verdacht, hängt an ODP.NET) |
| A24 | SE-7 | „Kopieren“ im Fehlerdialog läuft nicht durch `SqlRedaction` → `IDENTIFIED BY geheim` in der Zwischenablage (Windows-Verlauf/Cloud-Sync). |
| A25 | SE-3 | Ein neuer Link ersetzt den offenen Link-Dialog (Klick auf „Öffnen“ trifft den neuen); Link-Flut holt das Fenster immer wieder nach vorn. |
| A26 | SE-9 | Ein unterwegs abgeschnittener Link wird als anderer gültiger Link gelesen (`…Müller` → `…Mül`); Endmarke/Anzahl als letzter Parameter. |
| A27 | SE-10 | Fremd belegte Link-Pipe (Terminalserver): Server scheitert endlos, alle 2 s Warnung mit Stacktrace. Backoff, einmal loggen. (Verdacht) |
| A28 | SE-5, PH-9 | Host-Seite der WebView ohne Content-Security-Policy (Härtung, heute keine XSS gefunden; Update-Notizen laden externe Bilder). |
| A29 | UI-6, UI-7 | `MonacoEditor` weiter kein `FerretComponent` (A48-Rest), Vervollständigungsfehler verschluckt `monaco.js` spurlos; `SqlResultGrid.GetRows` ohne `GuardAsync` → leeres Grid ohne Log. |
| A30 | UI-9 | `SqlCode` kennt INSERT/UPDATE/MERGE/ALTER/ADD/MODIFY/DROP/TRIGGER/BEGIN … nicht → Änderungs-, DDL- und Audit-Anzeige halb eingefärbt. |
| A31 | RE-5 | Filterzeilen-Text dreimal gebaut: Tab-Kopf zeigt Enum-Namen, Vorlagen-Tooltip/Link-Dialog/Prod-Hinweis zeigen Rohwerte (Kommentar behauptet „as in the tab header“). |
| A32 | ST-10, ST-11, UI-13 | `SaveTabs` serialisiert bei jedem `Dirty` alle Tabs aller Verbindungen (Verdacht); `ConnectionSwitcher` setzt Zustand im Hintergrund-Thread; `RunProgress` `_ = TickAsync` statt `Fire`. |
| A33 | PH-2 | `Host.CreateApplicationBuilder(args)` liest `appsettings*.json` aus dem Arbeitsordner (mit Watcher), übernimmt `DOTNET_ENVIRONMENT`, `ConsoleLifetime` – `CreateEmptyApplicationBuilder`. |
| A34 | PH-8, PH-10 | Velopack-Hooks schreiben HKCU ohne try/catch (Install/Uninstall könnte abbrechen, Verdacht); Bilder mitten im Satz in Release-Notizen weder geprüft noch umgeschrieben. |
| A35 | PH-3, PH-11 | Release-Workflow ohne `concurrency` (zwei Tags kurz hintereinander → Delta-Kette); Actions in Release/Pages nur per Major-Tag gepinnt. |
| A36 | PH-4, PH-7 | `InvariantGlobalization` im ModelHost-csproj wirkt im Betrieb nicht (erzeugte runtimeconfig), nur beim Debuggen – irreführend; Kodierung der `dotnet`-Ausgabe bei Umlauten im Pfad (Verdacht). |

## Teil B – Struktur und Wiederverwendung

| # | IDs | Vorschlag | Aufwand |
|---|---|---|---|
| B1 | UI | **`Components/` (107 Dateien) in Unterordner**: Common (13), Shell (18), Connections (9), Schema (14), PlSql (6), Grid (12), Filter (6), Sql (14), Ddl (7), Pages (8); `Shell.razor` bleibt. Folgen: `@using` in `_Imports.razor` (oder je Ordner `@namespace` flach), `.cs`-Dateien behalten Namespace, Logger-Kategorien ändern sich, `paths:` von ui-grid/ui-monaco/ddl/plsql-dictionary/connection-transfer, Doku-Pfade. Ein `git mv`-Commit ohne Inhaltsänderung. Vollständige Zuordnung im UI-Bericht (unten Anhang). | M |
| B2 | ST | **`State/` (70 Dateien) in Unterordner**, Namespace bleibt: Shell, Tabs, Workspaces, Connections, Grid, Links, Schema, Settings, Host (die `I…`-Interfaces → Grenze zu App sichtbar), Model. Hauptnutzen: Regel-Globs – `oracle-transactions.md` (`State/Workspace*`) erfasst heute `StatementRunner`/`SessionWatch` nicht, `ui-grid.md` (`State/Grid*`) verfehlt `FkCounts`, `FormCursor`, `ExportActions`, `SqlResultPresentation`. | S–M |
| B3 | PH-12, PH-13 | Core: `ClrModel/Host/` (Prozess/Datei: DotNetCli, ModelHostRunner, BuildOutput*, ModelCache, LinqConsole*, ModelExport) und `ClrModel/CodeGen/`; `Query/SqlEditor/` (13 Dateien Textanalyse). Namespaces bleiben; Projektschnitte Core/UI/App/ModelHost/AppTrace bleiben. WP-54 keinen vierten Export-Ort aufmachen. | S |
| B4 | UI-4, RE-7, UI-3 | **Menü-Familie:** `MenuButton` (Knopf + `PopoverMenu`, misst Position/Fenster selbst) statt sechsmal `MenuAt`/`viewportSize`/`_disposed`; `MoreMenuItem` um `Strong`/`Danger`/Trenner → `ConnectionActionsMenu` und `LinqMenu` werden `MoreMenu`-Aufrufe; WorkspaceBar (A16) darüber. | M |
| B5 | UI-8, RE-6 | `ExportMenuItems` und Sprunglisten-Helfer für `GridContextMenu`/`ResultMenu` (WP-53 musste Excel zweimal einbauen) – **vor WP-54**. | S |
| B6 | DB-9, RE-4 | `ExportNotation.Text` aus `DelimitedExport.Value`; Excel nimmt sie für alles außer Zahl/Datum/Bool – **vor WP-54**. | S |
| B7 | RE-3 | `SettingsMap` (WP-51) ist eine Zeilenkopie von `ShortcutOverrides` → `ShortcutOverrides` durch `SettingsMap` ersetzen (gleiches JSON). | S |
| B8 | RE-1, ST | `State/PendingOpen` (Verbindung + Aktion, Regeln für Link und SQL, mit Tests) behebt A17, ~90 Zeilen aus `ShellState.Tabs`; `WorkspaceEditing.Undo.cs` als Partial (~200 Zeilen Undo/Redo). | S–M |
| B9 | UI-5, RE-9 | `SettingsPage` (743 Zeilen) in Abschnittskomponenten (Darstellung, Verbindung, Audit, Anzeigespalten, Updates); `AuditSettingsSection` mit einem `TemplateCheck` für alle drei Ebenen. | M |
| B10 | UI-5 | Grid-Editierlogik aus `FerretGrid.razor:638-930` (`Edit`, `OnDeleteKeyCoreAsync`, `ChangeRowsAsync`, `AddRowAsync`) nach `State/GridEditing` mit schmalem Interface und Tests. | M |
| B11 | RE-5, RE-10…16, DB-8 | Kleinkram je S: `FilterText.Row(filter, presentation?)` (A31), `OracleAddress.ToDataSource()`, `SessionActivityText.ProgramOf` (zwei resx-Paare), `TabModeLabels`, gemeinsame Select-Liste + `ReadLockHolder(offset)` in Diagnostics (verstößt heute gegen `oracle.md`), restliche Typvergleiche nach `OracleTypes` (`PropertyCode.IsOneCharacterText` übersieht `NCHAR(1)`?), `ProjectSite` (Owner/Repo viermal), `PathInput.Normalize` (A19), `GridRows.Presented`/`ForeignKeyBadges.Of`, `TableLink.Parse` (137 Zeilen) schneiden. | je S |
| B12 | UI-11, UI-12 | resx: doppelte Texte zusammenlegen (`Settings_AuditTemplateBuiltIn`/`AuditTemplate_BuiltIn`, „Auswählen…“ → `CommonText`, Core-Satz „neuere Version“ → `CoreText`; `Changes_OtherCounts` zählt Aktionen, sagt aber „geschrieben“ wie die Zeilenzahl); `ShellText` (499 Keys) → `SettingsText`, `ConnectionsText` abspalten. | S |
| B13 | UI-10, RE-14, RE-17 | Tot/veraltet: `.menu.linq-menu`, `MonacoEditor.ReadOnlyMessage`, wirkungsloses `@ondblclick:stopPropagation`, doppelter `<summary>` in Shell, „SELECT 1 FROM DUAL“ doppelt in `SettingsCatalog`; gelesen, nie benutzt: `LockHolder.LogonTime`, `LongOperation.Units`, nur in Tests: `FlushResult.InsertedRows`, `PlanStep.ParentId`, `DisplayColumnLoad.IsDefaultPath`, `GridWriteHistory.Knows` public; Kommentare `TimeText`, `PopoverMenu`, `GridContextMenu`; `IsOwnImage` → `OwnImageUri`; `SqlPreview` → `SqlPreviewDialog`; `AuditTableLink` als Fragment in `AuditStatusLine`. | S |
| B14 | PH-5, PH-6 | Build: `CentralPackageTransitivePinningEnabled` statt vier Pkcs-`PackageReference`s; `SatelliteResourceLanguages=en;de` (13 Roslyn-Kulturordner à 0,5–0,7 MB im ModelHost, dazu WPF/WebView). | S |

## Teil C – Tests

| # | IDs | Befund / Vorschlag |
|---|---|---|
| C1 | TE-1 | **M** `FakeDataEditor` nimmt DDL bei offener Transaktion an (echt: `DdlOnlyWithoutTransaction`), `ExecuteAsync`/`ReadSql` ohne `IsWriteStatement`/`IsReadOnlyStatement` → vergessenes `DiscardTransactionAsync` bleibt grün. Fake wie Oracle abweisen lassen. |
| C2 | TE-2 | **M** LINQ-Übersetzung der Filtergruppen nur als String verglichen; ob EF für NICHT dieselben NULL-Zeilen behält, ist ungetestet (Kern von WP-50). Integrationstest mit nullable Spalte, mitgeschnittenes SQL prüfen. |
| C3 | TE-3 | **M** WP-52: Longops-Teil, Weg ohne V$-Rechte (→ null) und `SessionWatch.For` nie gegen echte DB (`NoRightsUser` aus `PlanTests`, `DBMS_APPLICATION_INFO.SET_SESSION_LONGOPS`). |
| C4 | TE-4 | **M** `QueryErrorLog` (einzige Stelle, an der Statement + Binds in Serilog landen) ohne Test: Prod-Bind und `IDENTIFIED BY` dürfen nicht im Log stehen. |
| C5 | TE-5 | `ReadOnlyTests`: QueryBuilder-Durchlauf ohne ODER/NICHT-Gruppen, `BuildSelectByRowId`/`BuildSelectLob` fehlen. |
| C6 | TE-6 | `ReadOnlyTests`: zwei Facts doppelt zur Theory, Namensprüfungen seit R5b durch die Allow-List abgedeckt, Kommentar „until WP-09“. |
| C7 | TE-7 | Bildzeilen-Regel (WP-49) dreimal (Test, App, Workflow), der Test prüft nur seine eigene Kopie. |
| C8 | TE-8 | Fünf „es passiert nichts“-Prüfungen mit echter Wartezeit (falsch grün unter Last). |
| C9 | TE-9 | `ModelHostProcessTests` legen Ordner unter dem echten `WorkRoot` an statt über `TestFolder` (Verstoß gegen `tests.md`). |
| C10 | TE-10 | Struktur: Link-Tests in `Core.Tests/Query/`, `SqlResultPresentationTests` in der Wurzel, `FilterGroupsTests` mit drei Klassen, `EnglishTextTests` unter Oracle, `InMemory*`-Fakes weiter in `Core.Tests/Fakes` (B18 sagte `tests/Shared`). |
| C11 | TE-11 | App-Projekt ohne Tests; `ProtocolRegistration.ProgramOf` (entscheidet, ob Uninstall die Registrierung entfernt) nach UI/Core ziehen und testen. |
| C12 | TE-12 | `SafeDeleteTests`-Regex erkennt `Remove-Item -r`/`-Rec`, `rmdir … -Recurse`, `[IO.Directory]::Delete($p,$true)`, `del /s` nicht; `.py` wird nicht durchsucht (heute kein Treffer). |

## Teil D – Doku und CLAUDE.md

| # | IDs | Befund / Vorschlag |
|---|---|---|
| D1 | DO-1 | **M** CLAUDE.md wieder 15,6 KB (~4,5k Tokens je Session; nach Teil C 13,7 KB, Ziel ~11 KB). Abschnitt 8 (2,1 KB Versionsliste) auf ~300 B; Release-Stand steht dreimal (Kopf, §8, roadmap); Pkcs-Notiz steht schon in `Directory.Packages.props`. |
| D2 | DO-2, DO-13 | **M** README ohne Funktionen seit 3.34 (Excel, Filter-Vorlagen/Links, Gruppen ODER/NICHT, Warten/Fortschritt, FK-Navigation im SQL-/LINQ-Ergebnis, Audit je Gruppe); Screenshots aus 3.26; Label „aus C#-Modell“ statt englisch. |
| D3 | DO-3 | **M** SpreadCheetah (ADR 0028) fehlt in der Stack-Tabelle (CLAUDE.md §3, README „Tech stack“). |
| D4 | DO-4 | **M** Für Links (erster Eingang fremder Eingabe, ADR 0026: nie SQL, immer bestätigen, nie ins Log, Parse wirft nie, Grenzen) weder Invariante noch Regel – neue `links.md` für `Core/Links/**`, `LinkOpenDialog*`, `LinkInbox`, `LinkPipe`, `ProtocolRegistration`. |
| D5 | DO-5, DO-6, DO-10 | **M** `connection-transfer.md`: Audit-Vorlage fehlt bei den lokalen Pfaden; `architecture.md:44` lädt zur falschen Belegung von `CLIENT_INFO` ein (seit WP-52 Session-Kennung); `AuditTemplatePath` fehlt in den Profilfeldern. |
| D6 | DO-7, DO-8, DO-15 | Backlog/Roadmap: LINQ-IntelliSense (seit WP-19) und Enum-Namen im Ergebnis (WP-48) als offen; „national“ für SQL-Variablen teils erledigt; UX-Frage Commit/Rollback-Kürzel seit WP-25 überholt. |
| D7 | DO-9, DO-11, DO-12, DO-14 | Kleinkram: `architecture.md` „3.32.2“ (gibt es nicht → 3.33.0), Kopf „bis 3.31.1“, `Preset` in TabState-Skizze, Ctrl+P jetzt WP-38; CLAUDE.md-Baum ohne `Core/Links/` und `pages/`; Index-Versionstabelle v4 endet bei WP-35. |
| D8 | DO-16, DO-17, B2 | Regel-Globs: `oracle-transactions`/`oracle` laden wegen `Data/**` bei Excel/CSV mit (12,5 KB ohne Bezug); keine Regel für `.github/workflows/**`, `pages/**`, `Directory.Build.props` (Hinweis auf `docs/release.md`, Registry-Falle aus e2e). |
| D9 | DO-18, SE-4 | ADR 0024 ohne Nachtrag zu Bildern aus `raw.githubusercontent.com` (WP-49); ADR 0026: Pages-Seite ist Vertrauensanker für Prod-Filterwerte (Konto nie umbenennen, wie Update-Kanal behandeln) – siehe Frage 3. |
| D10 | SE-6 | Wortlaut der Invariante „Logs und Verlauf enthalten auf Prod nie Werte“ stimmt nicht: Literale im SQL-Text, EF-Konstanten, `RAISE_APPLICATION_ERROR`-Texte stehen drin – siehe Frage 2. |

## Fragen an den Nutzer

1. **SE-8** – `DROP TABLE … PURGE` (am Papierkorb vorbei) läuft als normales DDL, obwohl `PURGE` auf der Nie-Liste steht. Wie TRUNCATE behandeln? Und `DROP USER … CASCADE`?
2. **SE-6 / D10** – Literale im SQL-Text auf Prod: Doku präzisieren („Bind-Werte und Variablen“) oder Literale für Log und Verlauf über den Tokenizer schwärzen?
3. **SE-4 / D9** – Links als https über GitHub Pages: nur im ADR festhalten, oder beim Kopieren von Prod-Werten zusätzlich die `ferretsharp://`-Form anbieten?
4. **ST-8** – `comparisons.json`: Nach gescheitertem Lesen (gesperrt/kaputt) überschreibt das nächste „Speichern“ alle gespeicherten Vergleiche mit nur dem neuen (so dokumentiert in `architecture.md` 1.7). Wie bei den Filter-Vorlagen verweigern, bis die Datei lesbar ist?
5. **B1–B3** – Ordnerstruktur umbauen: `Components/`, `State/`, Core-Unterordner – alle, einzelne oder keine?
6. **Reihenfolge** – Vorschlag wie beim letzten Mal: R6a (Teil A, Patch 3.40.1) → Teil D (Doku) → R6b (Teil B + C, 3.41.0, B5/B6 vor WP-54).

## Entscheidungen des Nutzers (2026-10-17)

Der Nutzer hat die Vorschläge zu allen sechs Fragen übernommen.

1. **SE-8** – `DROP TABLE … PURGE` und `DROP USER … CASCADE` werden wie TRUNCATE behandelt (nie ausgeführt). Ein normales `DROP TABLE` bleibt erlaubt (Papierkorb). Dass ein abgeschalteter Papierkorb (`recyclebin = OFF`) auch ein normales DROP endgültig macht, steht nur in ADR 0021.
2. **SE-6** – Auf Prod schwärzt das Fehler-Log Text-Literale (`'…'` → `'***'`, über den Tokenizer), der SQL-Verlauf behält sie (lokal wie die Workspace-Dateien, A40). Fehlertexte (`RAISE_APPLICATION_ERROR`) bleiben im Log. Die Invariante lautet künftig: Log ohne Bind-Werte und Literale, Verlauf ohne Bind-Werte.
3. **SE-4** – Nur ein Nachtrag in ADR 0026 (Pages-Seite ist so vertrauenswürdig wie der Update-Kanal, Konto nie umbenennen); keine `ferretsharp://`-Form beim Kopieren von Prod-Werten.
4. **ST-8** – `comparisons.json` wie die Filter-Vorlagen: Speichern verweigert, solange die Datei nicht gelesen werden konnte; der nächste Versuch liest erneut.
5. **B1–B3** – Alle drei Umbauten (`State/`, `Components/` mit Unterordner-Namespaces und `@using` in der Wurzel-`_Imports.razor`, Core `Query/SqlEditor`, `ClrModel/Host`, `ClrModel/CodeGen`), je ein reiner `git mv`-Commit am Anfang von R6b, wenn kein anderer Branch offen ist.
6. **Reihenfolge** – R6a (3.40.1) mit A1/A2 zuerst, dann A7/A8/A24, dann der Rest. A12, A32 werden erst gemessen, A23 und A36 erst nachgestellt (Unbestätigtes in den Backlog). A13 (Excel im Hintergrund) geht mit WP-54, B5/B6 bleiben in R6b vor WP-54. Danach Teil D, R6b (3.41.0), WP-54. Gebaut in einer Session ohne weitere Agenten, im eigenen Worktree.

## Anhang: Zuordnung `Components/` (B1)

- **Common:** FerretComponent.cs, Fmt.cs, Icons.cs, ModalFrame, ConfirmDialog, PopoverMenu, MoreMenu, SearchBox, Kbd, EmptyState, FieldError, TitleEditor, SqlCode
- **Shell:** DialogHost, StatusBar, TabBar, TabFrame, WorkspaceBar, WorkspaceBadge, WorkspaceActionsBar, ConnectionLostBanner, ChangesPanel, OpenChangesList, ErrorDialog, LeaveDialog, UnlockDialog, WriteProblemDialog, LockHolderList, UpdateDialog, UpdateIndicator, LinkOpenDialog
- **Connections:** ConnectionScopeView, ConnectionSwitcher, ConnectionsPage, ConnectionDialog, ConnectionActionsMenu, ConnectionImportDialog, ConnectionExportDialog, PasswordDialog, ConnectProgress
- **Schema:** Explorer, LetterIndexBar, ObjectHeader, ColumnsView, ConstraintsView, IndexesView, DependenciesView, DependencyList, DdlView, DetailStatus, DetailViewBase.cs, TableLink, AuditStatusLine, AuditTableLink
- **PlSql:** PlSqlTabView, PlSqlHeader, PlSqlSourceView, PlSqlParametersView, PlSqlErrorsView, PlSqlDependenciesView
- **Grid:** TabView, TabFooter, FerretGrid, GridContextMenu, ColumnMenu, ColumnJump, FkJumpSection, FkJumpItem, RowFormPanel, FormFieldView, RowCompareDialog, LobEditorDialog
- **Filter:** FilterBar, PresetMenu, ValuePicker, ColumnPicker, ColumnList, LinqMenu
- **Sql:** SqlView, LinqView, MonacoEditor, SqlResultGrid, ResultMenu, ResultFooter, RunProgress, SqlVariablesEditor, SqlHistoryPanel, SqlPreview, TabTitle, WriteOutcome, PlanDialog, PlanTable
- **Ddl:** TableDesigner, NewTableView, DdlDraftPanel, DdlConfirmDialog, DdlStatementList, DdlWarnings, PropertySuggestions
- **Pages:** SettingsPage, ShortcutSettings, ModelPage, ModelMismatches, SchemaComparePage, CompareCellView, SchemaDdlDialog, AppStatementsPage

## Anhang: Zuordnung `State/` (B2)

- **Shell:** ShellState(.Tabs), ExitGuard, ShortcutService, DbCalls, QueryErrorLog, RenderFailureNotice, TimeText, InvalidStatusText, MoreMenuItem, DomModule, JsModule
- **Tabs:** WorkspaceTab, TableTab, CloseTabQuestion
- **Workspaces:** WorkspaceLifecycle (4 Teile), WorkspaceEditing, GridWriteHistory, ChangeOverview, StatementRunner, SessionWatch, SessionActivityText
- **Connections:** ConnectionHub, ConnectionForm, ConnectionKindInfo, ConnectionLabel, ConnectionTransfer, ConnectActivityLog
- **Grid:** GridBridge, GridRows, GridDisplayNames, GridContextMenuRequest, ColumnMenuRequest, FormCursor, FkCounts, ExportActions, SqlGridStatus, SqlResultPresentation, PresentationService, PresentationWatch
- **Links:** LinkInbox, LinkCopy, LinkTargetChoice, FilterPresetService, FilterPresetText
- **Schema:** ColumnDesigner, IndexDraftEditor, SuggestedName, PlSqlMarkers, ExplorerList, SchemaCompareService, AuditGroupTemplates, AuditTemplateLabel
- **Settings:** SettingsCatalog, SettingsSaving, SettingsSearch, UpdateState, ReleaseNotes, ReleaseVersion, AppVersion
- **Host:** IAppUpdater, IFileOpenService, IFileSaveService, IThemeService, IProtocolRegistration
- **Model:** ClrActivityLog, AppTraceService
