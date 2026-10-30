# Fix: Bild der WebView steht nach dem Ruhezustand (`fix/webview-frozen-after-sleep`) → Release 3.56.1
Meldung des Nutzers (2026-10-30, 3.56.0): Nach dem Aufwachen ließ sich FerretSharp nicht mehr bedienen – der Mauszeiger wechselte über Knöpfen, Klicks bewirkten nichts, Minimieren/Maximieren ging. Log: `Power mode "Resume"`, 5 s später der Keep-alive mit ORA-12571 (Verbindung weg), dann nichts bis zum Schließen per ✕ (`Main window closing by the user`).

## Analyse
- Der UI-Thread lief: Das `Closing` des Fensters kam an und wurde geloggt. Also kein Hänger wie am 21.10. (`fixes-2026-10-21.md`).
- BlazorWebView 10 nutzt fest das `WebView2CompositionControl`: Bild per `GraphicsCaptureSession` in ein `D3DImage`, Eingaben per `SendMouseInput`, Zeiger per `CursorChanged`. In der mitgebrachten WebView2-Version 1.0.3179.45 (`ilspycmd`) entstehen die Direct3D-Geräte einmal im Konstruktor, der Back Buffer wird einmal gesetzt – nach verlorenem Gerät oder zurückkehrendem Front Buffer bleibt das letzte Bild stehen, die Seite läuft weiter. Das passt zu allem: Zeiger wechselt (kommt nicht aus dem Bild), Klicks „bewirken nichts“ (man sieht ihre Wirkung nicht), und das Verbindungsbanner nach dem Keep-alive hätte erscheinen müssen.
- Seit 1.0.4258 baut das Control nach einer Änderung der Bildschirmeinstellungen alles neu auf (`RecoverAfterDisplayChange`); ein Aufwachen löst diese nicht immer aus.

## Gebaut
- `Microsoft.Web.WebView2` transitiv auf 1.0.4258.31 (`Directory.Packages.props`).
- `App/Services/WebViewCapture`: stößt den Wiederaufbau des Controls an (per Reflection: `OnDisplaySettingsChanging`, dann `OnDisplaySettingsChanged`) – 3 s nach `PowerModes.Resume` (`App.WatchSleep`), wenn der Front Buffer zurückkommt und wenn das Fenster aus dem minimierten Zustand zurückkehrt (`MainWindow`). Jeder Neustart steht mit Grund im Log; fehlen die Member nach einem Update, warnt das Log beim Start. ADR 0038.

## E2E (2026-10-30)
Eigene Instanz (Build im Scratchpad, `--data-dir`, eigener WebView2-Ordner, Debug-Port, deutsch/dunkel), ohne Datenbank:
- Start ohne Warnung (Member gefunden); im Ausgabeordner liegt `Microsoft.Web.WebView2.Wpf.dll` aus 1.0.4258.31.
- Dreimal minimiert und wiederhergestellt (`ShowWindow`): je ein „Restarting the WebView's capture (back from minimized)“; danach per CDP ein Element eingefügt – im Fensterbild (`PrintWindow`) sichtbar, die Aufnahme läuft also nach dem Neustart.
- Danach echter Windows-Klick (`SendInput`) auf „Verbindung hinzufügen“: Dialog öffnet und ist zu sehen. Maximieren: Inhalt füllt das Fenster neu. Log ohne `[WRN]`/`[ERR]`.
- Nicht prüfbar: der Ruhezustand selbst und der zurückkehrende Front Buffer (lassen sich nicht auslösen). Beim nächsten Aufwachen im Log nach „Restarting the WebView's capture (after sleep)“ sehen; steht das Bild trotzdem, hilft Minimieren und Wiederherstellen.

## Offen
- Backlog „Anzeige bleibt stehen, bis man klickt“ (2026-10-29) betrifft denselben Aufnahmeweg; mit der neuen WebView2-Version erneut beobachten.
