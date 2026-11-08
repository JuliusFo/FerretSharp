# ADR 0047: Daten-Diff über Flashback in einer eigenen Lese-Session

- Status: akzeptiert
- Datum: 2026-11-08
- Paket: WP-44 (`docs/work-packages/wp-44-data-diff.md`)

## Kontext

Der Nutzer will sehen, was sich in der Datenbank seit einem Zeitpunkt geändert hat – durch seine EF-Core-App, einen Job, einen Kollegen –, ohne vorher Stände wegzukopieren. Die Änderungsübersicht (WP-30) kennt nur die eigenen Änderungen eines Workspaces. Die Datenbank liegt hinter einem Kunden-VPN: ganze Tabellen zweimal über die Leitung zu holen ist teuer. WP-45 soll später die Tabellen eines App-Mitschnitts so vergleichen, WP-43 braucht ebenfalls eine Session, die unabhängig von den Workspaces liest.

## Entscheidung (des Nutzers, 2026-10-28 und 2026-11-08)

- **Flashback mit `MINUS` in der Datenbank**, kein Snapshot im Speicher (der bleibt im Backlog als Ausweichweg): „Vergleich starten“ merkt sich die SCN per `SELECT TIMESTAMP_TO_SCN(SYSTIMESTAMP) FROM dual` (braucht kein Recht auf `V$DATABASE`), „Vergleichen“ liest `jetzt MINUS AS OF SCN :start` und umgekehrt in einer Abfrage (`UNION ALL`, Seite als Spalte). Über die Leitung kommen nur Zeilen, die sich unterscheiden; .NET paart sie über den Schlüssel (PK, sonst ROWID) und vergleicht die Zellen.
- **Eine eigene Lese-Session je Verbindung** (`ReadSession`): Verglichen wird der committete Stand. Die Workspace-Session sähe nach einem Flush ihre eigenen Änderungen und hätte vielleicht gerade ein Statement offen. Die Session gibt nur den Leseweg heraus (`IDataAccess`), öffnet über `SessionOpening` bei Bedarf, schließt nach 5 Minuten ohne Lesen und mit der Verbindung, läuft im Keep-alive mit und meldet einen Verlust ans Banner der Verbindung. Auf schreibgeschützten Profilen läuft sie wie ein gesperrter Workspace in `SET TRANSACTION READ ONLY`.
- **Startpunkt je Workspace**, gespeichert mit dem Workspace (nur SCN, Zeit und Tabellen mit Filter – keine Werte), übersteht also einen Neustart. Ergebnisse nur im Speicher, auch Werte von Prod.
- Obergrenze 10.000 abweichende Zeilen je Tabelle, darüber abgeschnitten mit Hinweis. Prod erlaubt (nur lesend). Tabellen ohne PK über die ROWID, mit Hinweis bei Row Movement.

## Folgen

- Der Leseweg bleibt die einzige Tür: `AS OF` und `MINUS` entstehen im `QueryBuilder` (`BuildDiff`), die Lesesperre (`IsReadOnlyStatement`) lässt die Abfrage durch, `ReadOnlyTests` halten die Oberfläche von `IDataAccess` und `ReadSession` fest.
- `TIMESTAMP_TO_SCN` ist nur auf wenige Sekunden genau: Kurz vor dem Klick committete Änderungen können mit erscheinen (im Integrationstest gemessen; Alternative mit Recht: `V$DATABASE.CURRENT_SCN`).
- `MINUS` vergleicht keine LOB, LONG, XMLTYPE und Objekttypen: CLOBs werden wie im Grid über die ersten 200 Zeichen und die Länge verglichen, BLOBs über die Länge; LONG, XMLTYPE und Objekttypen werden mit Hinweis ausgelassen. Eine CLOB-Änderung hinter den ersten 200 Zeichen bei gleicher Länge bleibt unsichtbar.
- Grenzen der Datenbank: Recht `FLASHBACK` für Tabellen anderer Owner (ORA-01031), Reichweite des Undo (ORA-01555/08180/08181), DDL, die die Tabelle neu schreibt (`TRUNCATE`, ORA-01466) – `DataDiffErrors` erklärt sie. Eine seit dem Startpunkt hinzugefügte Spalte bricht den Vergleich in Oracle 23 nicht (sie liest sich damals als NULL).
- Jede offene Verbindung kann eine Session mehr halten (nur solange verglichen wird, plus 5 Minuten).
