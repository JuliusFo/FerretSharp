# ADR 0042: WPF rendert auch in einer Remote-Sitzung auf der GPU

- Status: akzeptiert
- Datum: 2026-11-03
- Paket: Fix `fix/rdp-gpu` (Protokoll `fix-rdp-gpu.md`)

## Kontext

Meldung des Nutzers (3.58.0): Tippen im Grid (neue Zeilen) ist träge, wenn er per RDP auf seinem Laptop arbeitet. Das Log zeigte über RDP Render-Tier 0 und viele zurückgehaltene Bilder (ADR 0038: bis zu ~860 je Minute, das längste 83 ms).

WPF rendert in einer Remote-Sitzung von sich aus in Software (`RenderCapability.Tier` 0), auch wenn die Maschine eine GPU hat. Die WebView kommt aber als GPU-Textur (`D3DImage`, ADR 0038/0039): In Software liest WPF jedes Bild von der GPU zurück, bevor es gezeigt wird. Das dauert, die Sperre des Bildes bleibt länger belegt, und Bilder warten. Seit .NET 8 gibt es den AppContext-Schalter `Switch.System.Windows.Media.EnableHardwareAccelerationInRdp` (in PresentationCore 10.0.12 nachgesehen), mit dem WPF auch in einer Remote-Sitzung die GPU nimmt.

Testversion mit `--rdp-gpu` beim Nutzer: Render-Tier 2, kaum noch zurückgehaltene Bilder, Kopien höchstens 4 ms; „das Tippen ist deutlich flüssiger“. Entscheidung des Nutzers: einbauen.

## Entscheidung

- **Der Schalter ist gesetzt**: `Program.Main` setzt ihn vor `new App` (WPF liest ihn einmal beim Start seines Mediensystems). Die Entscheidung trifft `CommandLine.GpuInRemoteSession`.
- **Notausgang `--rdp-software`**: lässt den Schalter weg, WPF rendert in einer Remote-Sitzung wieder in Software. Ein Link kann ihn nicht anhängen (`CommandLine.Effective`). Keine Einstellung in der Oberfläche: Sie wirkte erst nach einem Neustart, und es gibt keinen bekannten Fall, der sie braucht.
- `DisplayWatch` schreibt beim Start „GPU in a remote session on/off“ neben Render-Tier und Remote-Sitzung ins Log.
- `--rdp-gpu` aus den Testversionen ist überflüssig, die App ignoriert es wie jedes unbekannte Argument.
- Außerhalb einer Remote-Sitzung ändert der Schalter nichts.

## Folgen

- Über RDP rendert der Rechner, auf dem FerretSharp läuft, mit seiner GPU; RDP überträgt das fertige Bild. Ohne GPU (VM ohne Grafikkarte) bleibt WPF bei Tier 0, wie bisher.
- Zeigt ein Fenster über RDP Fehler beim Zeichnen (schwarze Flächen, alte Bilder nach dem Wiederverbinden), zuerst mit `--rdp-software` vergleichen.
- Nicht gelöst und unabhängig davon: Auf dem Laptop des Nutzers (Windows 11 23H2) liefert die Aufnahme auch ohne Änderung der Seite fortlaufend Bilder, solange sie gezeigt werden: jedes gezeigte Bild löst das nächste aus, bis zur Bildwiederholrate (direkt ~165/s, über RDP ~32/s). Ein Test, der Bilder 10 s lang nicht zeigte, bekam in dieser Zeit 11–13 statt ~1.600. Vermutlich hält das Zurückhalten aus ADR 0038 die Rückkopplung am Leben: Das Control verwarf ein Bild bei belegter Sperre und unterbrach sie damit. Auf dem Entwicklungsrechner (24H2) nicht nachzustellen. Backlog.
