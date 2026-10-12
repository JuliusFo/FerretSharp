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

WP-22 (DDL im SQL-Editor) ist umgesetzt: Protokoll in `docs/work-packages.md`, ADR 0019. WP-23 (Tabellen-Designer + Entity aus Tabelle) ist in 3.22.0–3.24.0 umgesetzt: Protokoll in `docs/work-packages.md`, ADR 0020. R4a (Fehler aus dem Review 3.24.0) ist in 3.24.1 umgesetzt, R4b (Struktur) in 3.25.0, WP-26 (Audit-Tabellen aus einer Vorlage) in 3.26.0 (Protokolle mit den Entscheidungen des Nutzers in `docs/work-packages.md`, ADR 0021). WP-31 (Anzeigespalten, Issue #11) ist in 3.27.0 umgesetzt (Protokoll in `docs/work-packages.md`, ADR 0022). Weitere Pakete sind noch nicht eingeplant – beim nächsten Mal mit dem Nutzer aus `docs/backlog.md` auswählen.

## Offene UX-Fragen
- Anzeigespalten (WP-31), beim Start selbst entschieden, Bestätigung des Nutzers offen: Views als Ziel bekommen keine Namen (kein erzwungener eindeutiger Schlüssel); Cache der Namen je Tab statt je Workspace (geleert bei F5, Filter, Schreiben, Commit, Rollback).
- Shortcut-Belegung für Commit/Rollback – vorläufig, Nutzerfeedback einholen.
- Workspaces: Standardname ist „Workspace N“ mit der kleinsten freien Nummer (nach Umbenennen von „Workspace 1“ heißt der nächste wieder „Workspace 1“). Shortcuts zum Wechseln (z. B. Ctrl+1…9) und eine Oberfläche für die Notizen fehlen noch.
