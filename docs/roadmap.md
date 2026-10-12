# Roadmap – geplante Pakete und offene Fragen

Seit 3.14.0 aus der `CLAUDE.md` ausgelagert; Stand nach WP-23. Beim Start eines Pakets den Abschnitt lesen und den Plan mit dem Nutzer abstimmen. Nicht eingeplante Ideen: `docs/backlog.md`; abgeschlossene Pakete: `docs/work-packages.md`.

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

WP-22 (DDL im SQL-Editor) ist umgesetzt: Protokoll in `docs/work-packages.md`, ADR 0019. WP-23 (Tabellen-Designer + Entity aus Tabelle) ist in 3.22.0–3.24.0 umgesetzt: Protokoll in `docs/work-packages.md`, ADR 0020. R4a (Fehler aus dem Review 3.24.0) ist in 3.24.1 umgesetzt, R4b (Struktur) in 3.25.0 (Protokolle in `docs/work-packages.md`); als Nächstes WP-26.

### WP-26 Audit-/Historientabellen aus einer Vorlage (nach WP-23)
Wunsch der Kollegen des Nutzers (2026-10-07): beim Anlegen einer Tabelle die Historientabelle und den Trigger gleich mit erzeugen, für bestehende Tabellen nachziehen. Entscheidung des Nutzers: das Schema kommt aus einer **Vorlagendatei**, nicht fest aus dem Code – damit FerretSharp auch außerhalb seiner Firma passt.
- Eingebaute, dokumentierte Standardvorlage; in den Einstellungen ein Pfad zu einer eigenen Vorlage (z. B. im Repo der Firma); später evtl. je Verbindung überschreibbar.
- Vorlage = SQL mit Platzhaltern (Tabelle, Historientabelle, Spalten mit Typen, Listen für `:OLD.`/`:NEW.`, Wiederholung über die Spalten); eigene minimale Syntax statt einer Template-Bibliothek (neue Abhängigkeit bräuchte ein ADR). Die Firmenvorlage des Nutzers (Tabelle + Trigger) dient als Testfall.
- Nachziehen: Spalte in `KUNDEN` neu → fehlt in `KUNDEN_HIST`/Trigger veraltet → `ALTER` + `CREATE OR REPLACE TRIGGER` vorschlagen (Vergleichslogik aus WP-20 wiederverwenden). Passt zum Backlog-Eintrag „Audit-/Historientabellen: Unterschiede hervorheben“ (Muster 1).
- Vorbereitet in R4b: DDL-Schritte mit Art und Risiko (`Core/Ddl`, `PlSqlObject` endet im Skript mit `/`), `ColumnChange.Steps` mit `ChangeIntent.Known` für „Spalte in `KUNDEN` neu → in `KUNDEN_HIST` ergänzen“, `SqlSplitter` als eigener Teil (dort kommt der Blockmodus hin), ein `StatementRunner` für alle Statements.
- Trigger enthalten PL/SQL – WP-22 weist PL/SQL-Blöcke und PL/SQL-Objekte (`CREATE [OR REPLACE] PROCEDURE/FUNCTION/PACKAGE/TRIGGER/TYPE`) ab (Entscheidung des Nutzers 2026-10-10: eigene Stufe). Für `CREATE [OR REPLACE] TRIGGER` aus der Vorlage braucht es eine bewusste, enge Ausnahme (beim Start von WP-26 mit dem Nutzer entscheiden, ADR): Splitter für Blöcke bis zur `/`-Zeile, Kompilierfehler (ORA-24344) aus `ALL_ERRORS` anzeigen.

Entscheidungen des Nutzers beim Start (2026-10-12):
- **Trigger allgemein** über den Schemaweg, auch von Hand im SQL-Editor; Procedures/Packages/Types bleiben abgewiesen (ADR 0021). System-Trigger (`ON DATABASE`/`ON SCHEMA`) ebenfalls abgewiesen.
- **Erst DISABLE, dann ENABLE:** Trigger ausgeschaltet anlegen, Kompilierfehler aus `ALL_ERRORS` prüfen, nur gültige einschalten.
- **Die Audit-Tabelle behält alles:** Gelöschte Basisspalten bleiben (werden nullable, der Trigger füllt sie nicht mehr), Typen werden nur vergrößert, Umbenennungen aus dem Designer werden übernommen. Neue Spalten in einer gefüllten Audit-Tabelle sind nullable (ORA-01758).
- **Spaltenkommentare** der Basistabelle werden übernommen, auch beim Nachziehen; die der Audit-Spalten und der Tabelle kommen aus der Vorlage.
- **Einstiege:** Häkchen beim Anlegen einer neuen Tabelle im Designer; „Audit anlegen/abgleichen“ für bestehende Tabellen; im Designer kommen die Audit-Schritte automatisch in den Plan einer Tabelle mit Audit-Tabelle (abwählbar).
- **Firmenmuster als Testfall** (im Repo mit neutralen Namen): Audit-Tabelle `AUDIT_<TABELLE>` mit `AUDIT_ID` (gemeinsame Sequence `AUDIT_ID_SEQ`), `AUDIT_USER` (`NVL(SYS_CONTEXT('USERENV','CLIENT_IDENTIFIER'), USER)`), `AUDIT_TRANS` (I/U/D), `AUDIT_TIME` (`SYSDATE`), `AUDIT_SID` (`USERENV('sessionid')`), danach alle Basisspalten; Trigger `<TABELLE>_AUDIT`, `AFTER INSERT OR UPDATE OR DELETE … FOR EACH ROW`: D schreibt `:old`, I/U schreiben `:new`, ein UPDATE ohne geänderten Wert (NULL-sicherer Vergleich je Spalte) schreibt nichts.
- Veraltete Trigger werden über `ALL_TRIGGER_COLS` erkannt (fehlt eine Basisspalte?), nicht über einen Textvergleich – vorhandene Trigger sind anders formatiert als die Vorlage.
- Je Verbindung überschreibbare Vorlagen: später (Backlog).

## Offene UX-Fragen
- Shortcut-Belegung für Commit/Rollback – vorläufig, Nutzerfeedback einholen.
- Workspaces: Standardname ist „Workspace N“ mit der kleinsten freien Nummer (nach Umbenennen von „Workspace 1“ heißt der nächste wieder „Workspace 1“). Shortcuts zum Wechseln (z. B. Ctrl+1…9) und eine Oberfläche für die Notizen fehlen noch.
