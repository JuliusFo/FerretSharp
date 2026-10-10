# ADR 0019: DDL im SQL-Editor – ein eigener enger Schemaweg neben dem Schreibweg

- Status: akzeptiert
- Datum: 2026-10-10

## Kontext

WP-22: Tabellen anlegen und ändern passend zum DB-first-Ablauf des Nutzers („erst die DB ändern, dann die Entity“). Bisher wies der SQL-Editor jedes DDL ab (ADR 0014), weil Oracle vor und nach DDL committet – auch die offene Transaktion des Workspaces, und zwar auch in einer READ-ONLY-Transaktion (`TransactionBehaviorTests`). Gegen DDL schützten nur die Statement-Schranken der Session (ADR 0006); die Lesesperre darf dafür nicht aufgeweicht werden.

Entscheidungen des Nutzers (2026-10-06 und 2026-10-10):
- DDL auf allen schreibbaren Workspaces – Profile ohne „Schreibgeschützt“ und Prod nach dem Freischalten (WP-10). Gesperrte Workspaces nie.
- FerretSharp führt das DDL aus, nicht nur kopieren.
- Ist eine Transaktion offen, wird sie nur nach Bestätigung verworfen; der Dialog zeigt, was verloren geht. „Abbrechen“ führt nichts aus.
- Weiter abgewiesen: PL/SQL-Blöcke, `CALL`/`EXEC`, `ALTER SESSION/SYSTEM`, `COMMIT`/`ROLLBACK` und `TRUNCATE` (löscht alle Zeilen ohne Rückweg). PL/SQL-Objekte (`CREATE [OR REPLACE] PROCEDURE/FUNCTION/PACKAGE/TRIGGER/TYPE`) bleiben vorerst abgewiesen – eigene Stufe vor bzw. mit WP-26.
- Bestätigung vor jedem DDL (auf Prod mit dem Namen der Verbindung), in Skripten eine für alle DDL-Statements; der Dialog sagt, dass DDL nicht rückgängig zu machen ist.
- Den DDL-Vorschlag des Schema-Vergleichs (WP-20) im SQL-Editor öffnen und dort ausführen.
- Kein Auto-Commit-Paket vorab (Backlog).

## Entscheidung

- **Ein eigener Weg, so eng wie der Schreibweg:** `OracleSession.ExecuteDdlAsync(sql)` ist `internal`, nimmt genau ein DDL-Statement (`IsDdlStatement` → `StatementGuard.IsDdl`) und läuft nur ohne offene Transaktion – also nie in einer gesperrten Session, die immer ihre READ-ONLY-Transaktion offen hat; beides wird zusätzlich mit eigener Meldung abgewiesen. Ohne ODP.NET-Transaktion committet Oracle das DDL selbst, wie jedes DDL.
- `IsReadOnlyStatement` und `IsWriteStatement` bleiben unverändert; der Schreibweg nimmt weiter nie DDL.
- **Was als DDL gilt, entscheidet dieselbe Einordnung wie im Editor** (`SqlScript.KindOf`): erstes Wort CREATE, ALTER (nicht SESSION/SYSTEM), DROP, RENAME, COMMENT, GRANT, REVOKE, ANALYZE, AUDIT, NOAUDIT, FLASHBACK, PURGE, ASSOCIATE, DISASSOCIATE. Eigene Arten für `TRUNCATE` und PL/SQL-Objekte (`CREATE` mit PROCEDURE/FUNCTION/PACKAGE/TRIGGER/TYPE/JAVA nach `OR REPLACE`, `EDITIONABLE` …). Die Schranke verlangt zusätzlich ein einzelnes Statement und keine Bind-Variablen (Oracle erlaubt in DDL keine, ORA-01027).
- Darüber `IDataEditor.ExecuteDdlAsync` (über dieselbe Semaphore wie Flush, Statement, Undo, Commit; leert die Aktionsliste, Undo gibt es für DDL nicht) und `WorkspaceManager.ExecuteDdlAsync` (prüft `IsWritable`, bevor die Session berührt wird).
- **Rollback vorher macht die UI, nach Bestätigung:** Der Dialog im SQL-Editor zeigt die DDL-Statements, auf Prod die Verbindung und – bei offener Arbeit – die Kompaktliste der offenen Änderungen (`OpenChangesList`, auch im Prod-Commit-Dialog). „Verwerfen und ausführen“ ruft den normalen Rollback (`WorkspaceEditing.RollbackAsync`), dann das DDL. Der Core weist DDL bei offener Transaktion ohnehin ab; ein Wettlauf führt also nie zu einem stillen Commit.
- **Skripte:** DML vor einem DDL-Statement wird vor dem Lauf abgewiesen (das DDL würde sie still committen). DDL zuerst und DML danach ist erlaubt (Tabelle anlegen, dann befüllen) – die DML bleibt in der Transaktion bis zum Commit des Nutzers.
- **Danach:** Schema-Cache neu laden, Tabs verschwundener Objekte schließen, Tabellen-Tabs mit geänderter Struktur neu aufbauen (`TableTab.StructureVersion`), das C#-Modell ohne Hilfsprozess neu abgleichen (`ClrModelManager.RemapAsync`) und neue „Spalten ohne Property“ nennen (`WorkspaceLifecycle.RefreshSchemaAsync`; „Schema neu laden“ im Explorer nutzt denselben Weg). Tabs mit nicht committeten Änderungen behalten ihre Struktur – die Änderungen adressieren Spalten nach Position – und werden genannt.
- Verlauf: DDL wie DML, mit `SqlHistoryEntry.Ddl` (ältere Dateien lesen es als `false`).
- **Andere offene Transaktionen:** DDL auf einer Tabelle, in die eine andere Session geschrieben und nicht committet hat, scheitert (DROP: ORA-00054, mit Hinweis auf den anderen Workspace) oder wartet (ALTER TABLE … ADD, siehe Konsequenzen). Der Bestätigungsdialog nennt deshalb andere Workspaces derselben Verbindung mit offener Schreib-Transaktion und die Tabellen, in die sie geschrieben haben (`WorkspaceLifecycle.OtherWriters`); die Laufanzeige erklärt nach einigen Sekunden, worauf ein DDL wartet.

## Konsequenzen

- DDL lässt sich nicht zurücknehmen; die Bestätigung und die Abweisung bei offener Transaktion sind der Schutz. Die einzige Garantie gegen ungewolltes DDL bleibt ein DB-Benutzer ohne DDL-Rechte.
- `ALTER TABLE … ADD` wartet in Oracle 23 auf die offene Transaktion einer anderen Session (`enq: TX - row lock contention`) statt mit ORA-00054 abzubrechen. Das Warten lässt sich **nicht** abbrechen: `OracleCommand.Cancel()`, ein `CommandTimeout` und das Schließen der Connection (also auch „Session trennen“) beenden es nicht; sobald die andere Transaktion endet, läuft das DDL durch (Versuch gegen Oracle 23 Free in WP-22). Abhilfe ist nur, die andere Transaktion zu committen oder zu verwerfen – deshalb die Warnung vorher. Sessions anderer Programme oder anderer Verbindungen sieht FerretSharp nicht.
- DDL verändert den Snapshot anderer gesperrter Workspaces (ORA-01466); deren Abfragen starten wie bisher einen neuen Snapshot und wiederholen.
- PL/SQL-Objekte, Trigger aus Vorlagen (WP-26) und PL/SQL ausführen brauchen eine eigene Entscheidung (Splitter für `/`-Blöcke, Kompilierfehler aus `ALL_ERRORS` statt ORA-24344).
