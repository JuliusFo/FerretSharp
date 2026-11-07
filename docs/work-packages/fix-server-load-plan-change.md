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
- ADR 0045 (Zeitfenster) nachgezogen.
- Test: `ServerLoadTrackerTests.A_statement_whose_one_row_names_another_plan_counts_only_what_it_did_since`. Mit dem alten Tracker liefert er 4.000.410 statt 160 Ausführungen, also das Bild aus der DB des Nutzers.

## Offen
- „Seit Laden“ zählt die Pläne mit `COUNT(DISTINCT plan_hash_value)` aus `V$SQLSTATS` und zeigt deshalb bei einer Zeile je SQL_ID immer einen Plan. Die Zahlen dort stimmen. Im Backlog.
- Nicht in der App gegen die DB des Nutzers geprüft; die Testcontainer-DB stellt Adaptive Cursor Sharing nicht verlässlich nach.

## Nachtrag: Code-Review (2026-11-07)
Ein unabhängiger Reviewer-Agent (`/branch-code-review`), zwei Durchgänge. Entscheidung des Nutzers: „Alle“.
1. **M Die Abfrage der geänderten Statements las `V$SQLSTATS` zweimal** (Unterabfrage auf dieselbe View, alle 30 s, auch auf Prod; Kosten eines Joins über Fixed Tables nur vermutet). Ihr Nutzen galt nur für eine Zeile je Plan, und selbst dann zählte eine verschwundene Plan-Zeile wieder ab 0. **Behoben:** zurück zu `WHERE last_active_time >= :since`, die Annahme „eine Zeile je SQL_ID und Container“ steht am SQL, der Kommentar an `Statements` sagt, was sonst passierte.
2. **K Die Unterabfrage war nicht nach `CON_ID` korreliert** (in der CDB-Root kamen die Zeilen aller PDBs mit). **Erledigt mit 1.**
3. **K Die Falle fehlte in der Regel.** **Behoben:** Satz in `.claude/rules/oracle.md` (Cursor-Statistik).
4. **K Drei Doku-Kommentare sprachen noch von „je Plan“/„je Cursor“** (`StatementCounters`, `StatementRow.Plans`, Tracker-Summary). **Behoben**, dazu `IServerLoadReader.ReadStatementsAsync`.
5. **K Der Schlüssel war nur für Container 0 getestet.** **Behoben:** `Cursor(…, con:)` und `The_same_statement_in_two_containers_counts_per_container_and_adds_up_in_the_list` (scheitert, wenn `ContainerId` aus dem Schlüssel fällt).
- Vorher schon da: Planzahl bei „Seit Laden“ immer 1 → `docs/backlog.md`.
- Learnings: Die Falle (eine Zeile je SQL_ID, wechselnder Plan-Hash) steht jetzt in `oracle.md` (Punkt 3). Punkt 1 war ein Einzelfall (eine Absicherung für einen nur gedachten Fall), keine neue Regel.
- CI: Der erste Lauf des PR scheiterte in `ServerLoadTests.A_statement_another_session_runs_…` an der 10-s-Grenze (die Werte in `V$SQLSTATS` kommen verzögert, wie nach WP-75). Lokal 6 von 6 Läufen grün; der Job wurde neu gestartet.
