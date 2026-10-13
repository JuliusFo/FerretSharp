# Roadmap – geplante Pakete und offene Fragen

Seit 3.14.0 aus der `CLAUDE.md` ausgelagert; Stand nach WP-26. Beim Start eines Pakets den Abschnitt lesen und den Plan mit dem Nutzer abstimmen. Nicht eingeplante Ideen: `docs/backlog.md`; abgeschlossene Pakete: `docs/work-packages/`.

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

WP-22 (DDL im SQL-Editor) ist umgesetzt: Protokoll in `docs/work-packages/`, ADR 0019. WP-23 (Tabellen-Designer + Entity aus Tabelle) ist in 3.22.0–3.24.0 umgesetzt: Protokoll in `docs/work-packages/`, ADR 0020. R4a (Fehler aus dem Review 3.24.0) ist in 3.24.1 umgesetzt, R4b (Struktur) in 3.25.0, WP-26 (Audit-Tabellen aus einer Vorlage) in 3.26.0 (Protokolle mit den Entscheidungen des Nutzers in `docs/work-packages/`, ADR 0021). WP-31 (Anzeigespalten, Issue #11) ist in 3.27.0 umgesetzt (Protokoll in `docs/work-packages/`, ADR 0022). WP-32 (Installer und Update-Hinweis) ist in 3.29.0 umgesetzt (Protokoll und Planung in `docs/work-packages/`, ADR 0024). WP-33 (Statements der laufenden App, Issue #12) ist in 3.28.0 umgesetzt (Protokoll mit den Entscheidungen des Nutzers in `docs/work-packages/`, ADR 0023). WP-34 (Tabs anheften, Schnell-Sprung in den Einstellungen) ist in 3.30.0 umgesetzt (Protokoll in `docs/work-packages/`). R5a (Fehler aus dem Gesamt-Review 3.31.0, `docs/review-3.31.0.md`) ist in 3.31.1 umgesetzt; als Nächstes die Doku (Teil C), dann R5b. WP-35 (Verbindungen importieren und exportieren) ist in 3.31.0 umgesetzt (Protokoll und Planung in `docs/work-packages/`, ADR 0025).

## Offene UX-Fragen
- Shortcut-Belegung für Commit/Rollback – vorläufig, Nutzerfeedback einholen.
- Workspaces: Standardname ist „Workspace N“ mit der kleinsten freien Nummer (nach Umbenennen von „Workspace 1“ heißt der nächste wieder „Workspace 1“). Shortcuts zum Wechseln (z. B. Ctrl+1…9) und eine Oberfläche für die Notizen fehlen noch.
