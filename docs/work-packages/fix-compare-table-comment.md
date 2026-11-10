# Fix: DDL-Vorschlag des Schema-Vergleichs schreibt den Tabellenkommentar (`fix/compare-table-comment`)

Branch `fix/compare-table-comment` (von `main`), 2026-11-10. Baut auf WP-20 (Schema-Vergleich, DDL-Vorschlag).

## Anlass
Frage des Nutzers: „Der DDL-Vorschlag beim Schema-Vergleich, wenn eine ganze Tabelle übernommen wird, nimmt den Tabellenkommentar nicht mit, oder sehe ich das aktuell falsch?“ – stimmte. `DdlWriter.CreateObject` schrieb nach `CREATE TABLE` nur `COMMENT ON COLUMN`; der Snapshot (`ObjectSnapshot`) kannte den Tabellenkommentar gar nicht, die Spaltenkommentare liest `ColumnsSelect` schon immer mit. Der Designer schrieb den Tabellenkommentar beim Anlegen bereits.

## Entscheidung des Nutzers (2026-11-10)
- „Ja, ergänzen“: Tabellenkommentar in Snapshot und DDL-Vorschlag.

## Gebaut
- Core `ObjectSnapshot.Comment` (optional, letzter Parameter).
- `OracleSchemaReader.ReadSnapshotAsync`: eine Abfrage mehr für das ganze Schema (`SnapshotCommentsSql`, `ALL_MVIEW_COMMENTS` und `ALL_TAB_COMMENTS` per `UNION ALL`); bei einer Materialized View gilt wie in `GetObjectInfoAsync` deren eigener Kommentar vor dem der Containertabelle.
- `DdlWriter.CreateObject`: `COMMENT ON TABLE` nach `CREATE TABLE` über `OracleDdl.CommentOnTable`; `DdlScript.Order` stellt ihn zu den Kommentaren ans Ende, vor die Spaltenkommentare derselben Tabelle.
- Kommentare zählen weiter nicht als Unterschied (`CompareDefinitions`): Bei Tabellen auf beiden Seiten schlägt der Vergleich keinen geänderten Tabellen- oder Spaltenkommentar vor (Backlog, zweite Stufe des Vergleichs).
- Tests: `DdlOutputTests` (ganze Skripte mit Tabellenkommentar); Integration `SchemaSnapshotTests` (Snapshot liest denselben Kommentar wie die Objekt-Info, auch bei einer MView), `SchemaDdlTests` (der ausgeführte Vorschlag setzt den Kommentar im Ziel).

## Offen / nicht geprüft
- Nicht in der laufenden App geprüft (E2E).

## Nachtrag: Code-Review (2026-11-10)
Entscheidung des Nutzers: „Alle beheben“.
- 1 (K) Protokoll und Indexzeile fehlten – diese Datei.
- 2 (K) Backlog „Schema-Vergleich, zweite Stufe“ nannte Kommentare als ganz offen – präzisiert (Snapshot liest sie, offen sind Vergleich und Abgleich bei bestehenden Tabellen).
- Vorher schon da (K): Ein Kommentar mit `\r\n` kommt im Skript mit `\n` an (`DdlScript.Lines`), für Spaltenkommentare schon immer – im Backlog notiert.
