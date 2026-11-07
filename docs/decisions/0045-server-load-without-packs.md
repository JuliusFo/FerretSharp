# ADR 0045: Serverlast und Top-Statements ohne Management Packs

- Status: akzeptiert
- Datum: 2026-11-07
- Paket: WP-75 (`docs/work-packages/wp-75-server-load.md`)

## Kontext

Der Nutzer will sehen, wie ausgelastet der Datenbankserver ist und welche Statements ihn belasten – wie die Top-SQL-Berichte von SQL Developer (CPU-Zeit und Häufigkeit). Oracle bietet dafür vieles, das meiste davon gehört aber zum Diagnostics oder Tuning Pack: ASH (`V$ACTIVE_SESSION_HISTORY`), AWR (`DBA_HIST_*`), ADDM, der SQL-Monitor (`V$SQL_MONITOR`). Laut *Database Licensing Information User Manual 19c* (Tabelle 1-15) kostet schon das Abfragen, „whether through … command-line APIs, or direct access to the underlying data“. Die Datenbank selbst hält davon nicht ab: `CONTROL_MANAGEMENT_PACK_ACCESS` steht bei Enterprise Edition standardmäßig auf `DIAGNOSTIC+TUNING`, auch ohne Lizenz, und ob eine Lizenz da ist, kann nur der DBA sagen. Beim Nutzer läuft die Datenbank über ein Kunden-VPN (Roundtrips teuer), der Benutzer hat `SELECT_CATALOG_ROLE`.

Die Werte in `V$SQL`/`V$SQLAREA` zählen seit dem Laden eines Cursors in den Shared Pool. Ein Statement, das seit Wochen dort liegt, steht damit vor einem, das gerade den Server belastet.

## Entscheidung

- **Nur Views außerhalb der Packs** (Entscheidung des Nutzers, 2026-11-07): `V$OSSTAT`, `V$SYS_TIME_MODEL`, `V$SYSSTAT`, `V$SYSTEM_WAIT_CLASS`, `V$INSTANCE`, `GV$INSTANCE` (nur zum Zählen), `V$SQLSTATS`, `V$SQL`, `V$SESSION`. Auch die Metrik-Views (`V$SYSMETRIC*`, `V$METRIC*`) bleiben draußen: Sie stehen nicht in der Liste, aber „Metric and Alert/Event history“ ist ein Pack-Feature (Grauzone, ebenfalls Entscheidung des Nutzers). `PackViewTests` durchsucht den Code nach den Views und Paketen der Packs und dem Hint `MONITOR`, auch in Kommentaren.
- **Zwei Sichten auf die Top-Statements:**
  - *Zeitfenster:* was die Statements taten, während die Seite zuschaute. Beim ersten Öffnen liest FerretSharp einmal die Werte aller Cursor aus `V$SQLSTATS` (nur SQL_ID, Plan-Hash, Container und Zahlen) als **Grundstand**; danach je Blick nur die Cursor mit `LAST_ACTIVE_TIME` seit dem Blick davor, die Differenzen rechnet .NET (`ServerLoadTracker`). Ein Cursor, der nicht im Grundstand ist oder kleinere Werte hat, wurde seitdem (neu) geladen und zählt ab 0. Ohne Grundstand würde ein einzelner teurer Lauf (ein Batch) seine eigenen Werte zum Startpunkt machen und nie erscheinen. Text, Modul und Parsing-Schema kommen einmal je SQL_ID aus `V$SQL` (höchstens 500 je Blick, die teuersten zuerst).
  - *Seit Laden:* wie SQL Developer die Werte seit dem Laden, aber aus `V$SQLSTATS` (je SQL_ID über die Pläne summiert, Modul und Schema per Join aus `V$SQL`), auf dem Server sortiert, gefiltert und auf 50 geschnitten – nur beim Umschalten und auf „Aktualisieren“, nicht alle 30 s. Geplant war `V$SQLAREA`; im E2E fehlte dort das teuerste Statement eines gerade beendeten Batchs, weil sein Cursor schon aus dem Library Cache verdrängt war – `V$SQLSTATS` behält die Werte länger (so dokumentiert). Ein Statement, das nicht mehr in `V$SQL` steht, hat kein Modul und Schema und fällt aus einem gesetzten Filter. Text kommt immer aus `V$SQLSTATS`.
- **Die Last als Mittel zwischen zwei Blicken:** Host-CPU aus `BUSY_TIME`/`IDLE_TIME`, durchschnittlich aktive Sessions = Δ DB time / Δ Zeit (gegen die Zahl der CPUs), Anteil auf CPU, Ausführungen, Commits und Logical Reads pro Sekunde, wartende Sessions je Warteklasse (Vordergrund). Ein Neustart der Instanz (`STARTUP_TIME`, kleinere Zähler) bringt einen neuen Grundstand. Die Zeit ist die des Servers (`SYSTIMESTAMP` in seiner Zone, dieselbe wie `LAST_ACTIVE_TIME`), nicht die des Clients – die Latenz des VPN verfälscht so die Mittel nicht.
- **Eigene Session, nur während die Seite gezeigt wird** (`ServerLoadService`, ACTION „Serverlast“): Die Explorer-Session arbeitet einen Aufruf nach dem anderen ab, ein Blick würde Schema-Aufrufe aufhalten. Alle 30 s ein Blick (Zähler, Warteklassen, geänderte Cursor, fehlende Texte, aktive Sessions – fünf kleine Roundtrips), anhalten und „Aktualisieren“ möglich. Verlässt man die Seite, schließt die Session; was gesehen wurde (Verlauf einer Stunde, Zeitfenster, „seit Öffnen“), bleibt je Verbindung, solange die App läuft. Ein Blick nach einer Lücke zählt zu „seit Öffnen“, aber zu keinem Zeitfenster, in dem er nicht ganz liegt, und das Diagramm zeichnet ihn nicht.
- **SQL-Texte fremder Sessions** bleiben im Speicher: nicht ins Log (nur Fehler der Blicke ohne Statement-Text), nicht in den SQL-Verlauf. Die Seite ist auf Prod erlaubt (nur lesend, über den Leseweg).

## Folgen

- Ohne Pack gibt es keinen Verlauf von vor dem Öffnen der Seite und keine Antwort auf „wer hat gestern um 14 Uhr was getan“. Das bleibt eine Option für Verbindungen mit bestätigter Lizenz (Backlog).
- Der Grundstand kostet beim ersten Öffnen einige Sekunden über das VPN (grob 5 MB bei 50 000 Cursors); danach kommen nur geänderte Cursor.
- Ein Statement, das zwischen zwei Blicken geladen und wieder verdrängt wurde, fehlt im Zeitfenster.
- In einer PDB gelten Statements und Sessions für die PDB, die Host-CPU für den ganzen Host; auf RAC zeigt die Seite nur die Instanz ihrer Session (Hinweise auf der Seite).
- Rechte: `SELECT_CATALOG_ROLE` oder Grants auf die Views; ohne sie eine Meldung mit dem `GRANT`.
