# Fix: Hängendes Öffnen der Session neu versuchen (`fix/session-open-retry`) → Release 3.54.0

Branch `fix/session-open-retry` (von `main`), 2026-10-28.

## Anlass
Meldung des Nutzers: „Opening the session“ läuft manchmal 20 s, bis es abbricht. Wunsch: nach 3 s aufgeben und neu versuchen, bis zu 5×. Erst evaluiert, dann umgesetzt.

Log des Nutzers (21.–28.10.):
- 16 von 20 gescheiterten Öffnen-Versuchen enden nach **19,4–21,4 s** mit „Netzwerksession: Unerwarteter Fehler beim Lesen des Pakets“ (ORA-12570), nur 4 mit ODP.NETs eigenem „Connection request timed out“ nach 15 s. `ConnectionTimeout` deckt den Hänger also nicht ab; ihn zu senken hilft nicht, `TRANSPORT_CONNECT_TIMEOUT`/`RETRY_COUNT` im Descriptor auch nicht (der TCP-Connect steht schon).
- Wer 1–2 s danach von Hand neu verband, hatte die Session nach 0,4–1 s.
- Gesundes Öffnen (38 Fälle): Median 0,64 s, p90 1,2 s, Maximum 2,4 s (Prod).

## Entscheidungen des Nutzers (2026-10-28)
- Statt fix 5 × 3 s **gestaffelte Grenzen 3, 3, 5, 8, 15 s** (34 s insgesamt): Bei fix 3 s käme eine langsame, aber gesunde Strecke (Prod 2,4 s) nie durch. Nach dem Code-Review: Start mit 5 s (5, 5, 8, 10, 15 s, 43 s), siehe Nachtrag.
- Für **Explorer- und Workspace-Sessions**.

## Gebaut
- Core `SessionOpening` (Connections): öffnet in Versuchen mit den Grenzen `AttemptLimits`. Ein Versuch über seiner Grenze wird aufgegeben, eine Session, die danach doch aufgeht, geschlossen (das Warten mit `WaitAsync` kam aus `ActiveConnection`). Ein neuer Versuch nur nach abgelaufener Grenze – ein Fehler innerhalb der Grenze (abgelehnte Anmeldung, kein Listener) endet sofort, ein zweiter Login-Versuch zählte für `FAILED_LOGIN_ATTEMPTS`. Nach dem letzten `SessionOpenTimeoutException` („… in 5 Versuchen nicht öffnen (34 s insgesamt)“) → `ConnectionError.TimedOut`, das automatische Neuverbinden versucht es weiter.
- `ActiveConnection.ConnectAsync` öffnet immer so; jeder weitere Versuch ist ein eigener Schritt „Öffne die Session (Versuch 2 von 5)“ mit Dauer im Log, `SessionOpenEnded` meldet jeden Versuch. Der Parameter `openTimeout` und `ReconnectPolicy.OpenTimeout` (30 s nur beim automatischen Neuverbinden) entfallen.
- `WorkspaceSessions` öffnet ebenso (Uhr vom `WorkspaceManager`); ein Trennen während des Öffnens kehrt jetzt sofort zurück statt auf ODP.NET zu warten.

## Offen
- Nicht abgedeckt: Schema-Vergleich (`SchemaCompareLoader`) und „Verbindung testen“ (`OracleConnectionTester`) öffnen weiter mit einem Versuch.
- Nicht gegen das echte VPN geprüft (der Hänger lässt sich nicht auslösen) – im Log nach dem nächsten Hänger auf „Opening the session (attempt 2 of 5)“ achten.

## Nachtrag: Code-Review (2026-10-28)
Unabhängiger Reviewer über `e35f6d16..d3fff838`, acht Befunde. Entscheidung des Nutzers: „Behebe alle und mach die Staffelung eventuell als Start mit 5s.“ → Grenzen 5, 5, 8, 10, 15 s (43 s); mit dem Rennen schadet eine längere erste Grenze einer langsamen Strecke nicht mehr und spart Logins.

