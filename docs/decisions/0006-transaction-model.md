# ADR 0006: Transaktionsmodell – gesperrte Sessions per READ ONLY-Transaktion, eigener Schreibweg

- Status: akzeptiert
- Datum: 2026-10-04

## Kontext

v2 bringt das Editieren (WP-09). Vorher braucht jede Workspace-Session ein Transaktionsmodell (WP-08), und Verbindungen mit „Schreibgeschützt“ im Profil (Prod voreingestellt) sollen nicht nur durch FerretSharp, sondern durch Oracle selbst geschützt sein. Die v1-Regel „kein Codepfad, der DML ausführt“ fällt damit; die Lesesperre (`IsReadOnlyStatement`) soll aber nicht aufgeweicht, sondern bewusst durch einen zweiten, eng gefassten Weg ergänzt werden.

Integrationstests (`TransactionBehaviorTests`) haben das Verhalten von Oracle 23 und ODP.NET (managed 23.26) festgehalten:

- `SET TRANSACTION READ ONLY` wirkt nur als erstes Statement einer **ODP.NET-Transaktion** (`BeginTransaction`). Ohne sie committet ODP.NET nach jedem Statement, die Read-only-Transaktion endet sofort und schützt nichts.
- Commands der Connection laufen ohne gesetzte `Transaction`-Eigenschaft automatisch in der offenen Transaktion.
- DML in der Read-only-Transaktion → ORA-01456. **DDL läuft trotzdem** (implizites Commit).
- Ein zweites `SET TRANSACTION` in derselben Transaktion → ORA-01453.
- Schließen einer Connection mit offener Transaktion rollt zurück.
- Ein Snapshot scheitert an Tabellen, die sich nach seinem Beginn geändert haben: ORA-01466 (DDL, auch noch rund eine Sekunde danach), ORA-08176 (erstes Segment einer Tabelle mit verzögerter Segment-Erzeugung), ORA-01555 (Undo zu alt).

## Entscheidung

- **Gesperrte Sessions:** Workspace-Sessions eines schreibgeschützten Profils laufen immer in einer `SET TRANSACTION READ ONLY`-Transaktion (`OracleSession.UseReadOnlySnapshotsAsync`, aufgerufen vom `WorkspaceManager` direkt nach dem Öffnen). Schlägt das fehl, wird die Session geschlossen und nicht benutzt. Eine schreibende Transaktion ist dort nicht möglich (WP-10 bringt die Freischaltung). Die Explorer-Session bleibt ohne Transaktion (nur Data Dictionary, sonst wäre auch das Schema eingefroren).
- **Neuer Snapshot je neuer Abfrage** (Entscheidung des Nutzers, abweichend vom ursprünglichen „Snapshot bis F5“): Die erste Seite einer Grid-Abfrage (`PageSpec.Offset == 0`: Tab öffnen, Filter, Sortierung, F5) startet einen neuen Snapshot; weitere Seiten derselben Abfrage bleiben darin – Paging kann bei gleichzeitigen Änderungen keine Zeilen doppelt liefern oder auslassen. Zählen und FK-Counts laufen im aktuellen Snapshot. Mehrere Tabs eines Workspace teilen die Session, also auch den Snapshot.
- **Snapshot-Fehler:** ORA-01555/01466/08176 starten den Snapshot neu und wiederholen die Abfrage (bis zu drei Mal; bei ORA-01466 mit 1, 2, 3 s Pause). Der Lese-Callback darf deshalb keinen Zustand über Aufrufe hinweg halten.
- **Schreibweg:** `OracleSession.ExecuteNonQueryAsync` ist `internal` und nimmt nur ein einzelnes INSERT, UPDATE oder DELETE (`IsWriteStatement`) innerhalb einer offenen Transaktion – nie Autocommit, nie DDL (würde implizit committen, auch in einer Read-only-Transaktion). In einer gesperrten Session lehnt Oracle das Statement mit ORA-01456 ab. Die Lesesperre bleibt unverändert.
- **Transaktionssteuerung** (`BeginTransactionAsync`, Savepoints über `OracleTransaction.Save/Rollback(name)`, Commit, Rollback) gibt es nur an `OracleSession`; nach außen (`IDatabaseConnection`) nur der Zustand (`TransactionInfo`) und das Sperren. `ReadOnlyTests` prüfen das.
- **Dispose** rollt eine offene Transaktion explizit zurück.

## Konsequenzen

- Auf schreibgeschützten Verbindungen zeigt jeder Tab den Datenstand („Stand 14:02:13“); Änderungen anderer erscheinen mit der nächsten Abfrage oder F5.
- Gegen DDL schützt weiterhin nur die Lesesperre bzw. der Schreibweg von FerretSharp; echte Sicherheit gibt nur ein DB-User mit reinen SELECT-Rechten. Nachtrag WP-22: DDL läuft über einen eigenen engen Schemaweg (`ExecuteDdlAsync`, nur ohne offene Transaktion, nie in einer gesperrten Session; ADR 0019) – Lesesperre und Schreibweg bleiben unverändert.
- WP-09 baut `FlushAsync` auf `ExecuteNonQueryAsync` und den Savepoints auf; WP-14 (Explain-Plan, „geschätzt“) braucht für `EXPLAIN PLAN` eine Erweiterung von `IsWriteStatement`.
- Der Keep-alive pingt auch Sessions mit offener Transaktion; bei schreibenden Transaktionen (Locks) in WP-09 neu bewerten.
