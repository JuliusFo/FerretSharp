# ADR 0027: Warten und Fortschritt laufender Statements – Kennung in CLIENT_INFO, Blick über die Explorer-Session

- Status: akzeptiert
- Datum: 2026-10-17
- Paket: WP-52

## Kontext

Ein Statement aus SQL-Editor, LINQ-Konsole oder Designer, das lange läuft, zeigt bisher nur eine Uhr. Über das Kunden-VPN ist offen, ob die Datenbank rechnet oder wartet – und worauf. Am häufigsten wartet ein schreibendes Statement auf eine Zeilensperre: ein `UPDATE` wartet in Oracle unbegrenzt (nur der Grid-Flush sperrt mit `FOR UPDATE WAIT n` und zeigt danach den Sperrkonflikt-Dialog), und mit mehreren Workspaces je Verbindung hält die Sperre oft ein eigener Workspace mit offener Transaktion. Lange Lesevorgänge (Full Scans, Hash-Joins, Sortierungen) meldet Oracle mit Fortschritt in `V$SESSION_LONGOPS`.

Aufgenommen vom Nutzer aus einer Ideensammlung (2026-10-17, `docs/roadmap.md`). Offen war, ob eine eigene Session oder die Explorer-Session fragt.

## Entscheidung

**Kennung je Session in `CLIENT_INFO`.** `OracleDatabaseConnector` gibt jeder Session beim Öffnen eine eindeutige Kennung (`OracleSessionDefaults.NewSessionTag`: `FerretSharp ` + 12 Hex-Zeichen, ASCII) als `CLIENT_INFO` (bisher leer); `IDatabaseConnection.SessionTag` nennt sie. ODP.NET schickt den Wert mit dem nächsten Aufruf mit – kein zusätzlicher Roundtrip, anders als `SYS_CONTEXT('USERENV', 'SID')` nach dem Öffnen. Die Kennung findet die Session in `V$SESSION`, und eine sperrende Session mit einer Kennung dieser Instanz ist eindeutig ein eigener Workspace (`ConnectionHub.FindSession`, auch in einer anderen offenen Verbindung). MODULE/ACTION (Workspace-Name) reichten dafür nicht: Kollegen haben Workspaces mit denselben Namen. `CLIENT_IDENTIFIER` bleibt frei, weil Datenbanken ihn für Audit oder VPD nutzen können.

**Ein Blick = eine Abfrage über die Explorer-Session** (`ISchemaReader.GetSessionActivityAsync`): die Session per Kennung, die blockierende Session (`BLOCKING_SESSION`, nur auf derselben RAC-Instanz) mit der Tabelle der Zeile (`ROW_WAIT_OBJ#` → `ALL_OBJECTS`) und der jüngste unfertige Eintrag aus `V$SESSION_LONGOPS` (`OUTER APPLY … FETCH FIRST 1 ROW ONLY`). Reines SELECT, die Lesesperre bleibt unverändert (`ReadOnlyTests`). Die Explorer-Session statt einer eigenen: kein weiterer Verbindungsaufbau über das VPN, kein eigener Keep-alive, Neuverbinden und Schließen wie bisher. Sie arbeitet einen Aufruf nach dem anderen ab; ein Blick wartet also hinter einem laufenden Schema-Aufruf – in der Praxis selten, die Anzeige kommt dann etwas später.

**Takt** (`SessionWatch`, UI): erst nach 3 s Laufzeit (kürzere Statements kosten nichts), danach 2 s nach jeder Antwort – nie mehrere Blicke gleichzeitig. Endet das Statement, endet das Schauen (ein laufender Blick wird abgebrochen).

**Ohne Rechte** (ORA-00942/01031 auf die V$-Views, bei Entwickler-Usern üblich): `SchemaCache` merkt sich das für die Verbindung und fragt nicht wieder (neu verbinden fragt neu). Ein schreibendes Statement nennt dann nach 3 s die anderen Workspaces der Verbindung mit nicht committeten Änderungen („Falls es auf eine Sperre wartet: …“) – reine Lesestatements warten in Oracle nie auf Sperren.

**Anzeige** in `RunProgress` (SQL-Editor, LINQ-Konsole, DDL-Entwürfe): „Wartet auf eine Sperre auf KUNDEN, gehalten von deinem Workspace „Bug 3711“ · 0:12“ mit „Workspace zeigen“ bzw. Session, User, Maschine, Programm bei fremden Sessions; Fortschrittsbalken mit Oracles Operation, Ziel, Prozent und geschätzter Restzeit. Der Sperrkonflikt-Dialog (`LockHolderList`) nennt eigene Workspaces über dieselbe Kennung.

## Konsequenzen

- `CLIENT_INFO` jeder FerretSharp-Session ist belegt; ein DBA sieht dort die Kennung. Sie enthält nichts über den Nutzer.
- Nur mit Leserechten auf `V$SESSION` und `V$SESSION_LONGOPS` (`SELECT_CATALOG_ROLE` oder einzelne Grants) gibt es die genaue Anzeige; sonst nur den Hinweis auf eigene offene Änderungen.
- RAC: `V$` zeigt die eigene Instanz. Läuft die Workspace-Session auf einer anderen Instanz als die Explorer-Session, findet der Blick sie nicht und zeigt nichts; eine Sperre von einer anderen Instanz erscheint nur mit SID. `GV$` bräuchte andere Grants – erst bei Bedarf.
- Grid-Flush (hat `FOR UPDATE WAIT n` und den Sperrkonflikt-Dialog), COUNT und FK-Counts zeigen noch nichts (Backlog).
- Je laufendem Statement ab 3 s alle 2 s ein Roundtrip auf der Explorer-Session.
