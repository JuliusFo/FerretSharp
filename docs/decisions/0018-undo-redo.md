# ADR 0018: Undo bis zu einer Aktion, Redo und Zellverlauf

- Status: akzeptiert
- Datum: 2026-10-09
- Paket: WP-30 (Issue #7)

## Kontext

Seit R1 (3.6.0) teilen Grid-Schreibvorgänge und SQL-/LINQ-Statements die Transaktion des Workspaces als `IDataEditor.Actions`, jede hinter einem eigenen Savepoint. ↶ nahm nur die neueste Aktion zurück, Redo gab es nicht, und ausstehende Änderungen ließen sich nur zeilenweise verwerfen. Was offen war, zeigte nur ein Tooltip mit Kurztexten.

Gewünscht (Issue #7): alle offenen Änderungen auf einen Blick mit vollem SQL, Zurücknehmen bis zu einer gewählten Aktion, Redo, Ctrl+Z/Ctrl+Y für einzelne ausstehende Zellen. Entscheidungen des Nutzers: Seitenpanel, nur der aktive Workspace (mit Hinweis auf andere), Ctrl+Z im Grid getrennt von ↶, **Redo auch für Statements** – mit Bestätigung und Prüfung der Zeilenzahl –, alles in einem Release.

## Entscheidung

- **Undo bis X = `ROLLBACK TO SAVEPOINT` von X.** Oracle rollt Savepoints nicht selektiv zurück; X und alle späteren Aktionen gehen mit, auch Statements anderer Tabs. Das Panel markiert sie vorher. Der Aufrufer nennt die neueste Aktion, die er gesehen hat; wurde inzwischen weiter geschrieben, wird abgelehnt (wie bisher bei ↶).
- **Buchführung als reine Klasse `WriteLog`** (Core/Data, Unit-Tests): Aktionen mit Savepoints und der Redo-Stapel. Zurückgenommene Aktionen werden wiederholbar, die älteste zuerst; ein Redo muss die nächste sein. Jede andere Schreibaktion, Commit und Rollback beenden Redo; eine anderswo beendete Transaktion (Verbindungsverlust, Sperren) ebenso, weil der Editor beides nur innerhalb einer offenen schreibenden Transaktion zeigt.
- **`WriteAction` behält, was lief**: `Statements` mit Bind-Werten (Grid: die DML ohne die sperrenden SELECTs; SQL/LINQ: das Statement, bei LINQ das erzeugte SQL). `ToString` lässt sie weg – Bind-Werte von Prod dürfen nie in ein Log, in der UI werden sie gezeigt. `RedoOf` nennt die erste Aktion der Linie, damit Ergebnisanzeigen ein wiederholtes Statement als „offen“ erkennen.
- **Redo eines Grid-Schreibvorgangs = dieselben Änderungen erneut schreiben.** Nach dem Undo sind sie wieder ausstehend; Redo schreibt genau diese Zeilen (`ChangeTracker.PendingOperations(batch)`) mit der üblichen Sperre und Konfliktprüfung (`FlushOptions.RedoOf`). Wurden die Zeilen inzwischen bearbeitet oder verworfen (`IsPendingAsUndone`), ist Redo gesperrt, bis die Bearbeitung zurückgenommen ist.
- **Redo eines Statements = dasselbe SQL mit denselben Bind-Werten**, aber erst nach einer Bestätigung (Statement, Zeilen beim ersten Lauf, auf Prod der Verbindungsname). Zwischen Undo und Redo können andere Sessions die Zeilen geändert haben – `ROLLBACK TO SAVEPOINT` gibt auch die Zeilensperren frei. Weicht die Zeilenzahl ab, fragt FerretSharp: behalten oder zurücknehmen (ein Undo dieser einen Aktion, sie bleibt wiederholbar). Das ist nicht gefährlicher als das erneute Ausführen von Hand.
- **Kein neuer Schreibweg** (ADR 0006 bleibt): Redo nutzt `FlushAsync` bzw. den Weg von `ExecuteAsync` über `OracleSession.ExecuteNonQueryAsync`.
- **Zellverlauf im `ChangeTracker`**: jede Bearbeitung der ausstehenden Änderungen (Zelle, LOB, neue Zeile, Löschmarke, Verwerfen von Zelle, Zeile oder Tab) ist ein Schritt mit dem Zustand der berührten Zeilen davor und danach (höchstens 500). Ctrl+Z/Ctrl+Y (einstellbar, Bereich `Grid`) gehen ihn durch; eine neue Bearbeitung beendet Redo, ein Schreibvorgang, sein Undo, Commit und Rollback beenden den Verlauf. Ctrl+Z nimmt nie Geschriebenes zurück – das tut ↶ –, weil ein Savepoint-Rollback den ganzen Workspace trifft.

## Konsequenzen

- Ein Grid-Redo, das an einem Konflikt scheitert, zeigt den Schreibproblem-Dialog; dessen Entscheidungen schreiben danach alles Ausstehende als neue Aktion – das beendet Redo.
- Redo eines Grid-Schreibvorgangs ist nur möglich, solange sein Tab offen ist.
- `WriteAction` hält die Bind-Werte (auch LOB-Werte) bis Commit oder Rollback im Speicher – wie schon die `FlushBatch` für das Undo.
- Nach einem Redo einer Statement-Aktion steht sie mit neuer Id in `Actions`; wer eine Aktion über die Zeit verfolgt, vergleicht `Origin`.
