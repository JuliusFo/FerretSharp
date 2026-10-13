# Roadmap – geplante Pakete und offene Fragen

Seit 3.14.0 aus der `CLAUDE.md` ausgelagert; Stand nach WP-26. Beim Start eines Pakets den Abschnitt lesen und den Plan mit dem Nutzer abstimmen. Nicht eingeplante Ideen: `docs/backlog.md`; abgeschlossene Pakete: `docs/work-packages.md`.

## GitHub-Issues und Labels

Entscheidung des Nutzers (2026-10-09). Issues bekommen eine Art und einen Status.
- **Art:** `enhancement`, `bug`, `documentation`, `question`. Optional der Bereich: `area: grid`, `area: sql-editor`, `area: linq` oder `area: schema`.
- **Status**, je Issue genau einer:
  - `needs decision`: Das Issue hat offene Fragen. Die Antworten des Nutzers kommen beim Start des Pakets hierher in den Abschnitt des Pakets, danach wird das Label entfernt.
  - `planned`: Das Issue ist als Paket eingeplant. In der Überschrift des Pakets hier steht der Link zum Issue.
  - `done`: Das Issue ist umgesetzt und released. `planned` wird entfernt, dazu kommt ein Abschlusskommentar mit Version, Entscheidungen und dem, was offen bleibt (Backlog). Dann wird das Issue geschlossen.
- Labels setzen dürfen nur Leute mit Triage-Rechten. Wer ein Issue anlegt, setzt in der Regel keine Labels; das geschieht beim Einplanen.

## Geplant (v4)

Pakete aus dem Backlog, nach v3 mit dem Nutzer ausgewählt (2026-10-05). Versionen: Minor-Releases 3.x (nichts Inkompatibles).

