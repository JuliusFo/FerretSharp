# ADR 0020: Tabellen-Designer – ein Entwurf als Soll-Zustand, DDL je Änderung, Prüfungen vorab

- Status: akzeptiert
- Datum: 2026-10-11

## Kontext

WP-23 Stufe 2: Spalten und Constraints einer bestehenden Tabelle in der Spalten-Ansicht ändern (Entscheidungen des Nutzers: Bearbeitungsmodus der Spalten-Ansicht, Änderungen sammeln und als ein Skript ausführen, ausführen/kopieren/im SQL-Editor öffnen/speichern; C# nur aus dem Modell-Export). Ausgeführt wird über den Schemaweg aus WP-22 (ADR 0019). Der DDL-Vorschlag des Schema-Vergleichs (WP-20) kennt dafür schon die Bausteine (`DdlText`, `OracleTypes`), arbeitet aber auf einem Vergleich zweier Stände: Er kann nicht wissen, ob eine fehlende Spalte umbenannt oder gelöscht wurde, und schreibt beides nur als Hinweis.

Ein Experiment gegen Oracle 23 (Integrationstest, danach entfernt) hat geklärt, welche Änderungen mit Daten in der Spalte gehen – siehe `OracleTypes.ChangeOf` und das Protokoll in `docs/work-packages/wp-23-table-designer.md`.

## Entscheidung

- **Absicht statt Diff:** Der Entwurf (`TableDraft`) beschreibt die Tabelle, wie sie sein soll, mit der Herkunft jeder Spalte (`ColumnDraft.Original`: vorhanden, umbenannt, gelöscht, neu) und den Constraint-Änderungen. `TableDesign.Plan` schreibt daraus ein Statement je Änderung in fester Reihenfolge: Constraints löschen, Spalten löschen, umbenennen, Typ/Default/NULL ändern, Spalten hinzufügen, Constraints hinzufügen (Schlüssel vor Fremdschlüsseln), Index für neue Fremdschlüssel, Kommentare. Umbenennen und Löschen sind echtes DDL, kein Hinweis. Wiederverwendet werden die Fragmente aus WP-20 (`DdlText.ColumnDefinition`, `ConstraintClause`, `CreateIndex`); `DdlWriter` bleibt unverändert.
- **Ein Statement je Änderung**, nicht ein `ALTER TABLE … MODIFY (a …, b …)`: Ein Fehler zeigt genau auf die Änderung, und die Lock-Vorabprüfung des Schemaweges gilt je Statement.
- **Was Oracle abweisen würde, wird vorher ein Problem** (Ausführen gesperrt, Grund im Panel): NOT NULL bei NULL-Werten, neue NOT-NULL-Spalte ohne Default in gefüllter Tabelle, Typwechsel, die eine leere Spalte brauchen oder per MODIFY nicht gehen (LOB), NULL im Primärschlüssel, Löschen referenzierter oder in Mehrspalten-Constraints steckender Spalten, zweiter Primärschlüssel, Fremdschlüssel ohne passenden Schlüssel oder mit unverträglichen Typen. Was nur an einzelnen Werten scheitern kann (kürzer, neue Constraints auf vorhandenen Zeilen), ist eine Warnung; Oracle meldet dann seinen eigenen Fehler.
- **Typwissen nur in `OracleTypes`:** `ChangeOf(von, nach)` ordnet einen Typwechsel ein (gleich, frei, kann an Werten scheitern, nur bei leerer Spalte, nicht per MODIFY) – belegt durch das Experiment.
- **Daten zählt die UI über den Leseweg:** Der Plan stellt Fragen (`DataQuestion`: Zeilen, NULLs, Werte einer Spalte), `TableDesign.CountAsync` beantwortet sie mit `IDataAccess.CountAsync` und `IS [NOT] NULL`-Filtern in der Session des Tabs – kein neues SQL, keine Änderung an der Lesesperre. Unbekannte Antworten (Fehler) werden zur Warnung statt zum Problem.
- **Nach einem Abbruch bleibt der Rest:** DDL ist nicht atomar; was lief, bleibt. Weil der Entwurf einen Soll-Zustand beschreibt, wird er auf die Tabelle, wie sie jetzt ist, umgelegt (`ColumnDesigner.Rebase`): Spalten werden über den Soll-Namen, sonst den alten Namen gefunden, erledigte Drops und Constraints fallen weg – der neue Plan enthält nur, was noch fehlt. Entwurf und Fehler stehen im Tab (`TableTab.Designer`), weil der Neuaufbau des Tabs nach Strukturänderungen den Designer mitten im Lauf abbaut; `RunDdlAsync` meldet den Abbruch deshalb vor dem Neuladen des Schemas.
- **C# im festen Stil** aus dem Modell-Export (`PropertyCode`): Name und Typ derselben Spalte anderswo im Modell, sonst PascalCase und der Typ, den EF Core liest; für einspaltige Texte der J/N-Converter des Projekts; `HasColumnName` nur ohne durchgängige Namenskonvention.
- Identity und virtuelle Spalten lassen sich im Designer nur umbenennen und kommentieren; Identity nur für neue Spalten (ORA-30673).

## Konsequenzen

- Ein Typwechsel gefüllter Spalten über „neue Spalte, umkopieren, alte löschen“ bleibt Handarbeit: Das Umkopieren ist DML, die zwischen zwei DDL-Statements still committet würde (ADR 0019). Der Designer sagt, wie es geht.
- Die Regeln von `ChangeOf` sind für Oracle 23 belegt; ältere Versionen (19c) können in Einzelfällen strenger sein – dann meldet Oracle seinen Fehler, und der Rest des Entwurfs bleibt.
- Der Designer ändert nur bestehende Tabellen; neue Tabellen und Entity aus Tabelle folgen in Stufe 3 mit demselben Entwurf (alle Spalten neu).

## Nachtrag R4b (2026-10-11)

- Vergleich und Designer teilen den DDL-Schreiber `Core/Ddl`: `OracleDdl` für jeden Text, `DdlStep` mit Art und Risiko, die Reihenfolge aus der Art (`DdlScript.Stage`) und ein Renderer (`DdlScript.Render`). Der Vergleich übernimmt die Reihenfolge des Designers (Constraints löschen zuerst, Kommentare zuletzt); der Satz oben, `DdlWriter` bleibe unverändert, gilt damit nicht mehr.
- Spaltenänderungen schreibt `ColumnChange.Steps` auf `OracleTypes.ChangeOf` für beide: mit bekannter Absicht (Designer) echtes DDL, ohne (Vergleich) Hinweise. Damit nennt auch der Vergleich Typwechsel, die eine leere Spalte brauchen, so (A4).

## Nachtrag Stufe 3 (2026-10-11)

- **Neue Tabelle** über denselben Entwurf: `TableDesign.PlanNew` schreibt die Spalten als ein `CREATE TABLE`, danach wie bei bestehenden Tabellen Constraints, FK-Indizes und Kommentare. Der Entwurf lebt in einem eigenen, **nicht gespeicherten** Tab (`NewTableTab`, `WorkspaceTab.IsSaved`): ein eigenes Dateiformat für Entwürfe wäre mehr als der Nutzen; Schließen fragt. Sobald `CREATE TABLE` gelaufen ist, gehört der Rest des Entwurfs zur echten Tabelle – er wird über `Rebase` zum Bearbeitungsmodus ihres Tabellen-Tabs.
- **Entity aus Tabelle** (`EntityCode`): fester Stil aus dem Modell-Export wie die Property-Zeile; Navigations nur für deklarierte FKs auf Tabellen mit Entity, `WithMany()` ohne Rück-Navigation (die Gegenseite gehört zur anderen Entity). Der Entity-Name ist ein Vorschlag nach dem Pluralmuster des Projekts, im UI änderbar. Eine Spalte gleichen Namens anderswo im Modell gibt Name, Typ und Converter nur vor, wenn ihre Property die neue Spalte lesen kann (`ColumnTypeCheck`).
