# Diagnose: UI-Hänger beim Commit über RDP (`fix/ui-hang-diagnostics`)
Meldung des Nutzers (2026-11-02, Arbeits-Laptop per RDP, kein Dock): Neue Zeilen in einer neuen Tabelle eingefügt und committet, das Fenster stand mit „(Keine Rückmeldung)“; nach dem Minimieren ging es weiter. Auftrag: Diagnose einbauen, kein Fix der Ursache.

## Log des Nutzers
- 10:12:27.584 `WebView frames held until the image was free: 400, longest 175562 ms; dropped after 1 s: 0`. Ein Bild wartete 175 s, wurde aber nicht verworfen: Der Timer von `WebViewCapture` (alle 4 ms, nach 1 s verwerfen) lief in dieser Zeit nie, der UI-Thread hing also von ca. 10:09:32 bis 10:12:27. „Keine Rückmeldung“ bestätigt das (kein verhungerter Timer).
- 150 ms danach „Rows 0+ … 194 ms“ (Neuladen nach dem Commit): alles stand dahinter. Der Neustart der Aufnahme („back from minimized“, 10:13:30) kam erst nach dem Hänger.
- Vorher viel Last auf dem Render-Thread: 873 zurückgehaltene Bilder in einer Minute, längstes 86 ms (beim Kollegen 134).

## Deutung (offen)
- Der Commit selbst blockiert nicht: `OracleTransaction.CommitAsync` ist in ODP.NET 23.26 echt async (per `ilspycmd` nachgelesen; nur das Schreiben ins Socket ist synchron, gelesen wird mit `ReadResponseAsync`). `WebViewCapture` blockiert nicht (`TryLock(0)`, Kopie höchstens 50 ms).
- Wahrscheinlich: Der UI-Thread wartete synchron auf WPFs Render-Thread – `SyncFlush` (wie am 21.10., `fixes-2026-10-21.md`) oder das unbegrenzte `D3DImage.Lock()` des WebView2-Controls bei einer Änderung der Bildschirmeinstellungen (ADR 0038). Über RDP ändern sie sich beim Verändern, Maximieren oder Neuverbinden des RDP-Fensters. Minimieren baut das Render-Ziel um und kann den Render-Thread wieder anstoßen.
- Belegt ist das nicht: Das Log hatte weder Display-Wechsel noch RDP-Ereignisse, „UI-Hänger protokollieren“ war aus, `dotnet-stack` ließ sich auf dem Laptop nicht installieren (Firmen-Feed in der globalen NuGet-Konfiguration; `--add-source https://api.nuget.org/v3/index.json --ignore-failed-sources`).

## Gebaut
- `UiHangWatch` (App), immer an: prüft jede Sekunde per Dispatcher-Aufruf (Priorität Normal, wie `UiStallMonitor`), ob der UI-Thread antwortet. Nach 5 s ohne Antwort eine Warnung mit Beginn („UI thread not responding for 5 s (since …)“) – noch während des Hängers, falls die App darin beendet wird –, danach eine mit der Dauer („responding again after X s“). Über einen Ruhezustand hinweg kann ein Hänger gemeldet werden, der keiner war; „Power mode Resume“ steht dann daneben. Entscheidung: eigene Klasse statt `UiStallMonitor` immer laufen zu lassen – der misst jede Dispatcher-Operation über Hooks und bleibt die Einstellung für die Details.
- `DisplayWatch` (App): beim Start „Display: remote session yes/no, N monitor(s), primary WxH, render tier T“; danach jede Änderung der Bildschirmeinstellungen („Display settings changing/changed“) und jeder Wechsel der Windows-Sitzung („Windows session RemoteConnect/SessionLock …“). Gestartet vor dem Hauptfenster: `SystemEvents` ruft die Handler in der Reihenfolge des Anmeldens, die Zeile steht also vor dem Handler des Controls, der hängen kann.

## Geprüft (E2E, `--data-dir` im Scratch)
- Haupt-Thread per `SuspendThread` 7 s angehalten: „not responding for 5 s (since 10:44:18.878)“, dann „responding again after 6.9 s“.
- `WM_DISPLAYCHANGE` nur an das `SystemEvents`-Fenster der Testinstanz geschickt: „Display settings changing“ und „changed“, 2 s später „Frames of the WebView's capture taken over“ (das Control hatte neu aufgebaut).
- Nicht geprüft: der Sitzungswechsel (Sperren/RDP) und die Werte in einer echten RDP-Sitzung.

## Zu prüfen beim nächsten Hänger
- Steht kurz vor „UI thread not responding“ ein „Display settings changing“ (oder „Windows session RemoteConnect“) → das blockierende `Lock()` des Controls. Dann den Display-Wechsel des Controls über den nicht blockierenden Neustart von `WebViewCapture` leiten (Handler des Controls von `SystemEvents` abhängen, `RestartWhenFree`).
- Steht nichts davor → Stacks nötig: „UI-Hänger protokollieren“ an und `dotnet-stack`, oder im Hänger per Task-Manager ein Speicherabbild (enthält Daten aus dem Speicher, auf dem Laptop lassen).
- Der Render-Tier in der Startzeile zeigt, ob WPF in der RDP-Sitzung in Software rendert (Tier 0).
