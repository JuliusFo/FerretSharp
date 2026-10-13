# Roadmap – geplante Pakete und offene Fragen

Was als Nächstes kommt, mit den Entscheidungen des Nutzers. Beim Start eines Pakets den Abschnitt lesen und den Plan mit dem Nutzer abstimmen. Ist das Paket released, wandert sein Abschnitt als „Planung“ in die Paketdatei unter `docs/work-packages/` (dort auch alle abgeschlossenen Pakete). Nicht eingeplante Ideen: `docs/backlog.md`.

## GitHub-Issues und Labels

Entscheidung des Nutzers (2026-10-09). Issues bekommen eine Art und einen Status.
- **Art:** `enhancement`, `bug`, `documentation`, `question`. Optional der Bereich: `area: grid`, `area: sql-editor`, `area: linq` oder `area: schema`.
- **Status**, je Issue genau einer:
  - `needs decision`: Das Issue hat offene Fragen. Die Antworten des Nutzers kommen beim Start des Pakets hierher in den Abschnitt des Pakets, danach wird das Label entfernt.
  - `planned`: Das Issue ist als Paket eingeplant. In der Überschrift des Pakets hier steht der Link zum Issue.
  - `done`: Das Issue ist umgesetzt und released. `planned` wird entfernt, dazu kommt ein Abschlusskommentar mit Version, Entscheidungen und dem, was offen bleibt (Backlog). Dann wird das Issue geschlossen.
- Labels setzen dürfen nur Leute mit Triage-Rechten. Wer ein Issue anlegt, setzt in der Regel keine Labels; das geschieht beim Einplanen.

## Geplant

Versionen: Minor-Releases 3.x (nichts Inkompatibles). Zuletzt released: 3.31.1 (R5a). R5b (Struktur nach dem Gesamt-Review, 3.32.0) ist umgesetzt (`docs/work-packages/r5b-structure.md`), das Release wartet auf das OK des Nutzers.

### WP-36 Abhängigkeiten aktualisieren (AG Grid 36, Pakete, CI) → 3.33.0 ([#13](https://github.com/JuliusFo/FerretSharp/issues/13))
Nach R5b. Entscheidung des Nutzers (2026-10-14): AG Grid und die übrigen Updates zusammen als eigenes Paket, Branch `wp/36-dependency-update`. Bewertung vom 2026-10-14; beim Start die Versionen neu prüfen (`dotnet list package --outdated`, npm, Actions-Releases).
- **AG Grid 34.3.1 → 36.x** (Stand: 36.2.0), direkt ohne Zwischenschritt über 35 (keine entfernten APIs in 35/36). Neue Funktionen sind fast alle Enterprise; der Gewinn ist Wartung (34.x ohne Patches seit 2025-10), Fehlerbehebungen und ein Scroll-Container statt neun (36.0). Zu erledigen und zu prüfen:
  - 35.0: Das Infinite Row Model zeigt eigene Overlays „No Rows“/„No Matching Rows“ (englisch, wir setzen kein `localeText`) → `suppressOverlays: ['noRows', 'noMatchingRows']`; Ladezustände mit Spinner ansehen.
  - 36.0: neue DOM-Struktur (`ag-body-viewport`, `ag-floating-top` … umbenannt) – unsere Selektoren (`.ag-cell`, `.ag-row`, `.ag-header-cell`, `.ag-cell-inline-editing`, `.ag-tooltip`) und `row-index="t-0"` für angeheftete Zeilen prüfen; `fontWeight` jetzt 400 statt `inherit`; Theme-Klassen an Elternelementen; `ValidationModule` nicht mehr in `AllCommunityModule` (Debug: `enableDevValidations()`); `content-visibility: auto` nicht mehr Standard.
  - UMD-Bundle `dist/ag-grid-community.min.js` gibt es weiter (36.2.0: 2,06 MB); prüfen, dass es ohne `ag-stack` auskommt. README und Hash unter `wwwroot/lib/ag-grid/`, Version in `CLAUDE.md`.
  - Risiko ist das Timing beim Editieren (`readOnlyEdit`, `stopEditingWhenCellsLoseFocus`, Popup-Editoren, `setTimeout`-Stellen in `grid.js`) → E2E-Lauf mit echter Windows-Eingabe: Editieren, Fokus, Tastatur, angeheftete Zeilen, Tooltips, Theme-Wechsel, Scrollen großer Tabellen.
- **NuGet-Patches:** ASP.NET Core Components.Web und Extensions.Hosting 10.0.13, CredentialManager 3.0.5, xunit.v3 4.0.2, Testcontainers.Oracle 4.16.0, EF Core Proxies/Sqlite (Tests) 8.0.32 – EF in den Tests bleibt auf 8.x.
- **CI:** `actions/checkout` v5 → v7, `actions/setup-dotnet` v5 → v6; ein CI-Lauf auf dem Branch vor dem Release.
- **Bleibt bewusst:** Roslyn-Scripting 4.11 und EF Core Relational 8.0.0 im ModelHost (ADR 0009, 0011). Monaco 0.57.0, Velopack/vpk, Oracle.ManagedDataAccess.Core und die übrigen Pakete waren aktuell.
- Kontext: .NET 8 und EF Core 8 laufen am 2026-11-10 aus dem Support. Wechselt das Projekt des Nutzers auf .NET 10, fällt die Roslyn-Grenze aus ADR 0011 vermutlich weg – dann neu bewerten (nicht Teil dieses Pakets).

Danach: weitere Pakete mit dem Nutzer aus dem Backlog auswählen (Workspace-Pins stehen dort mit offenen Varianten).

## Offene UX-Fragen
- Shortcut-Belegung für Commit/Rollback – vorläufig, Nutzerfeedback einholen.
- Workspaces: Standardname ist „Workspace N“ mit der kleinsten freien Nummer (nach Umbenennen von „Workspace 1“ heißt der nächste wieder „Workspace 1“) – so lassen oder fortlaufend zählen?
