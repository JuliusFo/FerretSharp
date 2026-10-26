# Fixes: Audit-Zeile gruppiert, BYTE-Spalten für CHAR-Werte

Fund des Nutzers (2026-10-26): Bei Tabellen, deren Audit-Tabelle lange nicht abgeglichen wurde, füllte die Audit-Zeile über den Spalten einen halben Bildschirm – je Spalte ein ganzer Satz mit demselben Rat. Branch `fix/audit-status-grouped`.

## Fehlalarm CHAR → BYTE
- Die gemeldeten Spalten hatten in der Audit-Tabelle `VARCHAR2(n)` (BYTE) mit genau dem Vierfachen der Zeichenlänge der Tabelle (`VARCHAR2(n/4 CHAR)`) – eine für AL32UTF8 bemessene Audit-Tabelle. `OracleTypes.ChangeOf` stuft CHAR → BYTE pauschal als `MayFailOnValues` ein (für `MODIFY` richtig), `FitOf` machte daraus „nimmt keinen Wert auf“, und die Meldung sagte „der Trigger ließe jede DML scheitern“.
- `FitOf`: Eine BYTE-Spalte mit mindestens `MaxBytesPerCharacter` (4, AL32UTF8 – mehr braucht kein Datenbank-Zeichensatz) Byte je Zeichen nimmt jeden Wert einer CHAR-Spalte vollständig auf. Kürzerer Text ist die neue Stufe `ValueFit.Partial`: Kurze Werte gehen hinein, ein längerer scheitert. Der Zeichensatz der Datenbank wird nicht gelesen – mit 4 als Obergrenze stimmt die Aussage „passt“ überall; bei UTF8 (3) oder Einzelbyte-Zeichensätzen meldet FerretSharp dafür manchmal „zu kurz“, wo es passt.
- Der Planer meldet `Partial` als eigenes Problem (`PlanTypeTooShort[Template]`): „ist kürzer, als die Werte sein können: Ein längerer Wert ließe seine DML scheitern“ statt „jede DML“. Es bleibt ein Problem (der Abgleich läuft nicht), damit der Status nie „aktuell“ sagt, solange DML scheitern kann (R5-Review).
- `ChangeOf` ist unverändert (gemessen gegen Oracle 23, WP-23); die neue Regel gilt nur für das INSERT des Triggers.

## Gruppierte Audit-Zeile
- Core: Der Planer liefert die Typprüfung der Audit-Spalten als Daten (`AuditColumnIssue`: Spalte, `AuditColumnFit`, aus der Vorlage?, beide Typen), `AuditPlanner.ColumnIssues` für den Status; die Sätze im Designer entstehen daraus (`AuditColumnIssue.Message`) und sind unverändert. `AuditStatus` trägt fehlende Spalten (`MissingColumns`) und Spalten, die nicht jeden Wert aufnehmen (`ColumnIssues`, nur Probleme), getrennt von den kurzen Gründen; deren Sätze kommen nicht ein zweites Mal in `Reasons`.
- UI (`AuditStatusSummary`, `AuditStatusLine`): Die Zeile bleibt einzeilig – „veraltet: 7 Audit-Spalten können nicht jeden Wert aufnehmen · Trigger … füllt nicht alle Spalten · Details · Abgleichen“. Bis zu drei fehlende Spalten werden genannt (mit „fehlt“/„fehlen“), mehr gezählt. „Details“ klappt eine Tabelle auf (Name, Typ in der Audit-Tabelle, Typ der Werte mit „Vorlage“, Folge) und darunter einmal den Rat; bei mehr als drei fehlenden Spalten auch deren Liste. Der Tooltip „unvollständig protokolliert“ hängt nur noch an Kopf und Gründen, nicht an den Details.
- Der Rat sagt jetzt auch, dass „Abgleichen“ erst nach der Änderung von Hand geht – vorher öffnete der Link einen Designer, der den Abgleich verweigerte.

## E2E
- Eigener Container `ferret-e2e-audit` (Port 1531, danach entfernt), Scratch-Datenordner, eingebaute Vorlage. Eine Tabelle mit fünf fehlenden Spalten, einer zu kurzen und einer typfremden Audit-Spalte zeigt die gruppierte Zeile und die Details; eine Audit-Tabelle mit 4 Byte je Zeichen meldet nur den fehlenden Trigger, „Abgleichen“ legt ihn an (VALID), danach „aktuell“. Ein Wert aus zehn 4-Byte-Zeichen (`LENGTHB` 40) geht über den Trigger in `VARCHAR2(40)`. Log ohne `[ERR]`.
- „Details“ per DOM-Klick geprüft, nicht mit Windows-Eingabe (ein gewöhnlicher `@onclick`-Knopf).

## Offen
- Könnte „Abgleichen“ eine zu kurze Text-Spalte selbst verbreitern (z. B. `VARCHAR2(30)` gegen `VARCHAR2(10 CHAR)` → `VARCHAR2(30 CHAR)`, per `MODIFY` mit Daten frei)? Wäre „Typen werden nur größer“ (Entscheidung 2026-10-12), ändert aber DDL an bestehenden Audit-Tabellen – Frage an den Nutzer.
