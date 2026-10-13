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

WP-22 (DDL im SQL-Editor) ist umgesetzt: Protokoll in `docs/work-packages.md`, ADR 0019. WP-23 (Tabellen-Designer + Entity aus Tabelle) ist in 3.22.0–3.24.0 umgesetzt: Protokoll in `docs/work-packages.md`, ADR 0020. R4a (Fehler aus dem Review 3.24.0) ist in 3.24.1 umgesetzt, R4b (Struktur) in 3.25.0, WP-26 (Audit-Tabellen aus einer Vorlage) in 3.26.0 (Protokolle mit den Entscheidungen des Nutzers in `docs/work-packages.md`, ADR 0021). WP-31 (Anzeigespalten, Issue #11) ist in 3.27.0 umgesetzt (Protokoll in `docs/work-packages.md`, ADR 0022). WP-32 (Installer und Update-Hinweis) ist in 3.29.0 umgesetzt (unten, Protokoll in `docs/work-packages.md`, ADR 0024). WP-33 (Statements der laufenden App, Issue #12) ist in 3.28.0 umgesetzt (Protokoll mit den Entscheidungen des Nutzers in `docs/work-packages.md`, ADR 0023). WP-34 (Tabs anheften, Schnell-Sprung in den Einstellungen) ist umgesetzt, Release 3.30.0 steht aus (Protokoll in `docs/work-packages.md`).

### WP-32 Installer und Update-Hinweis (Velopack)
Aus dem Backlog („Installer/Auto-Update“), geplant mit dem Nutzer (2026-10-12), umgesetzt in 3.29.0 (Protokoll in `docs/work-packages.md`, ADR 0024). Ziel: FerretSharp einmal per `Setup.exe` installieren (pro Benutzer, keine Admin-Rechte); danach meldet die App neue Versionen und aktualisiert sich auf Klick aus den GitHub-Releases – statt bei jedem Release ein Zip zu entpacken (eigene 12+ Verbindungen, Kollegen).
- Entscheidungen des Nutzers (2026-10-12, bei der Planung):
  - Downloads je Release: Velopack-`Setup.exe` und Velopack-Portable-Zip (aktualisiert sich ebenfalls). Das bisherige einfache Zip fällt weg; Vorschlag: noch ein Release lang parallel, damit der Umstieg im README beschrieben bei den Nutzern ankommt.
  - Nur melden: beim Start prüfen (in den Einstellungen abschaltbar) und „Jetzt prüfen“; Hinweis „3.x.y verfügbar“ mit den Release-Notes. Herunterladen und Installieren erst nach Klick – kein Download im Hintergrund, kein Anwenden beim Beenden ohne Klick.
  - Quelle: GitHub-Releases des öffentlichen Repos direkt (`GithubSource`, ohne Token); die Rechner des Nutzers und der Kollegen erreichen github.com ohne Proxy.
  - Unsigniert: Die `Setup.exe` zeigt beim ersten Start die SmartScreen-Warnung; Updates aus der App heraus nicht (kein Mark of the Web). Signieren bleibt im Backlog (Kosten und SignPath dort).
  - Einzelinstanz (2026-10-13, beim Start des Pakets): eine Instanz **je Datenordner**. Ein zweiter Start mit demselben Datenordner holt das Fenster der laufenden Instanz nach vorn und beendet sich; Instanzen mit eigenem `--data-dir` (E2E) laufen weiter parallel. Grund: Zwei Instanzen auf denselben Dateien überschreiben sich Workspaces und Einstellungen (zuletzt Speichernder gewinnt), und beim Anwenden eines Updates sind keine Dateien in `current` mehr durch eine zweite Instanz gesperrt. Zwei Fenster nebeneinander mit denselben Daten braucht der Nutzer nicht (seit WP-24 mehrere Verbindungen in einer Instanz).
- Was Velopack dafür bringt: `vpk pack` macht aus dem `dotnet publish`-Ordner `Setup.exe`, volles und Delta-Paket, Portable-Zip und `releases.win.json`. Installiert wird nach `%LocalAppData%\FerretSharp\current`; die Daten bleiben in `%APPDATA%\FerretSharp` (`AppPaths`), eine Deinstallation lässt Verbindungen und Workspaces also stehen. Ohne Installation (Debug, `dotnet run`, E2E mit `--data-dir` aus dem Build-Ordner) ist `IsInstalled` false und es passiert nichts.
- Plan:
  - App: eigenes `Program.Main` mit `VelopackApp.Build().Run()` als Allererstes (vor Serilog und Einstellungen); heute erzeugt WPF `Main` aus `App.xaml`.
  - UI: Schnittstelle `IAppUpdater` (prüfen, herunterladen mit Fortschritt, anwenden und neu starten, installierte Version), Implementierung mit Velopack in `FerretSharp.App` (Muster `ISecretStore`). `UpdateState` in `UI/State` hält Prüfung, Ergebnis, Fortschritt und Fehler – testbar in den UI.Tests mit Mock. Einstellungen › Updates (Version, „Beim Start prüfen“, „Jetzt prüfen“), Hinweis in der Statusleiste mit Release-Notes und „Aktualisieren“; alle Texte Englisch und Deutsch. Prüfung fehlgeschlagen (offline, VPN, Rate-Limit) ist ein harmloser Hinweis, kein Fehlerdialog.
  - Neustart über den `ExitGuard`: offene Transaktionen und ausstehende Änderungen erst über den bekannten Dialog (Commit/Rollback), dann Workspaces speichern, Sessions schließen, ModelHost-Prozesse beenden, erst dann `ApplyUpdatesAndRestart`.
  - Release-Workflow: `vpk` als lokales dotnet-Tool (Version im Tool-Manifest fest), `vpk download github` für die Delta-Pakete, `vpk pack` (Pack-Id `FerretSharp`, Icon, Release-Notes aus dem CHANGELOG-Abschnitt), Assets ans Release. Die Prüfung Tag ↔ `Directory.Build.props` bleibt.
  - Doku: ADR 0024 (Velopack, Quelle, nur melden, Neustart-Ablauf; 0023 ging an WP-33), README (Installation, Umstieg vom Zip: einmal `Setup.exe`, alten Ordner von Hand löschen), Protokoll, CHANGELOG.
- Beim Start prüfen:
  - Aktuelle Velopack-Version festlegen und die API gegen den Plan prüfen.
  - Mehrere Instanzen: FerretSharp hat keine Einzelinstanz-Sperre. Läuft beim Anwenden eine zweite Instanz, sind Dateien in `current` gesperrt – wie Velopack damit umgeht (warten, abbrechen, beenden); notfalls vor dem Anwenden darauf hinweisen.
  - ModelHost läuft aus `current\modelhost`: alle Hilfsprozesse müssen vor dem Anwenden beendet sein (die Shadow-Kopien unter `%TEMP%`, ADR 0016, betrifft das nicht).
  - E2E ohne GitHub und ohne die echte Installation des Nutzers: zwei Testversionen lokal packen, die ältere installieren, Update aus einem lokalen Ordner als Quelle (Startargument nur für Tests).

## Offene UX-Fragen
- Angeheftete Tabs (WP-34) behalten ihren vollen Titel; schmal (nur Symbol und Kurzname) wie im Browser, falls die Leiste zu voll wird – Nutzerfeedback einholen.
- Shortcut-Belegung für Commit/Rollback – vorläufig, Nutzerfeedback einholen.
- Workspaces: Standardname ist „Workspace N“ mit der kleinsten freien Nummer (nach Umbenennen von „Workspace 1“ heißt der nächste wieder „Workspace 1“). Shortcuts zum Wechseln (z. B. Ctrl+1…9) und eine Oberfläche für die Notizen fehlen noch.
