# Fix: Server-Load-Zeitfenster zählt bei Planwechsel die ganze Lebenssumme → Release offen

Branch `fix/server-load-plan-change` (von `main`), 2026-11-07, unveröffentlicht.

## Anlass
Nutzerfeedback nach 3.63.0 (WP-75): Im Zeitfenster „Seit Öffnen“ stand nach weniger als 5 Minuten ein Statement mit knapp 5 Mio. Ausführungen und 154.608 s CPU, also rund 43 CPU-Stunden.

Diagnose mit dem Nutzer an seiner DB:
- „Letzte 15 Min“ zeigte 4.981.193 Ausführungen und 154.645 s CPU bei „3 plans“.
- `V$SQLSTATS` hatte für die SQL_ID **eine** Zeile (ein Plan-Hash, `CON_ID` 0) mit 2.490.431 Ausführungen und 77.319 s CPU. Das Fenster zeigte also fast genau die doppelte Lebenssumme; der Rest von etwa 330 Ausführungen lief wirklich in den 15 Minuten.
- `V$SQL` zeigte 42 Child-Cursor mit mindestens fünf verschiedenen Plan-Hashes, alle `IS_BIND_AWARE = Y` (Adaptive Cursor Sharing).

Ursache: Der Tracker unterschied Cursor nach (SQL_ID, Plan-Hash, Container). `V$SQLSTATS` führt aber eine Zeile je SQL_ID, deren `PLAN_HASH_VALUE` dem zuletzt aktiven Child folgt, während die Zahlen über alle Pläne weiterlaufen. Ein noch nicht gesehener Plan-Hash galt als neu geladener Cursor und zählte ab 0, also mit der ganzen Summe seit dem Laden. Ein Wechsel zurück auf einen bekannten Plan-Hash schadete nicht.

Entscheidung des Nutzers: Fix bauen (nach Bestätigung über `V$SQL`). Mein Vorschlag, eine kleiner werdende Summe als neuen Ausgangswert zu nehmen statt ab 0 zu zählen, ist entfallen: Mit dem Schlüssel je Statement heißt „kleiner“ wieder wirklich „neu geladen“.

## Gebaut
- `ServerLoadTracker`: Schlüssel `CursorKey(SqlId, ContainerId)` ohne Plan-Hash. Die Zeilen eines Blicks werden je Schlüssel summiert (`Statements`), die genannten Plan-Hashes gehen in die Menge `Plans` (Anzeige „N plans“ = Pläne, die während des Zuschauens auftauchten).
- `OracleServerLoadReader.StatementsSinceSql` liest alle Zeilen der seit dem Blick davor aktiven SQL_IDs (`sql_id IN (SELECT … WHERE last_active_time >= :since)`). Bei einer Zeile je SQL_ID ändert das nichts; sollte eine Version eine Zeile je Plan führen, fehlen in der Summe sonst die gerade nicht aktiven Pläne, und sie würde kleiner.
- ADR 0045 (Zeitfenster) nachgezogen.
- Test: `ServerLoadTrackerTests.A_statement_whose_one_row_names_another_plan_counts_only_what_it_did_since`. Mit dem alten Tracker liefert er 4.000.410 statt 160 Ausführungen, also das Bild aus der DB des Nutzers.

## Offen
- „Seit Laden“ zählt die Pläne mit `COUNT(DISTINCT plan_hash_value)` aus `V$SQLSTATS` und zeigt deshalb bei einer Zeile je SQL_ID immer einen Plan. Die Zahlen dort stimmen. Für die echte Planzahl bräuchte es `V$SQLSTATS_PLAN_HASH` oder `V$SQL`; nicht in diesem Fix.
- Nicht in der App gegen die DB des Nutzers geprüft; die Testcontainer-DB stellt Adaptive Cursor Sharing nicht verlässlich nach.
