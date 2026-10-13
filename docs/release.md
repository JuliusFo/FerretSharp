# CI und Release

Aus der `CLAUDE.md` ausgelagert (Gesamt-Review 3.31.0, Teil C). Vor Änderungen an `.github/workflows/`, an der Version oder vor einem Release lesen. Entscheidungen: ADR 0002 (Versionierung), ADR 0024 (Velopack).

## Versionierung

- SemVer, Version in `Directory.Build.props`, `CHANGELOG.md` pflegen (Abschnitt `## [X.Y.Z] - Datum`).
- Git-Tag `vX.Y.Z` je Release. **Der Push des Tags veröffentlicht das GitHub-Release** – erst taggen, wenn der CHANGELOG-Abschnitt steht (er wird zu den Release-Notizen und zum Text im Update-Dialog der App).
- Nicht pushen und nicht taggen ohne Rückfrage beim Nutzer.

## CI (`.github/workflows/ci.yml`)

- **Linux:** Core, UI und AppTrace bauen, Unit- und Integrationstests (Oracle in Docker; .NET-8-Runtime für den ModelHost) mit Coverage (`Microsoft.Testing.Extensions.CodeCoverage`, Einstellungen `tests/coverage.config`, Bericht per ReportGenerator).
- **Coverage-Badge:** auf `main` schreibt der Job `coverage-badge` `coverage.json` in den Branch `badges` für das README-Badge. Lokal: `tools/coverage/Get-Coverage.ps1`.
- **Windows:** ganze Solution mit `-warnaserror`, Unit-Tests. Die Integrationstests laufen unter Windows nicht in der CI (Hänger unter Docker Desktop: Backlog).

## Distribution (Velopack, ADR 0024)

- Bauen: `dotnet publish src/FerretSharp.App -c Release -r win-x64 --self-contained -o <ziel>`, dann `vpk pack` (lokales Tool in `dotnet-tools.json`, gleiche Version wie das Paket `Velopack`).
- Release über `.github/workflows/release.yml`: Tag `vX.Y.Z` → Version gegen `Directory.Build.props` prüfen, Core-Tests, bauen, vorheriges Release für die Delta-Pakete holen, packen → `FerretSharp-win-Setup.exe`, `FerretSharp-win-Portable.zip`, volle und Delta-Pakete, `releases.win.json`. Notizen aus dem CHANGELOG-Abschnitt. Für bestehende Tags per `workflow_dispatch`.
- Das einfache Zip des Ordners gab es bis 3.29.0; seit 3.30.0 nur noch die Velopack-Dateien.
- `Program.Main` ruft zuerst `VelopackApp…Run()`, dann die Instanzsperre (eine Instanz je Datenordner). Installiert prüft die App auf Updates (Einstellungen › Updates); ohne Installation (Debug, `dotnet run`, E2E) passiert nichts.
- Lokale Release-Builds landen unter `artifacts/` (nicht eingecheckt).
- Updates im E2E prüfen: `docs/e2e-testing.md` › Updates (nie `Setup.exe` starten).
