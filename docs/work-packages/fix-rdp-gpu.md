# Fix: Träges Tippen über RDP → Release offen

Meldung des Nutzers (2026-11-03, 3.58.0): Tippen im Grid (neue Zeilen) ist wieder träge, er arbeitet per RDP auf seinem Laptop. Branch `claude/webview-gpu-performance-44b20a`, ADR 0043.

## Analyse
- Im Log über RDP: Render-Tier 0, viele zurückgehaltene Bilder (ADR 0038, bis ~860 je Minute, das längste 83 ms). WPF rendert in einer Remote-Sitzung in Software und liest jedes Bild der WebView von der GPU zurück.
- Testversion 3.58.1-test.1 mit `--rdp-gpu` (`Switch.System.Windows.Media.EnableHardwareAccelerationInRdp`): Tier 2, Kopien höchstens 4 ms, kaum zurückgehaltene Bilder. Nutzer: „das Tippen ist deutlich flüssiger“.
- Nebenbefund: Die Aufnahme liefert auf dem Laptop des Nutzers (Lenovo Legion 5, Windows 11 23H2 22631) fortlaufend Bilder, auch wenn die Seite ruht – über RDP ~32/s, direkt ~165/s (Bildwiederholrate des Panels). Die Seite selbst ist still (keine Animationen, keine DOM-Änderungen, Chromiums FPS-Anzeige steht). Testversion 3.58.1-test.3 mit `--capture-probe` (je Minute 10 s Bilder gezeigt, 10 s nur aus dem Puffer genommen): 1.622 bzw. 1.631 Bilder gezeigt, 13 bzw. 11 nicht gezeigt. Jedes gezeigte Bild löst also das nächste aus. Auf dem Entwicklungsrechner (24H2) nicht nachzustellen (6 Bilder in 147 s).
- Unser Weg zeigt je Bild genau einmal an wie das Control; die Prüfung der Sperre (`TryLock` + `Unlock`) präsentiert nichts (`D3DImage.Unlock` nur nach `AddDirtyRect`, in PresentationCore nachgelesen). Vermutet, nicht belegt: Das Control verwarf ein Bild bei belegter Sperre und brach die Schleife damit ab, das Zurückhalten aus ADR 0038 hält sie am Leben. Entscheidung des Nutzers: erst den RDP-Fix, die Schleife in den Backlog.

## Gebaut
- `CommandLine.RdpSoftware` (`--rdp-software`) und `GpuInRemoteSession`; `Program.Main` setzt den Schalter ohne `--rdp-software`, vor `new App`.
- `DisplayWatch`: Start-Zeile nennt „GPU in a remote session on/off“.
- `WebViewCapture`: Debug-Zeile „WebView frames arrived: N in S s, X per second“ höchstens einmal je Minute (bleibt für die offene Schleife). Der Testmodus `--capture-probe` ist wieder entfernt.

## Nicht geprüft
- Der Schalter selbst läuft in keinem Test (kein Testprojekt für `FerretSharp.App`); `CommandLineTests` hält die Entscheidung fest. Über RDP nur beim Nutzer mit der Testversion geprüft.
