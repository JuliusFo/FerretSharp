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

WP-22 (DDL im SQL-Editor) ist umgesetzt: Protokoll in `docs/work-packages.md`, ADR 0019. WP-23 (Tabellen-Designer + Entity aus Tabelle) ist in 3.22.0–3.24.0 umgesetzt: Protokoll in `docs/work-packages.md`, ADR 0020. R4a (Fehler aus dem Review 3.24.0) ist in 3.24.1 umgesetzt; als Nächstes R4b, dann WP-26.

### R4b Struktur-Refactoring nach dem Review 3.24.0 (vor WP-26)
Vereinbart mit dem Nutzer (2026-10-11), nach den Fehlern in R4a (Protokoll in `docs/work-packages.md`). Ohne neue Features, Core zuerst mit Tests; Minor-Release.
- **Vorbereitung WP-26:**
  - `Core/Ddl`: DDL-Text an einer Stelle (`OracleDdl`, übernimmt `DdlText` aus `Compare`). `DdlStep` mit Art (CreateTable, AddColumn, ModifyType, …, PlSqlObject, Hint) und Risiko (Safe, MayFailOnValues, NeedsEmptyColumn, DataLoss, NotExecutable); die Reihenfolge folgt aus der Art. `ColumnChange.Steps(before, after, intent)` auf `OracleTypes.ChangeOf`; `DdlScript.Render` mit `;` bzw. `/` je Art. Vergleich (`DdlWriter`) und Designer (`TableDesign`) nutzen es; Tests „gleiche Ausgabe“. Behebt nebenbei A4: der Vergleich nennt `NUMBER(10)`→`NUMBER(10,2)`, NVARCHAR2→VARCHAR2 als „kann an Daten scheitern“, richtig ist „nur bei leerer Spalte“.
  - `SqlScript` aufteilen: `SqlTokenizer`, `SqlSplitter`, `SqlClassifier`, `SqlAnalysis` hinter der bisherigen Fassade; die Sperren hängen dann nur an Tokenizer und Classifier. Der Blockmodus für PL/SQL bis zur `/`-Zeile kommt mit WP-26.
- **Übrige Struktur:** gemeinsamer `StatementRunner` in `State/` für SQL-Editor, Designer und LINQ-Konsole (Lesen, Schreiben, DDL mit Verlauf, Sperrhaltern, Hinweis); `GridWriteHistory` aus `WorkspaceEditing` mit Tests (`Forget` auch beim Schließen eines Workspaces); Entwurfslogik aus `TableDesigner.razor`/`IndexesView.razor` in State-Klassen (eine Regel „Name folgt den Spalten, bis er getippt wurde“); Fake-Host-Tests für den ModelHost-Prozessweg (offen seit R3b), Neustart-Koordination aus `LinqConsoleService` herauslösen; Effizienz: `SqlCode` vergleicht nach Inhalt, der Explorer rendert nicht bei jeder Zellbearbeitung alle Tabellenlisten (vorher messen); Kleinkram: Grid-Farben aus den Tokens, `WorkspaceActionsBar` und ein gemeinsamer Helfer fürs Speichern von Einstellungen, Bearbeitungshistorie aus `ChangeTracker` in eine eigene Klasse.

### WP-26 Audit-/Historientabellen aus einer Vorlage (nach WP-23)
Wunsch der Kollegen des Nutzers (2026-10-07): beim Anlegen einer Tabelle die Historientabelle und den Trigger gleich mit erzeugen, für bestehende Tabellen nachziehen. Entscheidung des Nutzers: das Schema kommt aus einer **Vorlagendatei**, nicht fest aus dem Code – damit FerretSharp auch außerhalb seiner Firma passt.
- Eingebaute, dokumentierte Standardvorlage; in den Einstellungen ein Pfad zu einer eigenen Vorlage (z. B. im Repo der Firma); später evtl. je Verbindung überschreibbar.
- Vorlage = SQL mit Platzhaltern (Tabelle, Historientabelle, Spalten mit Typen, Listen für `:OLD.`/`:NEW.`, Wiederholung über die Spalten); eigene minimale Syntax statt einer Template-Bibliothek (neue Abhängigkeit bräuchte ein ADR). Die Firmenvorlage des Nutzers (Tabelle + Trigger) dient als Testfall.
- Nachziehen: Spalte in `KUNDEN` neu → fehlt in `KUNDEN_HIST`/Trigger veraltet → `ALTER` + `CREATE OR REPLACE TRIGGER` vorschlagen (Vergleichslogik aus WP-20 wiederverwenden). Passt zum Backlog-Eintrag „Audit-/Historientabellen: Unterschiede hervorheben“ (Muster 1).
- Trigger enthalten PL/SQL – WP-22 weist PL/SQL-Blöcke und PL/SQL-Objekte (`CREATE [OR REPLACE] PROCEDURE/FUNCTION/PACKAGE/TRIGGER/TYPE`) ab (Entscheidung des Nutzers 2026-10-10: eigene Stufe). Für `CREATE [OR REPLACE] TRIGGER` aus der Vorlage braucht es eine bewusste, enge Ausnahme (beim Start von WP-26 mit dem Nutzer entscheiden, ADR): Splitter für Blöcke bis zur `/`-Zeile, Kompilierfehler (ORA-24344) aus `ALL_ERRORS` anzeigen.

## Offene UX-Fragen
- Shortcut-Belegung für Commit/Rollback – vorläufig, Nutzerfeedback einholen.
- Workspaces: Standardname ist „Workspace N“ mit der kleinsten freien Nummer (nach Umbenennen von „Workspace 1“ heißt der nächste wieder „Workspace 1“). Shortcuts zum Wechseln (z. B. Ctrl+1…9) und eine Oberfläche für die Notizen fehlen noch.
