# Diagnose: WebView-Bild hängt über RDP einen Schritt hinterher (`fix/webview-size-diagnostics`) → Release 3.62.0
Meldungen des Nutzers (2026-11-03 und 2026-11-06, Arbeits-Laptop per RDP): Die Verbindungsübersicht war nach dem Start (auch direkt nach einem Update) oder nach längerem Liegenlassen leer. Nach dem Maximieren stand das Bild der alten Größe oben links, der Rest war leer; nach dem Wiederherstellen stand das maximierte Layout rechts abgeschnitten im kleinen Fenster. Minimieren und Wiederherstellen (Neustart der Aufnahme) brachte das richtige Bild. Tritt nur sporadisch auf. Auftrag: Diagnose einbauen und mit dem Front-Buffer-Fix (PR #132) als Patch ausliefern, keine Testversion.

## Befunde
- Die Maße passen genau: Das Bild im maximierten Fenster hatte die Größe des vorigen kleinen Fensters (≈1426×861 bei 2560×1440). Das Bild hängt also eine Größe hinterher.
- Tier 0 (WPF in Software, 2026-11-03) **und** Tier 2 (GPU in der Remote-Sitzung seit 3.59.0, 2026-11-06). Die erste Vermutung „nur im Software-Weg von WPF“ trägt deshalb nicht.
- Bilder kamen weiter an („WebView frames arrived: 342 in 60 s“), keines wurde zurückgehalten oder verworfen, kein Neustart der Aufnahme bis zum Minimieren, kein Display- oder Sitzungswechsel im Log. Es fehlen also nicht einfach Bilder: Entweder haben die Bilder selbst die alte Größe, oder die Seite legt ihr Layout für die alte Größe an.
- Im dekompilierten Control (WebView2 1.0.4258.31): `SizeChanged` setzt `UpdateSize` (Frame-Pool in neuer Größe), `CoreWebView2Controller.Bounds` und die DPI. `RequestRender` legt Textur und Back Buffer erst neu an, wenn ein Bild mit anderer `ContentSize` kommt (`_lastSize`).

## Mögliche Stellen (offen)
1. **Browser:** Die `Bounds` des Controllers oder die Seite (`innerWidth`) bleiben auf der alten Größe – die Seite legt für das alte Fenster an (passt zum abgeschnittenen maximierten Layout).
2. **Aufnahme:** Die Bilder (`ContentSize`) behalten die alte Größe, obwohl Browser und Seite die neue haben.
3. **WPF:** Bild und Aufnahme haben die neue Größe, WPF zeigt aber ein altes Bild (`D3DImage.PixelWidth` alt).

## Gebaut
- `WebViewSizeWatch` (App), immer an, neben `WebViewCapture` (eigene Klasse, damit PR #132 in `WebViewCapture.cs` nicht kollidiert). Je Zeile die ganze Kette: Fensterzustand, Größe des Controls in Pixeln wie das Control sie rechnet (abgeschnitten, mit seinem `_dpiScale`; daneben die DPI von WPF), `Bounds`/`RasterizationScale`/`IsVisible` des Controllers, `frame` (`_lastSize`: zuletzt eingerichtete oder kopierte Größe), Back Buffer gesetzt, Größe des gezeigten Bildes (`D3DImage.PixelWidth/Height`), Front Buffer.
- Zeilen: „WebView sizes (…, changing)“ beim ersten Größen- oder Zustandswechsel einer Folge (bei einem Zustandswechsel noch mit den alten Größen, das Layout folgt danach); „WebView sizes (…, settled N ms after it began)“ nach 1 s Ruhe, zusätzlich mit dem Viewport der Seite (`innerWidth`×`innerHeight` @`devicePixelRatio` per `ExecuteScriptAsync`, höchstens 1 s warten, sonst „?“), den in der Folge angekommenen und kopierten Bildern und der Zahl der Bildwechsel; „WebView picture now WxH (N ms after the last change began)“, sobald das gezeigte Bild nach einem Wechsel die neue Größe annimmt, und immer, wenn es außerhalb eines Wechsels die Größe ändert (auch 0×0, wenn ein Neustart den Back Buffer freigibt). Beim Start eine Zeile „start“.
- Private Member per Reflection wie in `WebViewCapture`; fehlt einer, steht „?“ und einmal eine Warnung.
- Ein JS-Ausdruck vom Host (`ExecuteScriptAsync`) nur für die Diagnose, keine Logik.

## Geprüft (E2E, `--data-dir` im Scratch, Konsole, Tier 2)
- Start, Maximieren, Wiederherstellen, Minimieren und Wiederherstellen per `ShowWindow`: Jede Stufe der Kette folgt der neuen Größe nach ~20 ms („picture now 2560x1377 (21 ms after …)“), die Seite meldet dieselbe Größe; nach dem Neustart aus minimiert „picture now 0x0“, 90 ms später wieder 1424×861. Keine Warnung im Log.
- Nicht geprüft: eine echte RDP-Sitzung (der Fehler kam dort nur sporadisch).

## Zu prüfen beim nächsten Auftreten
- Die „settled“-Zeile nach dem Wechsel lesen. `control` ist genau die Größe, die das Control dem Browser (`Bounds`) und dem Frame-Pool (`UpdateSize`) gibt. Wo weicht es zuerst ab?
  - `browser`/`page` → Stelle 1.
  - `frames arrived 0` → die Aufnahme liefert nichts (Stelle 2).
  - Bilder angekommen, `copied 0` → das Control kopiert nicht (kein Front Buffer, `TryLock`), Stelle 2.
  - kopiert, aber `frame` alt → die Bilder haben die alte `ContentSize` (Stelle 2). Achtung: `CopyResource` tut bei abweichenden Maßen von Pool und Bild still nichts – ein „kopiertes“ Bild ändert dann das gezeigte nicht.
  - nur `picture` alt bei neuem `frame` → Stelle 3.
- `frame` allein beweist kein kopiertes Bild: Das Control setzt `_lastSize` auch beim Start und Neuaufbau der Aufnahme.
- `copied` > `arrived`: Das Control hatte selbst einen neuen Frame-Pool angelegt, den `WebViewCapture` erst bei der nächsten Prüfung (höchstens 2 s) übernimmt; bis dahin zählt `arrived` zu wenig. Das Freigeben des Back Buffers (Neustart, `WM_SIZE`) zählt nicht als Kopie.
- Kommt nach „changing“ keine „picture now“-Zeile, hat das gezeigte Bild die Größe nie gewechselt.
- Beim leeren Start: Start-Zeile mit `picture 0x0`/`back buffer none` = nie ein Bild gesetzt; dazu die Zähler und der Front Buffer (seit PR #132 auch eigene Zeilen dazu).

## Nachtrag: Code-Review (2026-11-06)
Entscheidung des Nutzers: „Jop, fix alles.“
1. M – `CoreWebView2` nach einem Absturz des Browser-Prozesses las vor dem `try` und warf aus dem `async void`-Handler: bei jedem Wechsel der Dialog „Unerwarteter Fehler“ (vor dem Fix nachgestellt: Browser-Prozess beendet, maximiert → `[ERR] Unhandled exception on UI thread`). Behoben: Zugriff im `try`, die Zeile „settled“ als Ganzes abgesichert (Debug-Zeile).
2. M – `frame` (`_lastSize`) setzt das Control auch beim Start und Neuaufbau der Aufnahme; „nur picture weicht ab“ bewies kein WPF-Problem. Behoben: die Zeile „settled“ zählt angekommene (`WebViewCapture.FramesArrived`) und kopierte Bilder (`D3DImage.Changed` kommt mit jeder Kopie) und nennt den Back Buffer; „Zu prüfen“ unten angepasst.
3. M – Ein Wechsel, während die Zeile „settled“ auf die Seite wartete, übernahm deren Werte und verschluckte sein erstes „picture now“. Behoben: Werte vor dem `await`, `_pictureSeen` davor gesetzt.
4. K – Der Grund wuchs bei einem Drag nach einem Zustandswechsel um „, size“ je Ereignis. Behoben: jeder Grund einmal.
5. K – Die Größe des Controls war gerundet, das Control schneidet ab (Scheinabweichung 1 px zu `browser`). Behoben: dieselbe Formel wie das Control.
6. K – Kommentare (Takt von `D3DImage.Changed`, alte Werte bei Zustandswechsel, „settled“ ab Beginn der Folge). Behoben.
7. K (Verdacht) – Eine verwaiste `ExecuteScriptAsync`-Task nach der Zeitgrenze könnte als „Unobserved task exception“ ins Log kommen. Nicht nachgestellt (Seite 8 s blockiert, nach der Grenze Browser-Prozess beendet: 60 s lang keine Zeile), daher nicht geändert.
8. K (zweiter Durchgang) – `copied` zählte das Freigeben des Back Buffers mit (`SetBackBuffer(IntPtr.Zero)` löst ebenfalls `Changed` aus): über RDP mit flackerndem Front Buffer „copied 1“ ohne Kopie. Entscheidung des Nutzers: „Jop“. Behoben: nur gezählt, wenn danach ein Bild da ist.

Geprüft in der Testinstanz (Konsole, Tier 2): Drag nach Maximieren/Wiederherstellen („state Maximized, size, state Normal“), zweite Folge während des Wartens auf eine blockierte Seite (eigenes „picture now“), Absturz des Browser-Prozesses (kein `[ERR]`, `browser`/`page` „?“).