- 1 (M) Später Login-Fehler wurde wiederholt (Oracle beantwortet Logins nach Fehlversuchen verzögert, jeder Versuch zählte für `FAILED_LOGIN_ATTEMPTS`): behoben. Die Versuche „rennen“ (`SessionOpening.Race`): Ein später Fehler eines früheren Versuchs beendet das Öffnen, außer es ist ein Netzfehler (`ReconnectPolicy.IsNetworkError`, jetzt public). Test.
- 2 (K) Eine knapp zu spät geöffnete Session wurde weggeworfen: behoben, die erste Session gewinnt, auch die eines Versuchs über seiner Grenze; die übrigen werden geschlossen. Test.
- 3 (K) Abbruch zwischen Grenze und nächstem Versuch startete noch einen Login: behoben (`ThrowIfCancellationRequested` am Schleifenanfang, `Task.Run` mit dem Token startet nach einem Abbruch nichts mehr; ein Gewinner, der in dem Moment feststeht, wird genommen). Das Fenster selbst ist nicht im Test nachgestellt.
- 4 (K) Log ordnete die Versuche nicht zu: behoben, `SessionOpenOutcome.Attempt`, `ConnectActivityLog` schreibt „(attempt n)“.
- 5 (K) Ursache ging verloren (VPN aus: früher ODP.NET-Fehler nach 15 s, dann nur „in 5 Versuchen nicht öffnen“): behoben, der letzte späte Netzfehler steht in der Meldung (`OpenTimedOutWithError`, „Zuletzt: …“). Länger dauert es weiterhin (43 s statt 15 s), wenn das Ziel stumm bleibt.
- 6 (K, Verdacht) Ein Connector, der vor der Rückgabe blockiert, hebelte die Grenze aus: im Log nie beobachtet (0 von 58 Öffnungen blockierend), trotzdem abgesichert – jeder Versuch startet per `Task.Run`. Test mit einem blockierenden Versuch.
- 7 (K) Testlücken: ergänzt – Abbrechen in Versuch 2 (`ActiveConnectionTests`), Trennen während eine Workspace-Session öffnet (`WorkspaceManagerTests`), später ORA-01017 und später Netzfehler (`SessionOpeningTests`). `CountingTimeProvider` liegt jetzt in `tests/Shared` (vorspulen erst, wenn der Timer des Versuchs steht).
- 8 (K) Doku: Schema-Vergleich und „Verbindung testen“ in `docs/backlog.md`, Hinweis in der Indexzeile von small-auto-reconnect.
- Vorher schon da: Der Task, der eine späte Session schließt, wurde verworfen (Fehler beim Schließen unbeobachtet) – mit dem Rennen behoben (`Race.CloseAsync` fängt). Eine Treiber-`OperationCanceledException` ohne Abbruch zeigt still „getrennt“ – in den Backlog.
- Falle beim Testen: Eine Auto-Property eines NSubstitute-Fakes (`session.Data`), die Test- und Pool-Thread zugleich zum ersten Mal lesen, liefert zwei verschiedene Instanzen – im Test vorher fest setzen.

Zweiter Durchgang des Reviewers über die Fixes, vier neue Befunde; Entscheidung des Nutzers: „Alle (A–C2)“.
- A (K) Eine Session, die genau beim Ende des Wartens (Grenze oder Abbruch) gewann, blieb offen (`Pooling=false`: echte Oracle-Session bis zum Ende der App); ein Fehler dort blieb unbeobachtet: behoben, `Race.End(taken)` schließt bzw. beobachtet. Test direkt am `Race` (jetzt `internal`).
- B (K, Designfrage) Ein schneller Netzfehler des jüngsten Versuchs beendete das Öffnen, obwohl ein früherer noch lief: behoben, er beendet erst, wenn kein Versuch mehr läuft (dann mit seinem Fehler); Nicht-Netzfehler beenden weiter sofort. Zwei Tests.
- C1 (K) Der Test der Staffelung hätte eine zu kurze dritte Grenze übersehen: prüft jetzt die Fristen der Timer (`CountingTimeProvider.DueTimes`).
- C2 (K) Der Test mit blockierendem Versuch hätte bei einem Rückfall den Lauf blockiert: läuft per `Task.Run`, wartet höchstens 10 s, gibt im `finally` frei.

