# Roadmap – geplante Pakete und offene Fragen

Seit 3.14.0 aus der `CLAUDE.md` ausgelagert; Stand nach WP-22. Beim Start eines Pakets den Abschnitt lesen und den Plan mit dem Nutzer abstimmen. Nicht eingeplante Ideen: `docs/backlog.md`; abgeschlossene Pakete: `docs/work-packages.md`.

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

WP-22 (DDL im SQL-Editor) ist umgesetzt: Protokoll in `docs/work-packages.md`, ADR 0019.

### WP-23 Tabellen-Designer + Entity aus Tabelle
- Spalten-Ansicht eines Tabs bearbeitbar: Spalte hinzufügen, Typ/Länge/Precision, NULL, Default, Kommentar ändern, umbenennen, löschen; PK, Unique, FK, Indizes; neue Tabelle anlegen. FerretSharp erzeugt das DDL und zeigt es **vor** dem Ausführen (ausführen über den Schemaweg aus WP-22 – `WorkspaceManager.ExecuteDdlAsync`, Bestätigung und Rollback-Dialog wie im SQL-Editor – oder nur kopieren, z. B. als Skript fürs Repo). Hinweis aus WP-22: Der Schemaweg sperrt die Tabelle vor `ALTER TABLE` (`LOCK TABLE … NOWAIT`) und startet nicht, wenn eine andere Session sie hält (`TableBusyException`) – der Designer zeigt dann wie der SQL-Editor, wer sperrt (`WorkspaceLifecycle.LockOfAsync`, `LockHolderList`), und warnt vorher (`OtherWriters`).
- Indizes setzen (Wunsch der Kollegen des Nutzers, 2026-10-07; bleibt in WP-23, Entscheidung des Nutzers): „Index anlegen“ in der Indizes-Ansicht (Spalten, optional UNIQUE), beim Hinweis „Fremdschlüssel ohne Index“ ein Klick „Index dafür anlegen“.
- Oracle-Fallen im Designer abfangen: NOT NULL auf Spalte mit NULL-Werten (vorher zählen), Typänderung gefüllter Spalten (oft nur über neue Spalte + Umkopieren), VARCHAR2 BYTE/CHAR-Semantik, Index für neue FKs vorschlagen (`IndexAdvice`), Identity/Default ON NULL.
- Verzahnung mit dem C#-Modell (Backlog-Idee „Entity aus Tabelle erzeugen“): nach der Änderung Property-Zeile bzw. Entity + `IEntityTypeConfiguration` im Stil des Projekts (Namenskonvention, J/N-Converter) zum Kopieren.

### WP-26 Audit-/Historientabellen aus einer Vorlage (nach WP-23)
Wunsch der Kollegen des Nutzers (2026-10-07): beim Anlegen einer Tabelle die Historientabelle und den Trigger gleich mit erzeugen, für bestehende Tabellen nachziehen. Entscheidung des Nutzers: das Schema kommt aus einer **Vorlagendatei**, nicht fest aus dem Code – damit FerretSharp auch außerhalb seiner Firma passt.
- Eingebaute, dokumentierte Standardvorlage; in den Einstellungen ein Pfad zu einer eigenen Vorlage (z. B. im Repo der Firma); später evtl. je Verbindung überschreibbar.
- Vorlage = SQL mit Platzhaltern (Tabelle, Historientabelle, Spalten mit Typen, Listen für `:OLD.`/`:NEW.`, Wiederholung über die Spalten); eigene minimale Syntax statt einer Template-Bibliothek (neue Abhängigkeit bräuchte ein ADR). Die Firmenvorlage des Nutzers (Tabelle + Trigger) dient als Testfall.
- Nachziehen: Spalte in `KUNDEN` neu → fehlt in `KUNDEN_HIST`/Trigger veraltet → `ALTER` + `CREATE OR REPLACE TRIGGER` vorschlagen (Vergleichslogik aus WP-20 wiederverwenden). Passt zum Backlog-Eintrag „Audit-/Historientabellen: Unterschiede hervorheben“ (Muster 1).
- Trigger enthalten PL/SQL – WP-22 weist PL/SQL-Blöcke und PL/SQL-Objekte (`CREATE [OR REPLACE] PROCEDURE/FUNCTION/PACKAGE/TRIGGER/TYPE`) ab (Entscheidung des Nutzers 2026-10-10: eigene Stufe). Für `CREATE [OR REPLACE] TRIGGER` aus der Vorlage braucht es eine bewusste, enge Ausnahme (beim Start von WP-26 mit dem Nutzer entscheiden, ADR): Splitter für Blöcke bis zur `/`-Zeile, Kompilierfehler (ORA-24344) aus `ALL_ERRORS` anzeigen.

## Offene UX-Fragen
- Shortcut-Belegung für Commit/Rollback – vorläufig, Nutzerfeedback einholen.
- Workspaces: Standardname ist „Workspace N“ mit der kleinsten freien Nummer (nach Umbenennen von „Workspace 1“ heißt der nächste wieder „Workspace 1“). Shortcuts zum Wechseln (z. B. Ctrl+1…9) und eine Oberfläche für die Notizen fehlen noch.
