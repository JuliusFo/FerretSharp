# Roadmap – geplante Pakete und offene Fragen

Aus der `CLAUDE.md` ausgelagert (Stand 3.14.0). Beim Start eines Pakets den Abschnitt lesen und den Plan mit dem Nutzer abstimmen. Nicht eingeplante Ideen: `docs/backlog.md`; abgeschlossene Pakete: `docs/work-packages.md`.

## Geplant (v4)

Pakete aus dem Backlog, nach v3 mit dem Nutzer ausgewählt (2026-10-05). Versionen: Minor-Releases 3.x (nichts Inkompatibles).

### WP-30 Änderungsübersicht, Undo bis zu einer Aktion, Redo
Issue [#7](https://github.com/JuliusFo/FerretSharp/issues/7) (2026-10-09), vom Nutzer als eigenes Paket aufgenommen. Problem: Es gibt keinen Ort, der zeigt, was ein Workspace noch nicht committet hat (Statusleiste nur Zähler und Kurztext, „SQL“ nur ausstehende Änderungen, ↶ nur die neueste Aktion, kein Redo). User Stories, Vorschlag und „Fertig, wenn“ stehen im Issue.

Vorschlag zu den offenen Fragen (Kommentar im Issue, **beim Start mit dem Nutzer abstimmen**):
- **Seitenpanel** statt Dialog (Springen zur Zeile/zum SQL-Tab braucht ein offenes Panel, Muster wie die Formularansicht). Die Liste als eigene Komponente mit schreibgeschützter Kompaktform, wiederverwendet im Commit-Dialog auf Prod und im Bestätigungsdialog von WP-22.
- **Nur der aktive Workspace** (Commit/Rollback/„bis hier zurücknehmen“ wirken auf genau eine Transaktion); darunter eine Zeile mit den anderen offenen Transaktionen als Sprungziele (Daten wie im ConnectionSwitcher).
- **Redo erst nur für Grid-Aktionen** (nach dem Undo sind die Änderungen wieder ausstehend, Redo schreibt sie mit der üblichen Konfliktprüfung erneut). Für SQL-/LINQ-Statements stattdessen „Im SQL-Editor öffnen“/„Kopieren“; echtes SQL-Redo später, falls es fehlt. Zu prüfen: Übernahme der Bind-Werte eines LINQ-Statements in den SQL-Editor.
- **Ctrl+Z im Grid und ↶ getrennt**: Ctrl+Z nimmt nur ausstehende Zelländerungen des Tabs zurück, nie geschriebene Aktionen (`ROLLBACK TO SAVEPOINT` wirkt auf den ganzen Workspace, auch auf Statements anderer Tabs).
- **In zwei Stufen, Stufe A vor WP-22:**
  - **A:** Übersicht ausstehend/geschrieben mit vollständigem SQL und Sprung zur Zeile/zum SQL-Tab (Stories 1, 2, 6); `WriteAction` speichert Statement und Bind-Werte (Bind-Werte von Prod nicht loggen, nur in der UI zeigen); „bis hier zurücknehmen“ (Story 3, gezieltes `ROLLBACK TO SAVEPOINT`). Liefert die Grundlage für den Bestätigungsdialog von WP-22.
  - **B:** Redo-Stapel mit Verfallsregeln (neues Schreiben, Commit, Rollback, Sperren, Verbindungsverlust, erneut bearbeitete Zellen) und Zell-Undo/-Redo im `ChangeTracker` (Stories 4, 5; Tasten über `ShortcutMap`, Bereich `Grid`).
- Kein neuer Schreibweg: Redo nutzt den bestehenden Flush bzw. `ExecuteNonQueryAsync` (ADR 0006 unverändert).

### WP-22 DDL im SQL-Editor
Wunsch des Nutzers (2026-10-06): Tabellen anlegen und ändern, passend zum DB-first-Ablauf („erst DB ändern, dann Entity“). Erste Stufe: DDL im SQL-Editor (und in Skripten) zulassen. Entscheidungen des Nutzers:
- **Wo:** auf allen schreibbaren Workspaces – Profile ohne „Schreibgeschützt“ sowie **auch Prod nach dem Freischalten** (WP-10). Gesperrte Workspaces nie (READ-ONLY-Transaktion schützt nicht vor DDL, deshalb weiter Abweisung im Editor).
- **Ausführen:** FerretSharp führt das DDL aus (nicht nur erzeugen/kopieren).
- **Offene Transaktion:** Hat der Workspace eine Transaktion, wird eine **Meldung** ausgegeben und die **Transaktion abgebrochen** (Rollback der nicht committeten Änderungen), bevor das DDL läuft – sonst würde DDL sie still mitcommitten. Entschieden (2026-10-06): **Rollback nur nach Bestätigung** – der Dialog zeigt, was verworfen wird (ausstehende/geschriebene Grid-Änderungen, per SQL geänderte Zeilen); „Abbrechen“ führt das DDL nicht aus.
- Vorschlag zur Umsetzung (beim Start mit dem Nutzer abstimmen):
  - Eigener enger Weg an `OracleSession` (z. B. `ExecuteDdlAsync`, `internal`): nur ein einzelnes DDL-Statement (`SqlStatementKind.Ddl`), nur ohne offene Transaktion, nie in einer gesperrten Session; `IsWriteStatement` und die Leseschranke bleiben unverändert. `ReadOnlyTests` erweitern. ADR.
  - Weiterhin abgewiesen: PL/SQL-Blöcke, `ALTER SESSION/SYSTEM`, `COMMIT`/`ROLLBACK` und **`TRUNCATE`** (Entscheidung des Nutzers: löscht alle Zeilen ohne Rollback – bleibt abgewiesen).
  - Bestätigung vor jedem DDL (zeigt das Statement; auf Prod immer, mit Verbindungsname); in Skripten eine Bestätigung für alle DDL-Statements. DDL ist **nicht** rückgängig zu machen – im Dialog so sagen.
  - Danach Schema-Cache neu laden (Explorer, Spalten-Ansichten, Autovervollständigung) und das C#-Modell neu abgleichen; Hinweis auf neue „Spalten ohne Property“.
  - Verlauf: DDL-Statements wie DML aufnehmen.

### WP-23 Tabellen-Designer + Entity aus Tabelle (nach WP-22)
- Spalten-Ansicht eines Tabs bearbeitbar: Spalte hinzufügen, Typ/Länge/Precision, NULL, Default, Kommentar ändern, umbenennen, löschen; PK, Unique, FK, Indizes; neue Tabelle anlegen. FerretSharp erzeugt das DDL und zeigt es **vor** dem Ausführen (ausführen über den Weg aus WP-22 oder nur kopieren, z. B. als Skript fürs Repo).
- Indizes setzen (Wunsch der Kollegen des Nutzers, 2026-10-07; bleibt in WP-23, Entscheidung des Nutzers): „Index anlegen“ in der Indizes-Ansicht (Spalten, optional UNIQUE), beim Hinweis „Fremdschlüssel ohne Index“ ein Klick „Index dafür anlegen“.
- Oracle-Fallen im Designer abfangen: NOT NULL auf Spalte mit NULL-Werten (vorher zählen), Typänderung gefüllter Spalten (oft nur über neue Spalte + Umkopieren), VARCHAR2 BYTE/CHAR-Semantik, Index für neue FKs vorschlagen (`IndexAdvice`), Identity/Default ON NULL.
- Verzahnung mit dem C#-Modell (Backlog-Idee „Entity aus Tabelle erzeugen“): nach der Änderung Property-Zeile bzw. Entity + `IEntityTypeConfiguration` im Stil des Projekts (Namenskonvention, J/N-Converter) zum Kopieren.

### WP-26 Audit-/Historientabellen aus einer Vorlage (nach WP-22/WP-23)
Wunsch der Kollegen des Nutzers (2026-10-07): beim Anlegen einer Tabelle die Historientabelle und den Trigger gleich mit erzeugen, für bestehende Tabellen nachziehen. Entscheidung des Nutzers: das Schema kommt aus einer **Vorlagendatei**, nicht fest aus dem Code – damit FerretSharp auch außerhalb seiner Firma passt.
- Eingebaute, dokumentierte Standardvorlage; in den Einstellungen ein Pfad zu einer eigenen Vorlage (z. B. im Repo der Firma); später evtl. je Verbindung überschreibbar.
- Vorlage = SQL mit Platzhaltern (Tabelle, Historientabelle, Spalten mit Typen, Listen für `:OLD.`/`:NEW.`, Wiederholung über die Spalten); eigene minimale Syntax statt einer Template-Bibliothek (neue Abhängigkeit bräuchte ein ADR). Die Firmenvorlage des Nutzers (Tabelle + Trigger) dient als Testfall.
- Nachziehen: Spalte in `KUNDEN` neu → fehlt in `KUNDEN_HIST`/Trigger veraltet → `ALTER` + `CREATE OR REPLACE TRIGGER` vorschlagen (Vergleichslogik aus WP-20 wiederverwenden). Passt zum Backlog-Eintrag „Audit-/Historientabellen: Unterschiede hervorheben“ (Muster 1).
- Trigger enthalten PL/SQL – WP-22 weist PL/SQL-Blöcke ab; für `CREATE [OR REPLACE] TRIGGER` aus der Vorlage braucht es eine bewusste, enge Ausnahme (beim Start von WP-22/26 mit dem Nutzer entscheiden, ADR).

## Offene UX-Fragen
- Shortcut-Belegung für Commit/Rollback – vorläufig, Nutzerfeedback einholen.
- Workspaces: Standardname ist „Workspace N“ mit der kleinsten freien Nummer (nach Umbenennen von „Workspace 1“ heißt der nächste wieder „Workspace 1“). Shortcuts zum Wechseln (z. B. Ctrl+1…9) und eine Oberfläche für die Notizen fehlen noch.
