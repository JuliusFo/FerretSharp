# Klein: Automatisch neu verbinden → Release offen

Branch `feat/auto-reconnect` (von `main`), 2026-10-21.

## Anlass
Frage des Nutzers: optional automatisch neu verbinden, wenn die Verbindung wegbricht (VPN, Ruhezustand). Bis dahin erkannte FerretSharp den Verlust (Statement oder Keep-alive), zeigte das Banner und verband erst über „Neu verbinden“ neu – das sprang zur Verbindung und in den Explorer.

Entscheidungen des Nutzers (Vorschlag übernommen, 2026-10-21):
- Einstellung „Automatisch neu verbinden“ unter Einstellungen › Verbindung, **global** (nicht je Verbindung), **standardmäßig aus**.
- Nur, wenn dabei nichts verloren geht: Hatte ein Workspace der Verbindung ausstehende oder geschriebene Änderungen (oder eine offene Schreib-Transaktion), bleibt das Banner wie bisher – der Verlust soll quittiert werden, nicht in einem Toast verschwinden. Ausstehende, noch nicht geschriebene Änderungen werden **nicht** über das Neuverbinden gerettet (`editing.Forget` verwirft sie heute; für eine erste Version zu viel).
- Ohne Sprung: Die angezeigte Verbindung bleibt auf ihrer Seite, eine im Hintergrund bleibt im Hintergrund und wird trotzdem neu verbunden.
- Versuche mit wachsenden Pausen (sofort, 5 s, 15 s, 30 s, dann jede Minute) bis 10 min nach dem Verlust; danach der Fehler wie bei einem gescheiterten Verbinden. **Nie** nach einer abgelehnten Anmeldung.
- Statements werden nicht wiederholt.
- Der Ruhezustand-Trigger kommt direkt mit (nicht als eigener Punkt danach).

## Gebaut
- Core: `ReconnectPolicy` (Pausen, Aufgeben, welche Fehler einen neuen Versuch lohnen: TNS ORA-12150…12699, ORA-50000 von ODP.NET, ORA-01033/01034/01089/01090, die Verlust-Codes, ein abgelaufenes Öffnen – alles andere beendet die Versuche, auch ein Fehler ohne Code wie „kein Passwort gespeichert“). `ActiveConnection.ConnectAsync(…, openTimeout)`: Das Öffnen der Session gibt nach der Grenze auf (`SessionOpenTimeoutException` → `ConnectionError.TimedOut`), eine danach doch geöffnete Session wird geschlossen; das Laden des Schemas hat keine Grenze. Hintergrund: Nach dem Ruhezustand hing das Öffnen über die 15 s von ODP.NET hinaus (`fixes-2026-10-21.md`), ein unbeobachteter Versuch hätte sonst für immer „Verbinde …“ gezeigt.
- `ConnectionKeepAlive.CheckSoon`: eine Runde 5 s nach dem Aufwachen (der App-Host hört auf `SystemEvents.PowerModeChanged` und loggt jeden Wechsel). Respektiert den Schalter des Keep-alive.
- UI State `AutoReconnect` (je Verbindung ein `ReconnectRun`: `Holding` / `Connecting` / `Waiting`): Die Shell ruft `Check` bei jeder Änderung einer Verbindung; ein Versuch ist `WorkspaceLifecycle.ReconnectInPlaceAsync` (wie „Neu verbinden“ ohne `Show`/`ShowExplorer`, mit Zeitgrenze, endet mit dem Verbinden). Läuft auf einer Session noch Schreiben, Commit oder ein Export (`HoldsSession`, `BackgroundReads.ReadingIn`), wartet er (alle 2 s neu): Neuverbinden schließt alle Sessions der Verbindung, auch eine, die noch geht. Hat in der Zwischenzeit jemand von Hand verbunden, getrennt oder neu verbunden, endet der Lauf still.
- Anzeige: Statt „Verbindung fehlgeschlagen“ während der Pause `ReconnectWaiting` (Fehler, Versuch n, Countdown, „Jetzt versuchen“, „Nicht mehr versuchen“, „Bearbeiten“), Statusleiste „Verbindung verloren – nächster Versuch in 0:12“, Umschalter „verbindet neu“. Das Banner sagt beim Warten auf laufende Arbeit „Verbindet sich von selbst neu, sobald …“ und bei verlorener Arbeit, warum es nicht von selbst neu verband. Nach Erfolg ein Hinweis, mit den wieder gesperrten Workspaces (Freischalten wird nie gespeichert, ADR 0008); nach dem Aufgeben einer mit dem Fehler.

## Offen / Fragen
- Ein Lesestatement auf einer anderen, noch lebenden Session der Verbindung (Grid-Seite, SQL-Abfrage) zählt nicht als laufende Arbeit und wird beim Neuverbinden abgebrochen. Selten (meist sind nach einem Verlust alle Sessions weg); fällt es auf, die laufenden Lesezugriffe zählen.
