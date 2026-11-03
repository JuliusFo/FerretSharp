# Fix: WebView-Bild nach einem Sitzungswechsel → Release offen

Gefunden im Code-Review von `fix-rdp-gpu.md` (2026-11-03): Der Nutzer wechselte von RDP zurück an den Laptop, mit offener App (Testversion ohne GPU-Schalter, Tier 0). Folge: Dialog „Unerwarteter Fehler“, das Bild stand, bis er minimierte und wiederherstellte. Entscheidung des Nutzers: „2–4, dann eigener Fix“. Branch `fix/webview-session-switch`, Nachtrag zu ADR 0038.

## Analyse (Log des Nutzers)
- `RemoteDisconnect`, `SessionLock`, dann „front buffer available again“ – 0,2 s vor `ConsoleConnect`. Unser Neustart baute neu auf, `Direct3DHelper.CreateD3D9Device` scheiterte mit `E_NOTIMPL` (`NotImplementedException`), wir setzten die Markierung zurück. Danach kopierte das Control in Geräte, die es nicht mehr hatte: rund 100 Debug-Zeilen „Copying a WebView frame failed: Object reference …“ je Sekunde.
- 1,3 s nach `ConsoleConnect` meldete Windows die Anzeigeänderung; der Neuaufbau des Controls scheiterte genauso. Es wiederholt nur nach `COMException`, die Ausnahme erreichte `OnDispatcherUnhandledException` → Dialog.
- Minimieren/Wiederherstellen 12 s später baute fehlerfrei neu auf.
- Ursache nicht der GPU-Schalter (aus), sondern die Zeit, in der die Konsole noch keine Anzeige hat.

## Gebaut
- `WebViewCapture.StartRebuild`/`RebuildNow`: jeder Neuaufbau mit Wiederholungen nach jeder Ausnahme, alle 500 ms, höchstens 30 s; währenddessen bleibt die Änderung markiert (das Control verwirft Bilder, statt in fehlende Geräte zu kopieren). Danach Markierung zurück und Warnung „failed after N attempts“. Erster Fehlschlag und später Erfolg („rebuilt at attempt N“) als Information.
- `WebViewCapture.OnDisplaySettingsChanged` ersetzt den Handler des Controls an `SystemEvents.DisplaySettingsChanged` (abgehängt über einen gleichen Delegaten; `SystemEvents` vergleicht mit `Equals`). Debug-Zeile „Display changes of the WebView's capture taken over“. `WebViewCapture` ist jetzt `IDisposable` und hängt sich beim Beenden ab.
- `App.WatchSleep`: `ConsoleConnect`, `RemoteConnect`, `SessionUnlock` → `RestartSoon` (3 s später). `RestartSoon` fasst mehrere Aufrufe zu einem Neustart zusammen (ein Timer statt einer je Aufruf).

## Nicht geprüft
- Der Wechsel selbst: hier ohne RDP nicht nachzustellen; Testversion an den Nutzer. Lokal geprüft: Start ohne Warnung, beide „taken over“-Zeilen, Minimieren/Wiederherstellen baut neu auf.
- Kein Unit-Test (kein Testprojekt für `FerretSharp.App`, Backlog).
