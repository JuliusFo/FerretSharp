# Fix: Leeres Fenster über RDP (`fix/webview-rdp-front-buffer`) → Release offen

Meldung des Nutzers (2026-11-06, 3.61.0): FerretSharp läuft auf dem Arbeitslaptop, er wechselt am PC zwischen der RDP-Verbindung und anderen Fenstern. Plötzlich war der ganze Inhalt des Fensters weg, nur der Fensterhintergrund blieb, auch nach Minimieren und Wiederherstellen. Startzeile: remote session yes, render tier 2, GPU in a remote session on (ADR 0043). Nachtrag zu ADR 0038.

## Analyse
- Das Log zeigt keinen Sitzungs- und keinen Anzeigewechsel, die RDP-Verbindung blieb also stehen. Es gibt viele „Restarting the WebView's capture (front buffer available again)“, zum Schluss zwei „back from minimized“, alle erfolgreich („taken over“). Die Aufnahme lieferte weiter Bilder, aber nur 1–3 je Sekunde statt 20–27 wie bei laufender Anzeige: Die Bilder wurden nicht gezeigt (ADR 0043, jedes gezeigte löst das nächste aus).
- Im Control (1.0.4258.31, `GraphicsItemD3DImage.RequestRender`) wird nur kopiert, wenn `IsFrontBufferAvailable || _useSoftwareRendering` gilt; das zweite ist nur bei Render-Tier 0 gesetzt. Über RDP war es bis 3.58 Tier 0, seit dem GPU-Schalter ist es Tier 2. Ohne Front Buffer (RDP-Fenster am PC im Hintergrund) verwirft es also jedes Bild.
- Jeder Neustart gibt zuerst den Back Buffer frei (`ClearBackBuffer` → `SetBackBuffer(null)`), ebenso eine Anzeigeänderung (`OnDisplaySettingsChanging`, `WM_DISPLAYCHANGE` im `WndProc` des Controls). Ohne Front Buffer setzt das Control danach keinen neuen.
- Im `D3DImage` (PresentationCore 10.0.12) meldet nur ein gesetzter Back Buffer die Rückkehr des Front Buffers (Callback der `InteropDeviceBitmap`; ohne Back Buffer ist die alte per `Detach` gelöst, es gibt keinen Callback). Ohne Back Buffer bleibt `IsFrontBufferAvailable` also für immer falsch: Das Control wartet auf den Front Buffer, WPF auf einen Back Buffer, das Bild bleibt leer. Minimieren hilft nicht, weil es nur einen weiteren Neustart auslöst.
- Mit `enableSoftwareFallback: true` (setzt das Control) behält WPF den Back Buffer ohne Front Buffer und zeigt ihn in Software, so wie bei Tier 0 immer.

## Gebaut
- `WebViewCapture.ShowWithoutFrontBuffer`: Fehlt der Front Buffer, wird `_useSoftwareRendering` des Controls gesetzt (per Reflection), es kopiert also weiter und WPF zeigt das Bild in Software. Kommt er zurück, gilt wieder die Regel des Controls (nur Tier 0). Nachgezogen nach jedem Tier-Wechsel (`RenderCapability.TierChanged`, nach dem Handler des Controls, der es nach dem Tier allein setzt).
- `WaitsForFrontBuffer`: Nur wenn `_useSoftwareRendering` fehlt, fällt ein Neustart ohne Front Buffer (bei Tier ≠ 0) aus, Info „postponed: no front buffer“; der Neustart folgt mit „front buffer available again“. Sonst läuft er wie bisher (Review).
- Front-Buffer- und Tier-Teil (`WatchImage`) laufen auch, wenn die Member des Neustarts fehlen; die Übernahme der Anzeigeänderung nur mit ihnen (Review).
- `Dispose` meldet `RenderCapability.TierChanged` nur auf dem UI-Thread ab: Beim Beenden läuft es auf einem Pool-Thread (`App.OnExit`), dort legte die Abmeldung einen eigenen Dispatcher und `MediaContext` an (Review).
- Log: „The WebView's front buffer is gone (render tier N)“ und „Render tier changed to N“ (Information). Fehlt das Member nach einem WebView2-Update, steht beim Start eine Warnung „cannot be shown without a front buffer“.

## E2E (2026-11-06)
- Lokal (Konsole, Tier 2): Start ohne Warnung, also Member gefunden. Dreimal minimiert und wiederhergestellt, danach ein Element per CDP eingefügt; es ist im Fensterbild (`PrintWindow`) zu sehen. Log ohne `[WRN]`/`[ERR]`.

## Nicht geprüft
- Der eigentliche Weg ohne Front Buffer: Lokal lässt er sich nicht auslösen. Nachzuprüfen mit einer Testversion beim Nutzer über RDP: das RDP-Fenster am PC in den Hintergrund legen bzw. minimieren, zurückholen, FerretSharp minimieren und wiederherstellen. Im Log dann „front buffer is gone“, danach „front buffer available again“; das Bild darf nie leer werden.
- Last ohne Front Buffer: Jetzt wird jedes Bild kopiert und in Software gezeigt, mit der Rückkopplung unter 23H2 (ADR 0043) womöglich 20–30 je Sekunde, während niemand hinsieht. Beim RDP-Test „WebView frames arrived“ und die CPU-Last mit RDP-Fenster im Hintergrund ansehen.
- Ob WPF das Bild ohne Front Buffer bei Tier 2 wirklich in Software zeigt, ist aus dem Code erschlossen (wie bei Tier 0), nicht beobachtet.
- Kein Unit-Test (kein Testprojekt für `FerretSharp.App`, Backlog).

## Nachtrag: Code-Review (2026-11-06)
Review per eigenem Agenten, Befunde nachgeprüft. Entscheidung des Nutzers: „Alle beheben“.
1. M (Verdacht) – Neustart ohne Front Buffer aufgeschoben: neben dem Software-Flag überflüssig und nahm den Ausweg Minimieren (meldet WPF den Front Buffer nicht zurück, bliebe jeder Neustart „postponed“ und das Bild dauerhaft in Software) → behoben, Aufschub nur noch ohne das Member (72b5771c).
2. K – `Dispose` meldete `TierChanged` auf dem Pool-Thread ab (neuer Dispatcher und `MediaContext`, nichts abgemeldet) → behoben, nur auf dem UI-Thread (72b5771c).
3. K – Software-Weg hing an den Membern des Neustarts → behoben, `WatchImage` unabhängig davon (72b5771c).
4. K – Doku und Kommentare ungenau (Grund ohne Callback, Anzeigeänderung gibt den Back Buffer ebenso frei, veralteter Kommentar in `DeliverHeld`) → behoben (72b5771c, b66c0ca3).
5. K (Verdacht) – Last ohne Front Buffer → unter „Nicht geprüft“, beim RDP-Test ansehen.
- Zweiter Durchgang: Fixes decken ihre Befunde, nichts anderes kaputt; nur dieser Nachtrag fehlte noch. E2E danach: minimieren/wiederherstellen und Beenden ohne `[WRN]`/`[ERR]`.
