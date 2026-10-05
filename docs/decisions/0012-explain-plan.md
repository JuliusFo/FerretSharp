# ADR 0012: Explain-Plan – geschätzt auf der Explorer-Session, tatsächlich aus dem Cursor-Cache

- Status: akzeptiert
- Datum: 2026-10-05

## Kontext

WP-14: der Ausführungsplan für die Grid-Abfrage und für Kommandos der LINQ-Konsole, wahlweise geschätzt (ohne Ausführung) oder tatsächlich (mit Laufzeitzahlen). FerretSharp schreibt nur über bewusste Wege (CLAUDE.md, Abschnitt 2); gesperrte Workspaces laufen in einer READ-ONLY-Transaktion.

Spike im Container (Oracle 23 Free):

- `EXPLAIN PLAN SET STATEMENT_ID = '…' FOR <SELECT mit :binds>` geht ohne Bind-Werte; die Platzhalter gelten als Text (`TO_NUMBER(:P_0)`), der Plan kann deshalb vom tatsächlichen abweichen (im Spike: geschätzt Index Range Scan, tatsächlich mit `:p_0 = 10` Full Table Scan).
- In einer READ-ONLY-Transaktion scheitert `EXPLAIN PLAN` (ORA-00604, darunter ORA-01456): Es schreibt in die `PLAN_TABLE` (öffentliches Synonym auf eine globale temporäre Tabelle, sitzungslokal).
- Die `SQL_ID` lässt sich im Client berechnen (die letzten 8 Byte von MD5(Text + NUL), Base 32) und stimmt mit `V$SQL` überein.
- `V$SQL`/`V$SQL_PLAN_STATISTICS_ALL` brauchen Leserechte (`SELECT_CATALOG_ROLE` oder Grants auf `V_$…`); ohne sie ORA-00942.

## Entscheidung

- **Geschätzt:** `EXPLAIN PLAN` immer auf der **Explorer-Session** (ohne Transaktion, auch bei Prod – Entscheidung des Nutzers: es schreibt nur in die sitzungslokale `PLAN_TABLE`). Ein eigener, enger Weg `OracleSession.ExplainPlanAsync`: nimmt nur ein Statement, das die Lesesperre passiert, setzt `EXPLAIN PLAN` mit einer selbst erzeugten Statement-ID davor, liest die Zeilen und löscht sie wieder. Die Lesesperre bleibt unverändert. Der Dialog weist darauf hin, dass Bind-Werte fehlen.
- **Tatsächlich:** auf der **Workspace-Session** (also im Snapshot bzw. der Transaktion des Workspaces) wird die Abfrage mit `/*+ GATHER_PLAN_STATISTICS */` erneut ausgeführt – erste Seite (500 Zeilen) oder, im Dialog wählbar, das ganze Ergebnis (verworfen, abbrechbar). Danach liest FerretSharp den Plan des Cursors über die selbst berechnete `SQL_ID` aus `V$SQL_PLAN_STATISTICS_ALL` (reines Lesen). Keine Abhängigkeit von `V$SESSION.PREV_SQL_ID`, das Keep-alive-Pings verfälschen würden. Die Rechte werden vorher geprüft; fehlen sie, nennt der Dialog den Grant.
- **Ein Modell** (`ExecutionPlan`/`PlanStep`) für beide Quellen, mit Auffälligkeiten (Schätzung um Faktor ≥ 10 daneben, Full Table Scan) und einer Textform im Stil von `DBMS_XPLAN` zum Kopieren.
- Anzeige im Dialog (Entscheidung des Nutzers), aufrufbar an der Filterleiste und in der LINQ-Konsole.

## Konsequenzen

- Der tatsächliche Plan führt die Abfrage aus – Kosten wie im Grid bzw. für das ganze Ergebnis; schreibt nichts.
- Ohne V$-Rechte bleibt nur der geschätzte Plan.
- SQL-Text muss exakt so gehasht werden, wie er gesendet wird (keine Normalisierung von Leerzeichen).
