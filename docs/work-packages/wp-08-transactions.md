# WP-08 Transaktionsmodell & Schreibschutz
- `OracleSession` bekommt ein Transaktions-API (Begin, Savepoint, Commit, Rollback, `ExecuteNonQueryAsync`).
- Prod gesperrt → `SET TRANSACTION READ ONLY` mit Snapshot-Semantik (Refresh = neue Tx, `ORA-01555` → Neustart).
- Statusleiste: Tx-Alter, Anzahl Pending/Flushed, Warnung bei Verbindungsverlust mit offener Transaktion.
- **Fertig wenn:** Integrationstest belegt, dass DML auf einer gesperrten Prod-Session von Oracle abgelehnt wird. → erfüllt (`SessionTransactionTests.Oracle_rejects_dml_in_a_locked_session`, ORA-01456).
- Umgesetzt (Release 1.8.0, ADR 0006):
  - Gesperrt sind die Workspace-Sessions der Profile mit „Schreibgeschützt“ (Prod voreingestellt), nicht nur Prod (Entscheidung des Nutzers); Dev/Test ohne Häkchen laufen bis WP-09 ohne Transaktion. `WorkspaceManager` sperrt direkt nach dem Öffnen (`IDatabaseConnection.UseReadOnlySnapshotsAsync`), die Explorer-Session bleibt ohne Transaktion.
  - Snapshot je neuer Abfrage statt bis F5 (siehe Abschnitt 6); `RowPage.DataAsOf` → Tab-Fußzeile „Stand 14:02:13“.
  - Statusleiste: „schreibgeschützt (Oracle)“ bei gesperrten Profilen; Tx-Alter und Pending/Flushed folgen mit WP-09. Das Verlust-Banner warnt, wenn eine schreibende Transaktion offen war (`WorkspaceManager.TransactionOf`).
  - `BeginTransactionAsync` (nicht „BeginReadWrite“: `ReadOnlyTests` werten „Write“ im Namen als Datenschreiben).