Dritter Durchgang, ein neuer Befund; Entscheidung des Nutzers: „Ja, D beheben“.
- D (K) Seit B startete die abgelaufene Grenze eines schon gescheiterten jüngsten Versuchs doch den nächsten („Versuch 3 von 5“, während Versuch 1 noch hing) – gegen „kein neuer Versuch nach einem Fehler“: behoben, `Race.PastLimit()` meldet den Fehler, dann wartet `OpenAsync` nur noch auf die früheren Versuche (höchstens so lange, wie die übrigen Grenzen gedauert hätten). Zwei Tests.

Vierter Durchgang über D: ohne Befund. Offen als UX-Frage: Während nur noch auf frühere Versuche gewartet wird, bleibt „Öffne die Session (Versuch 2 von 5)“ stehen (im Extremfall bis zu 33 s) – inhaltlich richtig, ein eigener Hinweis nur, falls es stört.

## Nachtrag: E2E (2026-10-29)
Vor dem Merge, Stand mit `main` (3.53.0 + Audit-Fixes, Werteauswahl, Verwendung ohne Index): eigener Container `ferret-e2e-enum` (Port 1547), davor ein TCP-Proxy im Scratchpad (Port 1548), der je neuer Verbindung durchreicht, sie hängen lässt (angenommen und gelesen, nie beantwortet: der TCP-Connect steht, der Login hängt) oder nach dem ersten Paket kappt. Build in den Scratchpad, eigene Instanz mit `--data-dir`, eigenem WebView2-Ordner, Debug-Port, `--lang=en`, Passwort ohne Speichern. Bedienung per CDP, Fortschritt alle 250 ms ausgelesen:
- Erster Versuch hängt: nach 5 s „attempt 2 of 5“, Session nach 115 ms offen, verbunden nach 5,7 s. Log: „session open after 115 ms (attempt 2)“, später „ended without one after 15064 ms (attempt 1): Connection request timed out“.
- Zwei Versuche hängen: Fortschritt und Statusleiste zeigen „Opening the session (attempt 2 of 5)“ mit eigener Dauer, Versuch 3 geht durch (10 s).
- Workspace-Session (Tabelle in einem neuen Workspace öffnen, zwei Versuche hängen): dritter Versuch nach 10 s, bis dahin „Loading rows“ im Tab – ohne Angabe des Versuchs (die Schritte zeigt nur das Verbinden).
- Alle Versuche hängen: Versuche bei 0, 5, 10, 18 und 28 s, „Connection failed“ nach 43,0 s mit „Opening the session did not finish in 5 attempts (43 s in all). Last error: Connection request timed out“ und „Try again“.
- Abbrechen in Versuch 3: sofort „Not connected“, kein weiterer Versuch; die drei offenen enden 15 s nach ihrem Start mit „Connection request timed out“ im Log.
- Verbindung sofort gekappt (Netzfehler innerhalb der Grenze): sofort ORA-50201, genau ein Verbindungsaufbau.
- Falsches Passwort: ORA-01017 nach 1,2 s, genau ein Verbindungsaufbau, kein zweiter Login.
- Log ohne `[ERR]`/`[WRN]`.
- Beobachtet, nicht geändert: Die Sockets aufgegebener Versuche blieben offen, bis die App beendet wurde – auch nach „Connection request timed out“ (der Proxy sah das Schließen erst beim Beenden). `OracleSession.OpenAsync` gibt die Connection im Fehlerfall frei; das Socket hält ODP.NET selbst, solange die Gegenseite stumm bleibt. Über das echte VPN endet der Hänger laut Log mit ORA-12570 (Gegenseite trennt), dort sollte nichts übrig bleiben. Ebenfalls vorher schon da: Die Dauer im Fortschritt zeigt kurz vor einer Sekunde „1000 ms“ statt „1,0 s“.
