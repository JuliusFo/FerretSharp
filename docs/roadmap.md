# Roadmap – geplante Pakete und offene Fragen

Aus der `CLAUDE.md` ausgelagert (Stand 3.14.0). Beim Start eines Pakets den Abschnitt lesen und den Plan mit dem Nutzer abstimmen. Nicht eingeplante Ideen: `docs/backlog.md`; abgeschlossene Pakete: `docs/work-packages.md`.

## Geplant (v4)

Pakete aus dem Backlog, nach v3 mit dem Nutzer ausgewählt (2026-10-05). Versionen: Minor-Releases 3.x (nichts Inkompatibles).

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

### WP-28 Prozeduren, Funktionen und Packages ansehen
Wunsch des Nutzers (2026-10-08). Unabhängig von den anderen Paketen. Entscheidung des Nutzers: **nur ansehen** (Stufe 1) – Ausführen und Bearbeiten sind nicht Teil des Pakets (siehe unten).
- Explorer: Prozeduren, Funktionen, Packages (Spec und Body) des Schemas, unterscheidbar von Tabellen/Views; ungültige Objekte markiert (`ALL_OBJECTS.STATUS`).
- Ansicht je Objekt im Tab: Quelltext (`ALL_SOURCE`, nach `LINE`), Parameter (`ALL_ARGUMENTS`: Name, Richtung IN/OUT/IN OUT, Typ, Default; bei Packages je Unterprogramm, Überladungen über `OVERLOAD`), Kompilierfehler (`ALL_ERRORS` mit Zeile/Spalte, Sprung in den Quelltext), Abhängigkeiten (vorhandene Ansicht wiederverwenden). Aus der Abhängigkeiten-Ansicht einer Tabelle per Klick zur Prozedur.
- Rein lesend über die bestehende Lesesperre (`ISchemaReader`-Methoden nach dem Muster der Objekt-Details, Explorer-Session, Integrationstests mit einem kleinen Package im Test-Container). Schema-Liste wie Tabellen nach `OWNER` filtern; Objekte aus anderen Schemas über Synonyme wie bei Tabellen (beim Start klären).
- Beim Start mit dem Nutzer klären: Platz im Explorer (eigene Gruppe oder gemischt mit Filter), Hervorhebung (PL/SQL-Schlüsselwörter in `SqlCode` oder Monaco nur lesend), Volltextsuche im Quelltext (`ALL_SOURCE`, kann auf großen Schemas langsam sein), ob Trigger und Typen gleich mitkommen (Trigger stehen im Backlog unter „Weitere Objekt-Details“).

**Später, nicht in WP-28** (Stufen 2 und 3, Gespräch vom 2026-10-08):
- *Ausführen* (Parameter-Dialog, OUT-Werte, `SYS_REFCURSOR` als Grid) braucht einen eigenen Schreibweg mit ADR – der SQL-Editor weist PL/SQL-Blöcke und `CALL`/`EXEC` heute ab, weil eine Prozedur selbst committen kann. Ideen dazu: (a) eigene Session nur für PL/SQL – ein Commit dort trifft nicht die Workspace-Transaktion, aber die Prozedur sieht deren nicht committete Änderungen nicht, wartet auf deren Zeilensperren und hinterlässt eine zweite offene Transaktion; (b) in der Workspace-Session mit `ALTER SESSION DISABLE COMMIT IN PROCEDURE` (COMMIT/ROLLBACK in Prozeduren → ORA-00034) plus Savepoint, als Aktion im Undo-Stapel – Lücken: autonome Transaktionen, DDL per `EXECUTE IMMEDIATE` (Verhalten per Integrationstest prüfen).
- *Bearbeiten/Kompilieren* (`CREATE OR REPLACE PROCEDURE/PACKAGE`) gehört zu WP-22 und hängt an derselben PL/SQL-Entscheidung wie der Trigger in WP-26.

## Offene UX-Fragen
- Shortcut-Belegung für Commit/Rollback – vorläufig, Nutzerfeedback einholen.
- Workspaces: Standardname ist „Workspace N“ mit der kleinsten freien Nummer (nach Umbenennen von „Workspace 1“ heißt der nächste wieder „Workspace 1“). Shortcuts zum Wechseln (z. B. Ctrl+1…9) und eine Oberfläche für die Notizen fehlen noch.
