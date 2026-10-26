# Fixes: Abbruch in `Fire` als „Unerwarteter Fehler“ → Release offen

Fund des Nutzers (2026-10-26): Beim Laden einer Tabelle über das Kunden-VPN kam ein Fehlerdialog, im Log `[ERR] Unhandled exception on UI thread` mit einer `TaskCanceledException` aus `FerretComponent.GuardAsync`, 0,2 s später ORA-12570 auf derselben Session. Branch `fix/guard-cancel`.

## Ursache
- `GuardAsync` ließ eine `OperationCanceledException` durch (`when (ex is not OperationCanceledException)`), gemeint war „Abbruch bleibt still“. `Fire` ruft `InvokeAsync(() => GuardAsync(work))` ohne Warten auf; der `WpfDispatcher` der BlazorWebView fängt in `InvokeAsync` jede Ausnahme und wirft sie per `BeginInvoke` auf dem WPF-Dispatcher neu – `App.OnDispatcherUnhandledException` loggt sie und zeigt den Fehlerdialog. Jeder Abbruch, der in per `Fire` gestarteter Arbeit ankam, wurde so zum „Unerwarteten Fehler“.
- Welcher der `Fire`-Aufrufe es diesmal war, ist nicht festgestellt; die DB-Aufrufe (`RunDbAsync`) und die Wartezeiten (`PageLoads`, `FkCounts`, `IncomingCounts`) fangen ihren Abbruch selbst. Der Fix greift für alle.

## Fix
- Beide `GuardAsync` fangen `OperationCanceledException` selbst: ohne Ergebnis still, mit Ergebnis der Fehlschlag-Wert (für JS wie ein gescheiterter Aufruf – vorher wurde die Promise verworfen).

## Nicht behoben
- Der Verbindungsverlust selbst: Der Abbruch des Ladens schickte einen Break, und die Session riss am Break ab (`last break … during this command`, endete mit ORA-12570 statt ORA-01013). Das ist der Backlog-Eintrag „Abbrechen über VPN“, jetzt zum zweiten Mal beobachtet.
- Kein E2E: Der Abbruch eines Ladens ohne VPN führt nicht in diesen Weg, nur per Unit-Test geprüft.
