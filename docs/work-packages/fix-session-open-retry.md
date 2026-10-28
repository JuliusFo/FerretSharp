# Fix: Hängendes Öffnen der Session neu versuchen (`fix/session-open-retry`)

Branch `fix/session-open-retry` (von `main`), 2026-10-28.

## Anlass
Meldung des Nutzers: „Opening the session“ läuft manchmal 20 s, bis es abbricht. Wunsch: nach 3 s aufgeben und neu versuchen, bis zu 5×. Erst evaluiert, dann umgesetzt.

Log des Nutzers (21.–28.10.):
- 16 von 20 gescheiterten Öffnen-Versuchen enden nach **19,4–21,4 s** mit „Netzwerksession: Unerwarteter Fehler beim Lesen des Pakets“ (ORA-12570), nur 4 mit ODP.NETs eigenem „Connection request timed out“ nach 15 s. `ConnectionTimeout` deckt den Hänger also nicht ab; ihn zu senken hilft nicht, `TRANSPORT_CONNECT_TIMEOUT`/`RETRY_COUNT` im Descriptor auch nicht (der TCP-Connect steht schon).
- Wer 1–2 s danach von Hand neu verband, hatte die Session nach 0,4–1 s.
- Gesundes Öffnen (38 Fälle): Median 0,64 s, p90 1,2 s, Maximum 2,4 s (Prod).

## Entscheidungen des Nutzers (2026-10-28)
- Statt fix 5 × 3 s **gestaffelte Grenzen 3, 3, 5, 8, 15 s** (34 s insgesamt): Bei fix 3 s käme eine langsame, aber gesunde Strecke (Prod 2,4 s) nie durch.
- Für **Explorer- und Workspace-Sessions**.

## Gebaut
- Core `SessionOpening` (Connections): öffnet in Versuchen mit den Grenzen `AttemptLimits`. Ein Versuch über seiner Grenze wird aufgegeben, eine Session, die danach doch aufgeht, geschlossen (das Warten mit `WaitAsync` kam aus `ActiveConnection`). Ein neuer Versuch nur nach abgelaufener Grenze – ein Fehler innerhalb der Grenze (abgelehnte Anmeldung, kein Listener) endet sofort, ein zweiter Login-Versuch zählte für `FAILED_LOGIN_ATTEMPTS`. Nach dem letzten `SessionOpenTimeoutException` („… in 5 Versuchen nicht öffnen (34 s insgesamt)“) → `ConnectionError.TimedOut`, das automatische Neuverbinden versucht es weiter.
- `ActiveConnection.ConnectAsync` öffnet immer so; jeder weitere Versuch ist ein eigener Schritt „Öffne die Session (Versuch 2 von 5)“ mit Dauer im Log, `SessionOpenEnded` meldet jeden Versuch. Der Parameter `openTimeout` und `ReconnectPolicy.OpenTimeout` (30 s nur beim automatischen Neuverbinden) entfallen.
- `WorkspaceSessions` öffnet ebenso (Uhr vom `WorkspaceManager`); ein Trennen während des Öffnens kehrt jetzt sofort zurück statt auf ODP.NET zu warten.

## Offen
- Nicht abgedeckt: Schema-Vergleich (`SchemaCompareLoader`) und „Verbindung testen“ (`OracleConnectionTester`) öffnen weiter mit einem Versuch.
- Nicht gegen das echte VPN geprüft (der Hänger lässt sich nicht auslösen) – im Log nach dem nächsten Hänger auf „Opening the session (attempt 2 of 5)“ achten.
