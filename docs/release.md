# CI und Release

Aus der `CLAUDE.md` ausgelagert (Gesamt-Review 3.31.0, Teil C). Vor Änderungen an `.github/workflows/`, an der Version oder vor einem Release lesen. Entscheidungen: ADR 0002 (Versionierung), ADR 0024 (Velopack).

## Versionierung

- SemVer, Version in `Directory.Build.props`, `CHANGELOG.md` pflegen (Abschnitt `## [X.Y.Z] - Datum`).
- Git-Tag `vX.Y.Z` je Release. **Der Push des Tags veröffentlicht das GitHub-Release** – erst taggen, wenn der CHANGELOG-Abschnitt steht (er wird zu den Release-Notizen und zum Text im Update-Dialog der App).
- Nicht pushen und nicht taggen ohne Rückfrage beim Nutzer.

## CI (`.github/workflows/ci.yml`)

- **Linux:** Core, UI und AppTrace bauen, Unit- und Integrationstests (Oracle in Docker; .NET-8-Runtime für den ModelHost) mit Coverage (`Microsoft.Testing.Extensions.CodeCoverage`, Einstellungen `tests/coverage.config`, Bericht per ReportGenerator).
- **Coverage-Badge:** auf `main` schreibt der Job `coverage-badge` `coverage.json` in den Branch `badges` für das README-Badge. Lokal: `tools/coverage/Get-Coverage.ps1` (gleiche Messung; unter Linux liegt AppTrace ein paar Zeilen niedriger). Kindprozesse mit eigener Kopie der gemessenen DLLs gehören aus der Messung (`.claude/rules/tests.md`).
- **Windows:** ganze Solution mit `-warnaserror`, Unit-Tests. Die Integrationstests laufen unter Windows nicht in der CI (Hänger unter Docker Desktop: Backlog).

## Distribution (Velopack, ADR 0024)

- Bauen: `dotnet publish src/FerretSharp.App -c Release -r win-x64 --self-contained -o <ziel>`, dann `vpk pack` (lokales Tool in `dotnet-tools.json`, gleiche Version wie das Paket `Velopack`).
- Release über `.github/workflows/release.yml`: Tag `vX.Y.Z` → Version gegen `Directory.Build.props` prüfen, Core-Tests, bauen, vorheriges Release für die Delta-Pakete holen, packen → `FerretSharp-win-Setup.exe`, `FerretSharp-win-Portable.zip`, volle und Delta-Pakete, `releases.win.json`. Notizen aus dem CHANGELOG-Abschnitt. Für bestehende Tags per `workflow_dispatch`.
- Das einfache Zip des Ordners gab es bis 3.29.0; seit 3.30.0 nur noch die Velopack-Dateien.
- `Program.Main` ruft zuerst `VelopackApp…Run()`, dann die Instanzsperre (eine Instanz je Datenordner). Installiert prüft die App auf Updates (Einstellungen › Updates); ohne Installation (Debug, `dotnet run`, E2E) passiert nichts.
- Lokale Release-Builds landen unter `artifacts/` (nicht eingecheckt).
- **Weiterleitungsseite für Links** (ADR 0026, Nachtrag): `.github/workflows/pages.yml` veröffentlicht `pages/` auf GitHub Pages, sobald sich der Ordner auf `main` ändert (oder per `workflow_dispatch`); unabhängig von Releases. Pages muss in den Repository-Einstellungen einmal auf „GitHub Actions“ stehen.
- Updates im E2E prüfen: `docs/e2e-testing.md` › Updates (nie `Setup.exe` starten).

## Bilder im Changelog (WP-49)

Der CHANGELOG-Abschnitt erscheint an drei Stellen: im Repo, als Text des GitHub-Releases und im Update-Dialog der App. Bilder gehen überall, wenn sie so eingebunden sind:
- **Ablage:** `docs/images/changelog/<version>-<name>.png` (oder `.webp`), Dateiname nur aus Buchstaben, Ziffern, `.`, `-`, `_`.
- **Einbinden:** relativ und als eigene Zeile, `![Alt-Text](docs/images/changelog/3.34.0-dialog.png)` – direkt unter dem Punkt, zu dem es gehört (eingerückt oder nicht; der Dialog zeigt es als eigenen Block nach dem Punkt). Mitten in einem Satz zeigt der Dialog nur „!Alt-Text“.
- **Release-Workflow:** schreibt die Pfade beim Ausschneiden auf `https://raw.githubusercontent.com/JuliusFo/FerretSharp/vX.Y.Z/<pfad>` um (für `changes.md` und `notes.md`) und bricht ab, wenn ein Bild fehlt oder nicht direkt unter `docs/images/changelog/` liegt. Vorher schon `ChangelogTests` (Core, laufen auch im Release): eigene Zeile, Alt-Text, Ordner, Dateiname, vorhanden, höchstens 400 KB.
- **Dialog:** zeigt nur Bilder von `raw.githubusercontent.com/JuliusFo/FerretSharp/…` (`ReleaseNotes.IsOwnImage`), sonst den Alt-Text; lädt das Bild nicht (offline, VPN), steht der Alt-Text als Hinweis da. Klick öffnet das Bild im Browser. Erst Installationen ab 3.34.0 zeigen Bilder, ältere zeigen „!Alt-Text“.
- **Aufnahme:** zugeschnittene Ausschnitte statt ganzer Fenster, Ziel 100–200 KB (die Bilder liegen für immer in der Git-Historie; das Velopack-Paket wird nicht größer). **Dunkles Theme** (Entscheidung des Nutzers, 2026-10-14: eine Variante, dunkel), englische Oberfläche. Aufnahme wie die README-Screenshots (`docs/e2e-testing.md` › README-Screenshots: eigene Instanz mit `--data-dir`, Beispiel-DB, `--theme=dark --lang=en`, CDP `Page.captureScreenshot` mit `clip`), nie echte Verbindungen, Hosts oder Daten.
- **Alt-Text:** ein lesbarer Satz, was das Bild zeigt – er steht offline, in älteren Versionen und für Screenreader an seiner Stelle.
