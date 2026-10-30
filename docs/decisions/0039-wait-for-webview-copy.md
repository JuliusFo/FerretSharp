# ADR 0039: Auf die GPU-Kopie eines WebView-Bildes warten

- Status: akzeptiert
- Datum: 2026-10-30
- Paket: Fix `fix/typing-delay`

## Kontext

Meldung des Nutzers (3.56.1): Getipptes erscheint verzögert, besonders nach einer Pause, und der Cursor blinkt unregelmäßig. Messung mit echter Tastatur im maximierten Fenster (3440×1440) im Dialog „Neue Verbindung“: Tastendruck → `keydown` 2 ms → `input` 3 ms → Bild der Aufnahme 20 ms später angekommen und kopiert – sichtbar im Fenster aber bei etwa jedem fünften Zeichen erst mit dem nächsten Bild, also nach 0,2–2 s (nächstes Blinken oder nächste Taste). 3.56.0 zeigte dasselbe (5–6 von 27 Zeichen), ist also keine Folge von ADR 0038. In kleinen Fenstern (1200×800) selten (2 von 36 Messläufen per CDP).

Das `WebView2CompositionControl` kopiert jedes Bild mit `ID3D11DeviceContext::CopyResource` in eine geteilte Textur, die WPFs Render-Thread über ein eigenes Direct3D-9-Gerät präsentiert (`D3DImage`). Zwischen beiden Geräten gibt es keine Synchronisation: Der Kopierbefehl steht nur in der Warteschlange des D3D11-Kontexts. Präsentiert WPF, bevor die GPU kopiert hat, zeigt es das vorige Bild – und präsentiert erst wieder beim nächsten Bild der Aufnahme. Je größer das Bild, desto länger die Kopie, desto öfter verliert WPF das Rennen.

## Entscheidung

- **Nach jeder Kopie auf die GPU warten** (`App/Services/CopyFence`, aufgerufen in `WebViewCapture.Render`): Event-Query (`D3D11_QUERY_EVENT`) auf dem Kontext des Controls beenden und `GetData` abfragen, bis sie fertig ist – auf dem UI-Thread, bevor der Frame an WPFs Render-Thread übergeben wird. Höchstens 50 ms, danach weiter wie bisher; gezählt in einer Debug-Zeile (höchstens einmal je Minute, ab 2 ms Wartezeit).
- Gerät und Kontext per Reflection aus `GraphicsItemD3DImage` (`_d3d11Device`, `_d3d11DeviceContext`), angesprochen über eigene `ComImport`-Schnittstellen bis zur benötigten Methode (`CreateQuery`, `End`, `GetData`); die Query gehört FerretSharp und wird bei neuen Geräten (Wiederaufbau, ADR 0038) neu angelegt. Fehlen die Felder nach einem Update, warnt das Log beim Start und es läuft wie vorher.
- **Geprüft und verworfen:** nur `Flush` nach der Kopie (Befehl abschicken, nicht warten) – beim Nutzer weiter 6 von 30 Zeichen verspätet. Mit Warten (zuerst als Rücklesen der Kopie per `Map`, dann als Event-Query): kein verspätetes Zeichen, der Nutzer empfand es als flüssig.

## Folgen

- Der UI-Thread wartet je Bild auf die Kopie, gemessen 1–2 ms im kleinen Fenster; im großen Fenster steht die Dauer in der Debug-Zeile „WebView copies waited for on the GPU“. Bleibt „not done after 50 ms“ nicht bei 0, ist die GPU ausgelastet – dann ist das Bild wie vorher evtl. einen Schritt hinterher.
- Hängt an denselben privaten Membern wie ADR 0038: beim Heben von WebView2/BlazorWebView zusätzlich auf die Warnung „copies cannot be waited for“ achten.
- Die Bilder, die das Control bisher bei belegter Sperre verwarf (ADR 0038), und diese hier sind zwei Fehler desselben Wegs; ein Teil der „Anzeige steht bis zum Klick“-Fälle kann auch dieser gewesen sein.
