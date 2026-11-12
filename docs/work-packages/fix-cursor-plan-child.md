# Fix: Serverlast-Test findet den Cursor ohne Plan

Branch `fix/cursor-plan-child` (von `main`), 2026-11-10, Release 3.69.0.

## Anlass
CI-Lauf 42817332499 (Versuch 1, PR #166, ein reiner Doku-Commit; der Code-Commit davor war im selben Job grün):
`ServerLoadTests.A_statement_another_session_runs_shows_up_with_its_figures_text_and_plan` scheiterte an
`Assert.NotEmpty(cursor.Plan.Steps)`. `GetCursorByIdAsync` hatte den Cursor gefunden: Text aus `V$SQL` (`Plan.Sql`
stimmte), Zahlen aus `V$SQL`, aber keine Planzeilen. Der Test lief 2,9 s.

## Diagnose (eigener Testcontainer `gvenzl/oracle-free:23-slim-faststart`, nicht `ferret-sample`)
Probe wie im Test: das Statement 12-mal in einer eigenen Session, danach `V$SQL` je Child mit Planzeilen aus
`V$SQL_PLAN_STATISTICS_ALL` und `V$SQL_PLAN`, nach 0, 1,2 und 4 s.
- **Zwei Children immer, beide mit Plan.** In 103 Runden (43 ohne, 60 unter Last): Child 0 einmal ausgeführt,
  `IS_REOPTIMIZABLE = Y`, Child 1 mit den übrigen 11 („Auto Reoptimization Mismatch“, Statistics Feedback), gleicher
  Plan-Hash, je 120 Planzeilen. `LatestChild` (`LAST_ACTIVE_TIME DESC, CHILD_NUMBER DESC`) nimmt Child 1. Beide
  Vermutungen aus dem Auftrag sind damit widerlegt: Das neueste Child hatte seine Zeilen immer schon, und es gab kein
  zweites Child mit unveröffentlichtem Plan.
- **Oracle gibt den Plan vor dem Cursor frei.** `DBMS_SHARED_POOL.PURGE('<address>,<hash>', 'C', 64)` (nur Heap 6, als
  SYSTEM erlaubt) ergibt genau das CI-Bild: Beide Children bleiben in `V$SQL` mit Text, Ausführungen und
  `LAST_ACTIVE_TIME`, `V$SQL_PLAN_STATISTICS_ALL` hat für beide 0 Zeilen; `GetCursor…` liefert einen leeren Plan ohne
  Fehler. Das Purge trifft alle Children, auch mit der Adresse eines Childs. Eine weitere Ausführung lädt den Plan des
  genutzten Childs neu (120 Zeilen), Child 0 bleibt ohne.
- **Von selbst unter Last:** Nach rund 3 Minuten mit 16 Last-Sessions hatten im Container 92 von 159 SYS-Children und 5 von
  10 Probe-Children mit Ausführungen keine Planzeilen mehr (alle älter als rund 30 s; die zwei absichtlich verworfenen nicht mitgezählt), bei 70 MB freiem Shared Pool.
- **Binnen Sekunden** ließ sich nur der ganze Cursor verdrängen: Mit 24 Sessions, die verschiedene Statements mit 20 KB
  Text schickten (159.000 in 5 Minuten), war der Cursor in einer von 30 Runden nach 4 s ganz aus `V$SQL` verschwunden.
  Den Zustand „Cursor da, Plan weg“ binnen 3 s habe ich lokal nicht getroffen. Er ist aber der einzige gefundene
  Mechanismus für das CI-Bild; dazu kommt der Fall, dass der Cursor zwischen den Abfragen der Suche (`LatestChild`,
  dann `ActualSteps`) verschwindet.

Ursache: Unter der Last der ganzen Suite verdrängte Oracle den Plan des Cursors aus dem Shared Pool, bevor der Test ihn
las. Der Test nahm an, dass ein gefundener Cursor immer einen Plan hat.

## Entscheidungen
- **Kein Rückgriff auf ein älteres Child mit Plan.** Zuerst gebaut, dann verworfen: Das Purge traf alle Children zugleich,
  und bei LRU verliert das ältere Child (Child 0, eine Ausführung) seinen Plan eher als das neueste. Im CI-Fall hätte er
  kaum geholfen; ein Plan eines anderen Childs neben den Zahlen aller Children wäre zudem missverständlich.
  Vorschlag (nicht bestätigt).
- **Produkt:** Die Cursor-Details (Serverlast, Statements der App) zeigten bei leerem Plan eine leere Tabelle und den
  Hinweis zu GATHER_PLAN_STATISTICS. Jetzt sagen sie, dass der Plan nicht mehr im Shared Pool ist und mit der nächsten
  Ausführung wiederkommt (`CursorInfo.HasPlan`, `CursorDetails`, Text `AppStatements_PlanDropped` in Englisch und
  Deutsch).
- **Statement ohne Plan (aus dem Review):** PL/SQL-Blöcke (auch `SaveChanges` von EF), `CALL`, `ALTER SESSION` und die
  meiste DDL haben nie Planzeilen. Gemessen gegen Oracle 23: Ein verworfener Plan behält `PLAN_HASH_VALUE` (≠ 0, 0
  Planzeilen), Block und `CALL` haben 0 (`COMMAND_TYPE` 47 bzw. 170). `CursorFigures` liest deshalb
  `MAX(PLAN_HASH_VALUE)` (`CursorInfo.PlanHashValue`); `HasNoPlanAtAll` (kein Schritt und Hash 0) zeigt
  `AppStatements_NoPlanForStatement` („Dieses Statement hat keinen Ausführungsplan …“) statt des Verdrängt-Hinweises.
- **Tests:** `CursorPlans.FindAsync` liest den Cursor, bis er da ist und eine Bedingung hält, und lässt das Statement
  dazwischen noch einmal laufen – auch wenn er zwischen den Abfragen der Suche verschwindet (`PlanUnavailableException`,
  ein Timeout nennt die letzte Ablehnung). `ReadAsync` wartet darüber auf einen Plan; das gilt für den Serverlast-Test und
  `PlanTests.A_statement_another_program_ran_is_found_by_its_text` (gleiches Risiko), die Zahlen prüfen beide weiter am
  ersten Lesen.
- Neuer Test `ServerLoadTests.A_cursor_whose_plan_oracle_dropped_keeps_its_figures_and_has_the_plan_again_after_the_next_run`:
  Plan per `CursorPlans.DropAsync` verwerfen, dann Cursor mit Zahlen und leerem Plan ohne Fehler, nach einer weiteren
  Ausführung wieder mit Plan; der Hash bleibt (`HasNoPlanAtAll` falsch). Er hält fest, worauf der Hinweis und
  `ReadAsync` bauen. `DropAsync` meldet, ob es einen Cursor traf; ist er unter Last schon ganz weg, lädt der Test ihn neu.
- Neuer Test `ServerLoadTests.A_pl_sql_block_is_found_with_its_figures_and_marked_as_having_no_plan_at_all`: ein Block
  wird mit Zahlen gefunden, `HasNoPlanAtAll`, Hash 0.
- Falle in `.claude/rules/oracle.md` (Cursor-Statistik).

## Offen
- `AppTraceSourceTests.On_oracle_the_statements_are_found_in_v_sql_…` prüft ebenfalls den Plan eines fremden Cursors
  (`TR_POSITION`), lässt sich aber nicht einfach erneut ausführen (die Fake-App ist beendet). Bisher nicht geflackert.
- Der tatsächliche Plan im SQL-Editor (`ExplainActualAsync`) zeigt bei einem sofort verdrängten Plan weiter eine leere
  Tabelle mit „keine Laufzeitstatistik“; dort ist das Statement gerade gelaufen, also selten.
- Der Hinweis ist nicht in der App angesehen (E2E), nur gebaut.

## Nachtrag: Code-Review (2026-11-10)
Ein unabhängiger Reviewer-Agent (`/branch-code-review`), drei Durchgänge. Entscheidung des Nutzers: „Fix all“, im zweiten
Durchgang „Yes, all three“.
1. **M Der Verdrängt-Hinweis erschien auch bei Statements, die nie einen Plan haben** (PL/SQL-Block wie `SaveChanges`
   von EF, `CALL`, DDL) und versprach, der Plan komme wieder. **Behoben:** `PLAN_HASH_VALUE` unterscheidet die Fälle
   (gegen Oracle 23 gemessen, siehe Entscheidungen), eigener Text, Test für einen Block, Drop-Test prüft den Hash.
2. **K `ReadAsync` fing den Cursor nicht ab, der zwischen `CursorFigures` und `LatestChild` verschwindet**
   (`PlanUnavailableException`). **Behoben:** wird wie „nicht gefunden“ behandelt, ein Timeout nennt die letzte
   Ablehnung. Ein Test ohne den Fix ist nicht deterministisch zu bauen (Wettlauf).
3. **K `DropAsync` scheiterte mit ORA-01403, wenn der Cursor schon ganz verdrängt war** (Verdacht, nicht nachgestellt;
   der Weg ist eindeutig). **Behoben:** `DropAsync` meldet, ob es traf, der Test lädt den Cursor sonst neu.
4. **K Ein Satz im Protokoll war verstümmelt** (zwei Klammern). **Behoben.**
5. **K Gerader Apostroph im englischen Text.** **Behoben.**
6. **K (zweiter Durchgang) Protokoll, Index und CHANGELOG kannten den dritten Fall und den Block-Test nicht.** **Behoben**
   (dieser Nachtrag, Entscheidungen, Index-Zeile, CHANGELOG Internal).
7. **K (zweiter Durchgang) Der Block-Test hatte dieselbe Lücke wie Punkt 2.** **Behoben:** `CursorPlans.FindAsync`, die
   `ReadAsync` mitnutzt.
8. **K (zweiter Durchgang) Der Text zählte Kategorien auf, die nicht passen** (`ALTER SESSION` fehlte, CTAS und
   `CREATE INDEX` haben einen Plan). **Behoben:** Satz über das Statement, Kategorien als Beispiele.
- Learnings: Punkt 1 steht als Falle in `.claude/rules/oracle.md` (leerer Plan heißt nicht immer verdrängt). Die übrigen
  waren Einzelfälle der neuen Testhilfe bzw. Text und Doku, keine neue Regel.