WP-22 (DDL im SQL-Editor) ist umgesetzt: Protokoll in `docs/work-packages.md`, ADR 0019. WP-23 (Tabellen-Designer + Entity aus Tabelle) ist in 3.22.0–3.24.0 umgesetzt: Protokoll in `docs/work-packages.md`, ADR 0020. R4a (Fehler aus dem Review 3.24.0) ist in 3.24.1 umgesetzt, R4b (Struktur) in 3.25.0, WP-26 (Audit-Tabellen aus einer Vorlage) in 3.26.0 (Protokolle mit den Entscheidungen des Nutzers in `docs/work-packages.md`, ADR 0021). WP-31 (Anzeigespalten, Issue #11) ist in 3.27.0 umgesetzt (Protokoll in `docs/work-packages.md`, ADR 0022). WP-32 (Installer und Update-Hinweis) ist geplant, der Zeitpunkt ist noch offen (unten). WP-33 (Statements der laufenden App) ist geplant, ebenfalls ohne Zeitpunkt (unten).

### WP-32 Installer und Update-Hinweis (Velopack)
Aus dem Backlog („Installer/Auto-Update“), geplant mit dem Nutzer (2026-10-12), noch nicht begonnen. Ziel: FerretSharp einmal per `Setup.exe` installieren (pro Benutzer, keine Admin-Rechte); danach meldet die App neue Versionen und aktualisiert sich auf Klick aus den GitHub-Releases – statt bei jedem Release ein Zip zu entpacken (eigene 12+ Verbindungen, Kollegen).
- Entscheidungen des Nutzers (2026-10-12, bei der Planung):
  - Downloads je Release: Velopack-`Setup.exe` und Velopack-Portable-Zip (aktualisiert sich ebenfalls). Das bisherige einfache Zip fällt weg; Vorschlag: noch ein Release lang parallel, damit der Umstieg im README beschrieben bei den Nutzern ankommt.
  - Nur melden: beim Start prüfen (in den Einstellungen abschaltbar) und „Jetzt prüfen“; Hinweis „3.x.y verfügbar“ mit den Release-Notes. Herunterladen und Installieren erst nach Klick – kein Download im Hintergrund, kein Anwenden beim Beenden ohne Klick.
  - Quelle: GitHub-Releases des öffentlichen Repos direkt (`GithubSource`, ohne Token); die Rechner des Nutzers und der Kollegen erreichen github.com ohne Proxy.
  - Unsigniert: Die `Setup.exe` zeigt beim ersten Start die SmartScreen-Warnung; Updates aus der App heraus nicht (kein Mark of the Web). Signieren bleibt im Backlog (Kosten und SignPath dort).
- Was Velopack dafür bringt: `vpk pack` macht aus dem `dotnet publish`-Ordner `Setup.exe`, volles und Delta-Paket, Portable-Zip und `releases.win.json`. Installiert wird nach `%LocalAppData%\FerretSharp\current`; die Daten bleiben in `%APPDATA%\FerretSharp` (`AppPaths`), eine Deinstallation lässt Verbindungen und Workspaces also stehen. Ohne Installation (Debug, `dotnet run`, E2E mit `--data-dir` aus dem Build-Ordner) ist `IsInstalled` false und es passiert nichts.
- Plan:
  - App: eigenes `Program.Main` mit `VelopackApp.Build().Run()` als Allererstes (vor Serilog und Einstellungen); heute erzeugt WPF `Main` aus `App.xaml`.
  - UI: Schnittstelle `IAppUpdater` (prüfen, herunterladen mit Fortschritt, anwenden und neu starten, installierte Version), Implementierung mit Velopack in `FerretSharp.App` (Muster `ISecretStore`). `UpdateState` in `UI/State` hält Prüfung, Ergebnis, Fortschritt und Fehler – testbar in den UI.Tests mit Mock. Einstellungen › Updates (Version, „Beim Start prüfen“, „Jetzt prüfen“), Hinweis in der Statusleiste mit Release-Notes und „Aktualisieren“; alle Texte Englisch und Deutsch. Prüfung fehlgeschlagen (offline, VPN, Rate-Limit) ist ein harmloser Hinweis, kein Fehlerdialog.
  - Neustart über den `ExitGuard`: offene Transaktionen und ausstehende Änderungen erst über den bekannten Dialog (Commit/Rollback), dann Workspaces speichern, Sessions schließen, ModelHost-Prozesse beenden, erst dann `ApplyUpdatesAndRestart`.
  - Release-Workflow: `vpk` als lokales dotnet-Tool (Version im Tool-Manifest fest), `vpk download github` für die Delta-Pakete, `vpk pack` (Pack-Id `FerretSharp`, Icon, Release-Notes aus dem CHANGELOG-Abschnitt), Assets ans Release. Die Prüfung Tag ↔ `Directory.Build.props` bleibt.
  - Doku: ADR 0023 (Velopack, Quelle, nur melden, Neustart-Ablauf), README (Installation, Umstieg vom Zip: einmal `Setup.exe`, alten Ordner von Hand löschen), Protokoll, CHANGELOG.
- Beim Start prüfen:
  - Aktuelle Velopack-Version festlegen und die API gegen den Plan prüfen.
  - Mehrere Instanzen: FerretSharp hat keine Einzelinstanz-Sperre. Läuft beim Anwenden eine zweite Instanz, sind Dateien in `current` gesperrt – wie Velopack damit umgeht (warten, abbrechen, beenden); notfalls vor dem Anwenden darauf hinweisen.
  - ModelHost läuft aus `current\modelhost`: alle Hilfsprozesse müssen vor dem Anwenden beendet sein (die Shadow-Kopien unter `%TEMP%`, ADR 0016, betrifft das nicht).
  - E2E ohne GitHub und ohne die echte Installation des Nutzers: zwei Testversionen lokal packen, die ältere installieren, Update aus einem lokalen Ordner als Quelle (Startargument nur für Tests).

### WP-33 Statements der laufenden App
Aus dem Backlog („Live-SQL der eigenen Anwendung“), geplant mit dem Nutzer (2026-10-13), noch nicht begonnen. Ziel: FerretSharp hängt sich an eine lokal laufende .NET-App mit EF Core (Debug-Session aus Visual Studio, `dotnet run`, Rider) und zeigt live, welche Statements sie schickt – nach Herkunft (LINQ, Lazy Loading, Raw SQL, `SaveChanges`, `ExecuteUpdate`/`ExecuteDelete`) mit Anzahl, Zeitanteil und Zeilen, dazu die häufigsten und teuersten Statements und N+1-Hinweise; ein Statement lässt sich mit `V$SQL` und dem tatsächlichen Plan der passenden Verbindung anreichern. Ohne Code-Änderung in der App und ohne VS-Extension.
- Entscheidungen des Nutzers (2026-10-13, bei der Planung):
  - Eigene Seite wie Einstellungen und Schema-Vergleich (`ShellPage`, Knopf oben rechts, „Zurück“ dorthin, woher man kam). Der Zustand (angehängter Prozess, Mitschnitt) gehört keiner Verbindung und läuft weiter, wenn man die Seite verlässt.
  - Prozesse nur als Liste, die sich auf Klick aktualisiert; kein Beobachten im Hintergrund.
  - Umgebung aus der Kommandozeile: fest der Schlüssel `environment` in den üblichen Schreibweisen (`-environment=x`, `--environment=x`, `--environment x`, `/environment=x`, Groß-/Kleinschreibung egal) – Vorschlag, noch zu bestätigen. Ein einstellbarer Schlüssel erst, wenn ihn jemand braucht. Umgebungsvariablen (`DOTNET_ENVIRONMENT`, `ASPNETCORE_ENVIRONMENT`) sind von außen nicht lesbar.
  - N+1-Hinweis ab 10 gleichen Statements in 2 s; gilt für Lazy Loading und für dieselbe Abfrage in einer Schleife.
- Spike (2026-10-12, EF Core 8 mit Lazy-Loading-Proxies, SQLite und Oracle 23 im Container):
  - Weg: `Microsoft.Diagnostics.NETCore.Client` hängt sich per PID an (nur Prozesse des eigenen Benutzers), EventPipe-Session mit dem Provider `Microsoft-Diagnostics-DiagnosticSource` (Keywords `0x2 | 0x800`) und `FilterAndPayloadSpecs` auf `Microsoft.EntityFrameworkCore`; lesen mit `TraceEvent` (`EventPipeEventSource`). Verschachtelte Pfade gehen (`Command.CommandText`, `Command.Connection.DataSource`, `Context.ContextId`), Enums und `TimeSpan` kommen als Text.
  - `CommandExecuted.CommandSource` (ab EF Core 7) liefert die Herkunft: `LinqQuery`, `FromSqlQuery` (nur unkomponiert, `FromSqlRaw(…).Count()` ist `LinqQuery`), `ExecuteSqlRaw`, `SaveChanges` (bei Oracle ein PL/SQL-Block), `BulkUpdate` (`ExecuteUpdate`/`ExecuteDelete`). Lazy Loading: `NavigationLazyLoading` kommt direkt vor dem Command im selben Context (15 von 15 erkannt).
  - Zeiten: `CommandExecuted.Duration` endet, wenn der Reader zurückkommt; `DataReaderDisposing` (über `CommandId`) liefert die Lesezeit allein und `ReadCount` (zählt das letzte `Read()` mit, also Zeilen + 1). Gesamtzeit = Ausführung + Lesen.
  - Oracle: Die mit `ExecutionPlan.SqlId` aus dem EF-Text berechnete `SQL_ID` traf für alle 7 Statements genau einen Cursor in `V$SQL` (Ausführungen stimmen). `DataSource` ist der EZConnect-String bzw. TNS-Alias.
  - Last: 3 × 3000 Abfragen, alle 9000 Events angekommen, nach dem Aufwärmen kein messbarer Unterschied je Abfrage. Pause: Prozess 15 s eingefroren (`NtSuspendProcess`, strenger als ein Breakpoint) – die Session überlebt, danach fehlt nichts.
  - `DiagnosticsClient.GetProcessInfo()` (liefert die Kommandozeile) ist nicht öffentlich. Endet die App, endet der Stream; `Stop()` danach wirft `ServerNotAvailableException`.
  - Bind-Werte kommen auf diesem Weg nicht an (`Command.Parameters*Enumerate` bleibt leer).
- Plan:
  - Core zuerst mit Tests (neuer Ordner, Name offen, z. B. `Core/AppTrace/`): rohe Events → Commands (Herkunft, Lazy Loading je Context, Fehler, Lesezeit über `CommandId`), Statistik je Herkunft und je SQL-Text (Anzahl, Summe, Mittel, Max, Zeilen; Zurücksetzen; Obergrenze für gemerkte Commands), N+1-Erkennung, Zuordnung zur Verbindung. Schnittstelle für die Quelle (Prozesse auflisten, anhängen, rohe Events als `IAsyncEnumerable`, trennen); Unit-Tests mit Event-Folgen wie im Spike.
  - Quelle: EventPipe in `FerretSharp.App` (TraceEvent bringt native ETW-Teile mit; Core und UI sollen weiter unter Linux bauen), Kommandozeile über WMI bzw. `NtQueryInformationProcess` (Muster `ISecretStore`).
  - Verbindung zuordnen: neues optionales Feld der Verbindung „Umgebung in der App“; erst über den Umgebungsnamen, sonst über `DataSource` (Host/Port/Service bzw. TNS-Alias) – nur wenn eindeutig, sonst wählt der Nutzer, gemerkt je Umgebung.
  - Anreichern (nur mit zugeordneter, verbundener Verbindung): `V$SQL` per `SQL_ID` über die Explorer-Session (reines SELECT, passiert die Lesesperre) und der tatsächliche Plan über den Weg aus WP-14. Ohne `SELECT_CATALOG_ROLE` ein Hinweis wie beim Plan; Hinweis, dass `V$SQL` instanzweit zählt.
  - UI: State-Klasse für Prozessliste, Mitschnitt, Auswahl und Fehler; Rendern gedrosselt (höchstens ein paar Mal pro Sekunde, nicht je Event). Seite mit Prozessliste, Übersicht nach Herkunft, Statement-Tabelle (sortierbar), Detail mit SQL, `SQL_ID`, „Im SQL-Editor öffnen“ und „Plan“. Alle Texte Englisch und Deutsch.
  - Tests: Unit (Core, UI) ohne Prozess und DB; Integration mit einer kleinen EF-Test-App als Prozess (wie `FakeModelHost`; SQLite ohne Docker, mit Testcontainer zusätzlich `V$SQL`); E2E mit `--data-dir` und eigenem Container.
  - Doku: ADR (Anhängen an fremde Prozesse, feste Feldliste, was nie gelesen wird), README-Abschnitt, Protokoll, CHANGELOG.
- Sicherheit: feste Feldliste im Code – nie der Connection-String (ODP.NET entfernt das Passwort nach dem Öffnen nur ohne `Persist Security Info=true`), keine Parameter. SQL-Texte können Literale enthalten: nur im Speicher, nie ins Log. FerretSharp schreibt nichts in die App und nichts in die DB.
- Nicht im Paket (Backlog): VS-Extension; Prozesse auf anderen Rechnern oder in Containern (`dotnet-monitor`); Bind-Werte; Mitschnitt speichern oder exportieren; EF Core vor 7 (ohne `CommandSource` – Herkunft „unbekannt“, der Rest geht); .NET Framework.
- Beim Start prüfen:
  - Aktuelle Versionen und Lizenzen von `Microsoft.Diagnostics.NETCore.Client` und `TraceEvent` (beide MIT), Größe von TraceEvent im Publish-Ordner.
  - Mit echtem VS-Debugger am Breakpoint (im Spike nur eingefroren).
  - Zwei Sessions auf denselben Prozess (zweite FerretSharp-Instanz, `dotnet-trace` parallel).
  - Lange Mitschnitte (Speicher), EF Core 6/7 als Ziel.

## Offene UX-Fragen
- Shortcut-Belegung für Commit/Rollback – vorläufig, Nutzerfeedback einholen.
- Workspaces: Standardname ist „Workspace N“ mit der kleinsten freien Nummer (nach Umbenennen von „Workspace 1“ heißt der nächste wieder „Workspace 1“). Shortcuts zum Wechseln (z. B. Ctrl+1…9) und eine Oberfläche für die Notizen fehlen noch.
