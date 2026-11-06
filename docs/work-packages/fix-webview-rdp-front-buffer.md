# Fix: Leeres Fenster über RDP (`fix/webview-rdp-front-buffer`) → Release offen

Meldung des Nutzers (2026-11-06, 3.61.0): FerretSharp läuft auf dem Arbeitslaptop, er wechselt am PC zwischen der RDP-Verbindung und anderen Fenstern. Plötzlich war der ganze Inhalt des Fensters weg, nur der Fensterhintergrund blieb, auch nach Minimieren und Wiederherstellen. Startzeile: remote session yes, render tier 2, GPU in a remote session on (ADR 0043). Nachtrag zu ADR 0038.

## Analyse
- Das Log zeigt keinen Sitzungs- und keinen Anzeigewechsel, die RDP-Verbindung blieb also stehen. Es gibt viele „Restarting the WebView's capture (front buffer available again)“, zum Schluss zwei „back from minimized“, alle erfolgreich („taken over“). Die Aufnahme lieferte weiter Bilder, aber nur 1–3 je Sekunde statt 20–27 wie bei laufender Anzeige: Die Bilder wurden nicht gezeigt (ADR 0043, jedes gezeigte löst das nächste aus).
- Im Control (1.0.4258.31, `GraphicsItemD3DImage.RequestRender`) wird nur kopiert, wenn `IsFrontBufferAvailable || _useSoftwareRendering` gilt; das zweite ist nur bei Render-Tier 0 gesetzt. Über RDP war es bis 3.58 Tier 0, seit dem GPU-Schalter ist es Tier 2. Ohne Front Buffer (RDP-Fenster am PC im Hintergrund) verwirft es also jedes Bild.
- Jeder Neustart gibt zuerst den Back Buffer frei (`ClearBackBuffer` → `SetBackBuffer(null)`). Ohne Front Buffer setzt das Control danach keinen neuen.
- Im `D3DImage` (PresentationCore 10.0.12) meldet nur ein gesetzter Back Buffer die Rückkehr des Front Buffers (Callback der `InteropDeviceBitmap`, `SetIsFrontBufferAvailable` prüft deren Version). Ohne Back Buffer bleibt `IsFrontBufferAvailable` also für immer falsch: Das Control wartet auf den Front Buffer, WPF auf einen Back Buffer, das Bild bleibt leer. Minimieren hilft nicht, weil es nur einen weiteren Neustart auslöst.
- Mit `enableSoftwareFallback: true` (setzt das Control) behält WPF den Back Buffer ohne Front Buffer und zeigt ihn in Software, so wie bei Tier 0 immer.

## Gebaut
- `WebViewCapture.ShowWithoutFrontBuffer`: Fehlt der Front Buffer, wird `_useSoftwareRendering` des Controls gesetzt (per Reflection), es kopiert also weiter und WPF zeigt das Bild in Software. Kommt er zurück, gilt wieder die Regel des Controls (nur Tier 0). Nachgezogen nach jedem Tier-Wechsel (`RenderCapability.TierChanged`, nach dem Handler des Controls, der es nach dem Tier allein setzt).
- `WaitsForFrontBuffer`: Ein Neustart ohne Front Buffer (bei Tier ≠ 0) fällt aus, Info „postponed: no front buffer“. Der Neustart folgt mit „front buffer available again“.
- Log: „The WebView's front buffer is gone (render tier N)“ und „Render tier changed to N“ (Information). Fehlt das Member nach einem WebView2-Update, steht beim Start eine Warnung „cannot be shown without a front buffer“.

## E2E (2026-11-06)
- Lokal (Konsole, Tier 2): Start ohne Warnung, also Member gefunden. Dreimal minimiert und wiederhergestellt, danach ein Element per CDP eingefügt; es ist im Fensterbild (`PrintWindow`) zu sehen. Log ohne `[WRN]`/`[ERR]`.

## Nicht geprüft
- Der eigentliche Weg ohne Front Buffer: Lokal lässt er sich nicht auslösen. Nachzuprüfen mit einer Testversion beim Nutzer über RDP: das RDP-Fenster am PC in den Hintergrund legen bzw. minimieren, zurückholen, FerretSharp minimieren und wiederherstellen. Im Log dann „front buffer is gone“, gegebenenfalls „postponed“, danach „front buffer available again“; das Bild darf nie leer werden.
- Ob WPF das Bild ohne Front Buffer bei Tier 2 wirklich in Software zeigt, ist aus dem Code erschlossen (wie bei Tier 0), nicht beobachtet.
- Kein Unit-Test (kein Testprojekt für `FerretSharp.App`, Backlog).
