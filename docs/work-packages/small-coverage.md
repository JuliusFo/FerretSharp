# Klein: Coverage-Messung und Tests für UI.State → kein Release (nur Tests und CI)
Auftrag (2026-10-14, Beobachtung des Nutzers): Das Coverage-Badge sank auf 73 %. Erst bewerten, dann mit dem Nutzer: Messung reparieren (Branch `fix/coverage-fake-host`), danach gezielt Tests für die größten Lücken in UI.State (Branch `test/ui-state-coverage`).

## Bewertung
- Die Abdeckung sank nicht, die Messung der CI war falsch. Lokal (`tools/coverage/Get-Coverage.ps1`) 88 %, die CI 73,8 % bei gleich vielen messbaren Zeilen. Der Sprung des Badges von 84 auf 75 % kam mit R4b, ohne Änderung an `tests/coverage.config`.
- Ursache: `ModelHostProcessTests` (seit R4b) starten den `FakeModelHost` als Kindprozess mit eigener Kopie von `FerretSharp.Core.dll`. Unter Linux wurde diese Kopie mitgemessen und überschrieb die Treffer, die der Testprozess bis dahin gesammelt hatte – je Lauf ein anderer Teil der Core-Abdeckung (z. B. `SchemaDiff` 3/270, `RowForm` 0/54, beim nächsten Lauf andere Klassen). Unter Windows nicht.
- Nachgestellt im Linux-Container (`dotnet/sdk:10.0`, Kopie des Repos), nur Core-Tests: wie bisher 9536 von 14807 Zeilen; ohne die ModelHost-Prozesstests 11736; mit dem Ausschluss unten in zwei Läufen je 12051 (Windows 12052). `CollectFromChildProcesses` in `coverage.config` lehnt der Sammler ab (kein Test läuft).

## Gebaut
- `fix/coverage-fake-host`: `ModulePaths`/`Exclude` für `FerretSharp.FakeModelHost` in `tests/coverage.config`; Falle in `.claude/rules/tests.md`, Hinweis in `docs/release.md`. CI auf dem Branch: 88 % (Core 92,7 %).
- `test/ui-state-coverage`, ausgewählt nach Nutzen, nicht nach Prozent:
  - `SchemaCompareServiceTests` (bisher ohne Tests): Matrix nur über geladene Seiten, Passwort nur für die Seite, die es braucht (getippt, nicht gespeichert), Optionen rechnen neu, neue Seiten fangen neu an, Abbrechen, Seiten ohne Verbindung, gespeicherte Vergleiche (sortiert, Löschen des offenen), Datei einer neueren Version wird gemeldet und nicht überschrieben, Beschriftung.
  - `WorkspaceLifecycleTests`: Commit auf Prod erst nach Bestätigung (Abbrechen schreibt nichts), anderswo sofort; Rollback nach Frage; Verlassen mit Commit oder Verwerfen und Weitermachen, Abbruch ohne Weitermachen, wenn das Verwerfen scheitert; erneutes Sperren fragt erst; letzter Workspace bleibt offen; Session aufgeben; ausstehende Änderungen als SQL; Antworten ohne Frage ändern nichts.
  - `ActivityLogTests`: Verbinden loggt jeden Schritt einmal und am Ende die Dauern; nach `Dispose` nichts mehr; das C#-Modell loggt jeden neuen Zustand einmal.
- Ergebnis lokal: UI 73,6 % → 79,6 %, gesamt 88,0 % → 89,5 %.

## Offen (Backlog)
- Bewusst nicht: `OracleSession` (ungetestet sind nur Abbruch- und Schließpfade einer still gewordenen Verbindung, nur mit echter hängender Session nachstellbar; die Sperren decken `ReadOnlyTests`), `Icons`/`FerretComponent` (Razor-Basis, E2E), `DotNetCli` (Prozessaufrufe).
- Der Konsolen-Teil von `ClrActivityLog` (braucht den Modellaufbau aus `ClrModelLoadingTests`).
- Weitere Lücken in UI.State: `ShellState` (rund 130 Zeilen), `ExportActions`, `PresentationService`, `TableTab`.
