# Fix: Bild der WebView steht nach dem Ruhezustand oder nach schnellen Änderungen (`fix/webview-frozen-after-sleep`) → Release 3.56.1
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

## Nachtrag: Anzeige bleibt stehen, bis man klickt (Backlog 2026-10-29)
Erneuter Test beim Kollegen (2026-10-30): Ein Klick irgendwo auf den LOB-Dialog zeigt das Ergebnis, ein Klick außerhalb nicht; nach Zeilenwechsel geht es sofort; Doppelklick auf eine bereits markierte Zeile löst es aus, auf eine nicht markierte nicht. Zwei SQL-Tabs: über den anderen fahren, den Tooltip abwarten, klicken – der Editor zeigt nur die erste Zeile, bis man hineinklickt. Bei Tabellen auch schon vorgekommen.

- Ursache (ADR 0038): Das Control kopiert ein Bild nur, wenn es das `D3DImage` sofort sperren kann, sonst verwirft es das Bild. Belegt ist die Sperre, solange WPFs Render-Thread das vorige Bild präsentiert. Die Aufnahme liefert erst bei der nächsten Änderung der Seite wieder ein Bild – deshalb hilft nur ein Klick, der etwas Sichtbares ändert. Beim Tab-Wechsel mit Tooltip kommen drei Änderungen direkt hintereinander (Tooltip weg, Tab sichtbar, Monaco zeichnet nach `automaticLayout` alle Zeilen); die letzte ging verloren. Das WebView2-Update ändert daran nichts.
- Gebaut: `WebViewCapture` übernimmt die Bilder des Bildpuffers (Handler des Controls ab, eigener an), wartet bei belegter Sperre und kopiert dann (`RequestRender`), höchstens 1 s; ein neuer Bildpuffer nach einem Wiederaufbau wird übernommen (Prüfung alle 2 s). Debug-Zeilen: „Frames of the WebView's capture taken over“, „WebView frames held until the image was free: N, longest X ms“ (höchstens einmal je Minute).
- E2E: Stresstest im Scratchpad (CDP: Serien von 2–240 Bildwechseln per `requestAnimationFrame`, danach Fensterbild per `PrintWindow` prüfen) – vor und nach dem Fix kein verlorenes Bild, die Sperre war auf diesem Rechner nie belegt (keine „held“-Zeile). Gegenprobe mit einem Handler, der nichts tut: Das Bild blieb stehen, der Handler des Controls war also ab. Nach Minimieren/Wiederherstellen neuer Bildpuffer übernommen, danach wieder kein verlorenes Bild; echter Klick wirkt sichtbar.
- Beim Kollegen (Test-Build 3.56.1-test.1, 2026-10-30): Die Abläufe oben zeigen das Ergebnis ohne Klick. Log: „held … 1, longest 47 ms“, eine Minute später „134, longest 301 ms“ – auf seinem Rechner ist die Sperre oft und teils lange belegt, also genau die verworfenen Bilder von vorher.

## Nachtrag: Code-Review (2026-10-30)
Review per eigenem Agenten, Befunde nachgeprüft. Entscheidung des Nutzers: „Alle beheben“ (zu 1: Neustart auslassen statt blockierend warten; zu 6: nur Doku, kein Testprojekt – beides wie vorgeschlagen).

1. S – `Restart` → `OnDisplaySettingsChanging` → `ClearBackBuffer` sperrt mit `Lock()` ohne Grenze; bei ausgefallenem Render-Thread hinge der UI-Thread für immer: behoben, Neustart erst bei freier Sperre (`TryLock(0)` alle 50 ms, höchstens 1 s), sonst Warnung. Der Hänger selbst ist nicht nachzustellen; per Versuchsschalter (Sperre als belegt gemeldet, danach entfernt) geprüft: Warnung nach 1 s, App bleibt bedienbar.
2. K – Neuer Bildpuffer nach einem Neustart erst bis zu 2 s später übernommen: behoben, Übernahme direkt nach dem Wiederaufbau (E2E: 50–70 ms nach jedem Neustart).
3. K – Kommentar „newest frame of the pool“ falsch (ein Puffer): korrigiert, Verhalten im ADR als beobachtet vermerkt.
4. K – Nach 1 s verworfene Bilder zählten als „held“: behoben, eigene Zahl „dropped after 1 s“ in der Debug-Zeile.
5. K – Andere Ausnahme als `COMException` beim Wiederaufbau ergäbe Fehlerdialog und markierte Änderung (jedes Bild verworfen): behoben, `RecoverAfterDisplayChange` wird selbst aufgerufen, `COMException` geht an die Wiederholungen des Controls, alles andere wird geloggt und die Markierung zurückgesetzt. Nicht nachgestellt (keine solche Ausnahme auslösbar).
6. K – Kein Test sichert die privaten Member bzw. das Abhängen des Handlers: nur dokumentiert (ADR 0038, Gegenprobe bei jedem Heben); fehlendes App-Testprojekt im Backlog.

Zweiter Durchgang des Reviewers: 1–5 abgedeckt, nichts anderes kaputt (Reentranz von Neustart/Wiederaufbau/Halten, echter Display-Wechsel parallel geprüft). Neu: K – Kommentar behauptete, der Wiederaufbau laufe nach dem Commit des freigegebenen Back Buffers (`BeginInvoke` mit Normal läuft vor dem Render-Durchgang): Kommentar korrigiert, Verhalten wie beim Control.

Ohne Unit-Test (kein Testprojekt für `FerretSharp.App`); Build, Tests und E2E (Neustarts, Stresstest, echter Klick) grün.
