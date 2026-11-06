# Diagnose: WebView-Bild hängt über RDP einen Schritt hinterher (`fix/webview-size-diagnostics`)
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
- `WebViewSizeWatch` (App), immer an, neben `WebViewCapture` (eigene Klasse, damit PR #132 in `WebViewCapture.cs` nicht kollidiert). Je Zeile die ganze Kette: Fensterzustand, Größe des Controls in Pixeln samt DPI (auch `_dpiScale` des Controls), `Bounds`/`RasterizationScale`/`IsVisible` des Controllers, Größe des letzten kopierten Bildes (`_lastSize` = `ContentSize`), Größe des gezeigten Bildes (`D3DImage.PixelWidth/Height`), Front Buffer.
- Zeilen: „WebView sizes (…, changing)“ beim ersten Größen- oder Zustandswechsel einer Folge; „WebView sizes (…, settled after N ms)“ nach 1 s Ruhe, zusätzlich mit dem Viewport der Seite (`innerWidth`×`innerHeight` @`devicePixelRatio` per `ExecuteScriptAsync`, höchstens 1 s warten, sonst „?“) und der Zahl der Bildwechsel; „WebView picture now WxH (N ms after …)“, sobald das gezeigte Bild nach einem Wechsel die neue Größe annimmt, und immer, wenn es außerhalb eines Wechsels die Größe ändert (auch 0×0, wenn ein Neustart den Back Buffer freigibt). Beim Start eine Zeile „start“.
- Private Member per Reflection wie in `WebViewCapture`; fehlt einer, steht „?“ und einmal eine Warnung.
- Ein JS-Ausdruck vom Host (`ExecuteScriptAsync`) nur für die Diagnose, keine Logik.

## Geprüft (E2E, `--data-dir` im Scratch, Konsole, Tier 2)
- Start, Maximieren, Wiederherstellen, Minimieren und Wiederherstellen per `ShowWindow`: Jede Stufe der Kette folgt der neuen Größe nach ~20 ms („picture now 2560x1377 (21 ms after …)“), die Seite meldet dieselbe Größe; nach dem Neustart aus minimiert „picture now 0x0“, 90 ms später wieder 1424×861. Keine Warnung im Log.
- Nicht geprüft: eine echte RDP-Sitzung (der Fehler kam dort nur sporadisch).

## Zu prüfen beim nächsten Auftreten
- Die „settled“-Zeile nach dem Wechsel lesen: Wo weicht die Größe zuerst von `control` ab? `browser`/`page` → Stelle 1, `frame` → Stelle 2, nur `picture` → Stelle 3. Kommt nach „changing“ keine „picture now“-Zeile, hat das gezeigte Bild die Größe nie gewechselt.
- Beim leeren Start: „picture“ in der Start-Zeile (0×0 = nie ein Back Buffer gesetzt) und der Front Buffer (seit PR #132 auch eigene Zeilen dazu).
