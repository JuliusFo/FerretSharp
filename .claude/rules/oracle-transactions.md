---
paths:
  - "src/FerretSharp.Core/Oracle/**"
  - "src/FerretSharp.Core/Data/**"
  - "src/FerretSharp.Core/Workspaces/**"
  - "src/FerretSharp.Core/Design/**"
  - "src/FerretSharp.UI/State/Workspace*"
---

# Transaktionen, Sperren und DDL in Oracle

Modell der Änderungen (Pending → Flushed → Committed, Undo-Stapel, Schemaweg): `docs/architecture.md` 1.6, ADR 0006/0007/0018/0019.

## Transaktionen und Sperren
- Uncommittetes Update in Workspace A blockiert ein Update derselben Zeile in B. Ein normales `UPDATE` wartet unbegrenzt; `ORA-00054` gibt es nur bei `NOWAIT`. Deshalb `FOR UPDATE WAIT n` (→ `ORA-30006`, in Oracle 23 `ORA-00054`) und Schreibproblem-Dialog mit den Sperrhaltern (`GetLockHoldersAsync`: `V$LOCKED_OBJECT`/`V$SESSION`, falls Rechte vorhanden – ein Entwicklerbenutzer darf sie meist nicht lesen, ORA-00942).
- Concurrency-Check über die Werte, die das Sperr-SELECT liefert, verglichen in .NET (`OracleDataEditor`) – nicht über `ORA_ROWSCN` (nur zuverlässig bei `ROWDEPENDENCIES`-Tabellen).
- Lange offene Transaktionen halten Locks und Undo. Die Statusleiste zeigt „Tx seit X · N ausstehend · M geschrieben“ und warnt ab 10 min.
- Prod im gesperrten Zustand: `SET TRANSACTION READ ONLY` (Oracle erzwingt das selbst). Snapshot-Semantik, Entscheidung des Nutzers (WP-08, ADR 0006): Jede neue Abfrage (erste Seite: Tab öffnen, Filter, Sortierung, F5) startet einen neuen Snapshot, weitere Seiten bleiben darin; nach Rollback neu. Bei ORA-01555, ORA-01466 (DDL nach Snapshot-Beginn, auch noch ~1 s danach) und ORA-08176 (erstes Segment einer Tabelle mit verzögerter Segment-Erzeugung) automatisch neu starten und wiederholen.
- `SET TRANSACTION READ ONLY` nur innerhalb einer ODP.NET-Transaktion (`BeginTransaction`) – ohne sie committet ODP.NET sofort und der Schutz ist weg. DDL läuft auch in einer Read-only-Transaktion (implizites Commit), und autonome Transaktionen (`SELECT pkg.f() FROM dual` mit `PRAGMA AUTONOMOUS_TRANSACTION`) schreiben trotzdem (A26). Belegt in `TransactionBehaviorTests`.

## DDL gegen fremde offene Transaktionen (WP-22, ADR 0019)
- `DROP TABLE` auf einer Tabelle mit nicht committeter DML einer anderen Session scheitert sofort mit ORA-00054. `ALTER TABLE … ADD` dagegen **wartet** (`enq: TX - row lock contention`), bis die andere Transaktion endet – und weder `OracleCommand.Cancel()` noch `CommandTimeout` noch `Close()` beenden das Warten (`Close` blockiert); danach läuft das DDL trotzdem durch. Ein Test, der darauf wartet, hängt.
- Sofort ORA-00054: `MODIFY`, `DROP/RENAME COLUMN`, `ADD CONSTRAINT`, `CREATE INDEX`, `DROP TABLE`, `RENAME`. Kein Konflikt: `COMMENT`, `GRANT`, `CREATE VIEW`, `CREATE [OR REPLACE] TRIGGER`, `ALTER TRIGGER … ENABLE/DISABLE`, `DROP TRIGGER` (die nächste DML der offenen Transaktion feuert den neuen Trigger schon).
- Deshalb holt sich der Schemaweg vor `ALTER TABLE` die Tabelle mit `LOCK TABLE … IN EXCLUSIVE MODE NOWAIT` (scheitert bei fremder DML nach Millisekunden, braucht keine Rechte; ein DDL danach in derselben ODP.NET-Transaktion läuft, und `Rollback`/`Dispose` des Transaktionsobjekts danach schadet nicht).

## ALTER TABLE … MODIFY mit Daten (WP-23, gemessen gegen Oracle 23)
- Was ein Typwechsel braucht, steht nur in `OracleTypes.ChangeOf`: frei / scheitert an zu langen Werten (ORA-01441) / nur bei leerer Spalte (ORA-01439, ORA-01440, ORA-30082) / nie per MODIFY bei LOB (ORA-22858, auch leer).
- Nicht naheliegend: `NUMBER(10)` → `NUMBER(10,2)` braucht eine leere Spalte (weniger Vorkommastellen), `TIMESTAMP` → `DATE` geht mit Daten, NVARCHAR2 → VARCHAR2 nicht; `MODIFY (x NULL)` auf eine schon nullable Spalte ist ORA-01451; Identity nur für neue Spalten (ORA-30673).
- Zählungen für die Prüfungen über den Leseweg (`TableDesign.CountAsync`), nie mit eigenem SQL.
