# Fix: WebView-Bild nach einem Sitzungswechsel → Release 3.59.0

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

## E2E (2026-11-03)
- Lokal: Start ohne Warnung, beide „taken over“-Zeilen, Minimieren/Wiederherstellen baut neu auf.
- Beim Nutzer mit 3.58.1-test.4 (zusammen mit `fix-rdp-gpu`, GPU-Schalter an): RDP getrennt, zurück an der Konsole, entsperrt. Neustart nach „front buffer available again“ 0,13 s vor `ConsoleConnect` klappte beim ersten Versuch, die Anzeigeänderung baute über den eigenen Handler neu auf, der Neustart nach `SessionUnlock` lief 3 s später; kein Dialog, Bild lief ohne Minimieren („Ja, lief“).

## Nicht geprüft
- Die Wiederholungen selbst: Im Testlauf scheiterte kein Neuaufbau (`E_NOTIMPL` kam nur im Lauf mit Software-Rendering, Tier 0). Nachstellen mit `--rdp-software` (aus `fix-rdp-gpu`) und demselben Wechsel; im Log dann „trying again“ und „rebuilt at attempt N“.
- Kein Unit-Test (kein Testprojekt für `FerretSharp.App`, Backlog).
