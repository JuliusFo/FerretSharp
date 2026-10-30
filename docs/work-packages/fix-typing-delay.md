# Fix: Getipptes erscheint verzögert (`fix/typing-delay`)
Meldung des Nutzers (2026-10-30, 3.56.1): Im Dialog „Neue Verbindung“ ins Feld Name tippen, 5–10 s warten, weitertippen – es „braucht ewig“, bis das Getippte zu sehen ist; der Cursor blinkt stellenweise unregelmäßig.

## Analyse
- Blazor und Chromium sind schnell (Ausgangssession): vom `input` bis zum fertigen Render 2–7 ms, nächster Chromium-Frame nach 1–13 ms.
- Messaufbau (Scratchpad): Testinstanz mit `--data-dir`, Debug-Port; ein Messprogramm nimmt alle ~20–30 ms das Fensterbild über dem Feld auf (`PrintWindow`, Flag 2, Zahl heller Pixel) und – nur beim Test mit dem Nutzer – die Zeitpunkte echter Tastendrücke (Low-Level-Hook, nur Zeiten). In der Seite per CDP `keydown`/`input` mitgeschrieben; in Messversionen von `WebViewCapture` jedes Bild der Aufnahme samt Kopie.
- Per CDP im 1200×800-Fenster (`Input.dispatchKeyEvent` mit Pausen bis 12 s): 2 von 36 Läufen verloren Bilder (ein Zeichen erst mit dem nächsten, ~15–20 fehlende Blinkwechsel in 40 s), beide mit dem aktuellen Stand; 3.56.0 und eine Variante ohne die Bildübernahme aus ADR 0038 blieben in ihren Läufen sauber, ein abwechselnder Vergleich (je 8 Läufe) zeigte bei keinem etwas – zu selten für einen Schluss. Mit der Pause hatte es nichts zu tun.
- Mit dem Nutzer, echte Tastatur, Fenster maximiert auf 3440×1440: aktueller Stand 5 von ~30 Zeichen 0,2–0,6 s zu spät, 3.56.0 5–6 von 27 (einmal 2,1 s) – **keine Regression von 3.56.1**. Im kleinen Fenster beim Nutzer kein verspätetes Zeichen. Je Taste: `keydown` +2 ms, `input` +3 ms, Bild der Aufnahme +20 ms angekommen und kopiert (`D3DImage` danach dirty), im Fenster aber erst mit dem nächsten Bild (Blinken oder nächste Taste). Kein Bild zurückgehalten (ADR 0038 nicht beteiligt).
- Ursache (ADR 0039): Das Control kopiert per D3D11 `CopyResource` in eine geteilte Textur, WPF präsentiert sie über sein D3D9-Gerät ohne Synchronisation. Ist die Kopie auf der GPU noch nicht gelaufen, zeigt WPF das alte Bild – und präsentiert erst beim nächsten Bild wieder. Große Bilder kopieren länger, deshalb fiel es im maximierten Fenster auf.
- Gegenproben beim Nutzer: nur `Flush` nach der Kopie – weiter 6 von 30 verspätet, „ist weiterhin so“. Rücklesen der Kopie per `Map` (wartet auf die GPU) – 0 von 28, „diesmal flüssig“.

## Gebaut
- `App/Services/CopyFence`: Event-Query auf dem D3D11-Kontext des Controls, nach jeder Kopie gewartet (höchstens 50 ms); eigene `ComImport`-Schnittstellen bis `CreateQuery`/`GetData`, Query neu bei neuen Geräten.
- `WebViewCapture.Render` wartet nach `RequestRender`; Debug-Zeile „WebView copies waited for on the GPU: N over 2 ms, longest X ms; not done after 50 ms: M“ höchstens einmal je Minute. Warnung beim Start, falls Gerät/Kontext nach einem Update fehlen; bei verlorenem Gerät neue Query mit den neuen Geräten.

## E2E (2026-10-30)
- Mit dem Nutzer, maximiert, echte Tastatur, 90 s mit Pausen: 0 von 28 Zeichen verspätet (Bild 25–50 ms nach `input`), „scheint zu laufen“. Log: einmal 2 ms gewartet, nichts über 50 ms.
- Kleines Fenster per CDP: Wartezeit ≤ 2 ms, kein Bildverlust. Logs der Testinstanzen ohne `[WRN]`/`[ERR]`.
- Ohne Unit-Test (kein Testprojekt für `FerretSharp.App`, wie ADR 0038).
