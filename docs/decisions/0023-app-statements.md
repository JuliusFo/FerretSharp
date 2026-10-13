# ADR 0023: Statements der laufenden App – EventPipe von außen, feste Feldliste, eigenes Projekt

- Status: akzeptiert
- Datum: 2026-10-13
- Paket: WP-33 (Issue #12)

## Kontext

Beim Debuggen einer App mit EF Core sieht man nicht, welche Statements sie wirklich an die Datenbank schickt – wie viele eine Anfrage auslöst, wie viel davon Lazy Loading ist (N+1), welche langsam sind und welchen Plan sie haben –, ohne Code zu ändern (Logging, Interceptor). Gewünscht (Idee des Nutzers, 2026-10-12): eine Übersicht nach Herkunft mit Anzahl und Zeitanteil, die häufigsten und teuersten Statements, ohne VS-Extension.

Der naheliegende Weg über die Datenbank (`V$SQL`, `V$SESSION`) reicht nicht: `V$SQL` zählt je Cursor für die ganze Instanz, nicht je Session, und ob ein Statement aus Lazy Loading, LINQ oder Raw SQL stammt, steht nicht im SQL-Text. Genauer ginge es nur über Session-Traces (Dateien auf dem Server) oder ASH (Diagnostics-Pack-Lizenz).

Entscheidungen des Nutzers (2026-10-13, `docs/roadmap.md`): eigene Seite wie Einstellungen und Schema-Vergleich; Prozesse als Liste auf Klick; Umgebung aus der Kommandozeile über den Schlüssel `environment`; N+1 ab 10 gleichen Statements in 2 s, für Lazy Loading und Schleifen.

## Entscheidung

**Mitschnitt von außen über EventPipe.** EF Core meldet jedes Command an einen `DiagnosticListener`. Die Runtime jeder .NET-App (ab Core 3.0) öffnet einen Diagnose-Port; `Microsoft.Diagnostics.NETCore.Client` startet darüber eine EventPipe-Session mit dem Provider `Microsoft-Diagnostics-DiagnosticSource`, der die Events dieses Listeners mit den angeforderten Feldern serialisiert; `TraceEvent` liest den Strom. Die App braucht keine Änderung; der Port bedient nur Prozesse desselben Benutzers auf demselben Rechner. Belegt im Spike (2026-10-12) und in den Integrationstests: 9.000 Abfragen ohne Verlust und ohne messbaren Unterschied je Abfrage; ein angehaltener Prozess (wie am Breakpoint) verliert nichts.

**Feste Feldliste** (`EfEvents.PayloadSpecs`, Core): `CommandExecuted`/`CommandError` mit SQL-Text, `CommandSource`, `ExecuteMethod`, `Result`, Dauer, ContextId, CommandId, `DataSource` (und die Fehlermeldung), `DataReaderDisposing` mit `ReadCount` und Dauer, `NavigationLazyLoading` mit Navigation und ContextId. **Nie** der Connection-String (ODP.NET entfernt das Passwort nach dem Öffnen nur ohne `Persist Security Info=true`), nie Parameter (über diesen Weg ohnehin nicht lesbar), nie die Entity selbst (ihr `ToString` kann Werte ausgeben). `Result` wird nur bei `ExecuteNonQuery` als Zeilenzahl gelesen. Ein Test prüft die Liste.

**Auswertung** (`StatementRecording`, Core): Herkunft aus `CommandSource` (ab EF Core 7; `BulkUpdate` = `ExecuteUpdate`/`ExecuteDelete`, `FromSqlQuery` nur ohne LINQ darauf); Lazy Loading, weil EF `NavigationLazyLoading` direkt vor dem Command im selben Context meldet. Zeit = Ausführung (`CommandExecuted` endet, wenn der Reader zurückkommt) plus Lesezeit aus `DataReaderDisposing`; Zeilen = `ReadCount` − 1 (der letzte `Read()` zählt mit). Summen je Herkunft und je SQL-Text, höchstens 2.000 verschiedene Statements. **N+1 je DbContext:** Die Regel des Nutzers (10 in 2 s) zählt die Läufe eines Statements in einem Context (einer Anfrage). Abweichend vom Plan, beim E2E gefunden: Global gezählt bekam bei einer App unter Last oder einem Polling-Job jedes Statement das Abzeichen.

**Zuordnung zur Verbindung** (`AppConnectionMatch`): neues optionales Feld `ConnectionProfile.AppEnvironment`; erst über den Umgebungsnamen der Kommandozeile (`-environment=x`, `--environment x`, `/environment=x`, Groß-/Kleinschreibung egal), sonst über `DataSource` (EZConnect, Connect-Descriptor oder TNS-Alias gegen Host/Port/Service bzw. Alias) – nur wenn genau eine passt, sonst wählt der Nutzer und kann die Umgebung an der Verbindung merken. Umgebungsvariablen (`ASPNETCORE_ENVIRONMENT`) sind von außen nicht lesbar.

**Nachsehen in der Datenbank** (`ISchemaReader.GetCursorAsync`): `V$SQL` per `SQL_ID`, die FerretSharp aus dem exakten Text berechnet (ADR 0012), summiert über alle Children, und der Plan des zuletzt benutzten Child aus `V$SQL_PLAN_STATISTICS_ALL` – auf der Explorer-Session, reines SELECT (Lesesperre, `ReadOnlyTests`). Ohne Rechte ein Hinweis mit dem Grant. Die UI sagt, dass `V$SQL` instanzweit zählt und gemessene Zahlen je Schritt nur mit Statistik da sind.

**Eigenes Projekt `FerretSharp.AppTrace`** (net10.0, plattformneutral) für die EventPipe-Quelle (`EventPipeTraceSource`) und das Lesen der Kommandozeile (Windows `NtQueryInformationProcess`, Linux `/proc`; `DiagnosticsClient.GetProcessInfo` ist nicht öffentlich). Der Core kennt nur `IAppTraceSource` und die rohen Events, die App registriert die Quelle. Gründe: TraceEvent (rund 8 MB mit nativen Teilen) kommt nicht in Core und UI; die echten Prozess-Tests (eine Test-App mit EF Core 8 starten, anhängen, `V$SQL` prüfen – `tests/FerretSharp.FakeEfApp`) laufen auch in der Linux-CI, die `FerretSharp.App` (WPF) nicht baut.

**UI:** `AppTraceService` (Singleton, nicht je Verbindung – ein Mitschnitt läuft weiter, während der Nutzer woanders ist) meldet Events höchstens alle 250 ms (eine App schickt Tausende Statements je Sekunde), alles andere sofort. Seite `AppStatementsPage` wie der Schema-Vergleich. Build-Werkzeuge (MSBuild, Compiler-Server, dotnet-CLI) sind in der Prozessliste ausgeblendet.

## Konsequenzen

- Nur lokale Prozesse des eigenen Benutzers. Apps auf dem Server bräuchten `dotnet-monitor` o. Ä. (Backlog). Eine App mit `DOTNET_EnableDiagnostics=0` lässt sich nicht anhängen (klare Meldung).
- Man sieht, was ab dem Anhängen passiert. Eine hart beendete App (Debuggen beenden) bricht den Strom mitten im Block ab – das gilt als Ende, nicht als Fehler.
- SQL-Texte können Literale enthalten (Raw SQL mit eingesetzten Werten): Sie bleiben im Speicher, nie im Log.
- `V$SQL`-Zahlen enthalten andere Sessions, die dasselbe Statement ausführen.
- EF Core vor 7 hat kein `CommandSource`: Herkunft „Unbekannt“, Lazy Loading und Zeiten funktionieren. .NET Framework (kein EventPipe) geht nicht.
- Ein Projekt mehr in der Solution; CI baut es auf Linux mit, Coverage zählt es.
- Nicht geprüft: Anhängen an eine App unter dem echten VS-Debugger (im Spike nur `NtSuspendProcess`, strenger als ein Breakpoint), zwei Sessions auf denselben Prozess.
