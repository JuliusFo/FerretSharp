# ADR 0035: Icons aus Lucide, als Pfade in `Icons.cs`

- Status: akzeptiert
- Datum: 2026-10-25
- Paket: Klein (`feat/icons`)

## Kontext

Alle Icons standen seit den ersten Commits als handgezeichnete Inline-SVGs in `Components/Common/Icons.cs` (ViewBox 16, Strich je Icon 1,4 bis 1,8). WP-61 brachte neun dazu, die Schnellleiste des Zellmenüs zeigt nur Icons mit der Bedeutung im Tooltip. Einige Motive waren schwer zu unterscheiden (Link gegen LinkView), „Vergleichen“ (Zeilen) nutzte das Icon des Schema-Vergleichs. „Keine Icon-Bibliothek“ war keine Entscheidung des Nutzers, sondern aus dem Bestand übernommen.

Geprüft (Übersicht mit allen Icons hell und dunkel und den Kandidaten je Motiv):
- **Eigene behalten, schwache nachzeichnen:** keine Lizenzfrage, aber jedes neue Motiv wieder von Hand – bei LinkView, Records und Form hatte das schon nicht gereicht.
- **Lucide** (ISC, Teile aus Feather unter MIT, über 1.500 Motive) und **Tabler** (MIT, über 5.000): ViewBox 24, Strich 2. In 14 px ist die Linie 1,17 px, die eigenen hatten 1,23 px – die Pfade passen unverändert. Lucide ist im Stil (runde Enden, Feather-Familie) am nächsten am Bestand, Tabler eckiger.
- **Fluent System Icons:** Das NuGet-Paket `Microsoft.FluentUI.AspNetCore.Components.Icons` (5.0.1, MIT) hängt von `Microsoft.FluentUI.AspNetCore.Components` ab, der ganzen Komponentenbibliothek mit CSS und JS – die Stack-Regel schließt das aus. Die SVGs selbst sind Flächen mit fester Linie (in 14 px 0,88 px), nicht nachstellbar und nicht mit Strich-Icons zu mischen.
- **Webfont** (Lucide, Tabler): eine weitere Schrift mit Hunderten ungenutzter Glyphen für gut 30 Icons.

## Entscheidung

- **Alle Icons kommen aus Lucide** (Entscheidung des Nutzers), Version `lucide-static` 1.55.0. Die Elemente jedes SVGs stehen unverändert in `Icons.cs`, der Lucide-Name in der Summary. Einzige Anpassung: `SelectColumn` (`columns-3`) bekommt die mittlere Spalte gefüllt, sonst liest es sich als Layout-Schalter.
- **Keine Laufzeit-Abhängigkeit:** kein Paket, kein CDN, keine Schrift. `Svg()` setzt ViewBox 24 und Strich 2 für alle Icons; die Darstellungsgröße (14 px, im Objektkopf 18, in Chips 12) bleibt beim CSS.
- **Lizenz:** `wwwroot/lib/lucide/LICENSE.txt` (ISC und der MIT-Text für die Feather-Anteile) mit `README.md` zu Version und Quelle, wie bei AG Grid und Monaco; der Ordner wird mit der App ausgeliefert. Die README des Repos nennt Lucide im Abschnitt „License“.
- **Neue Icons nur aus derselben Quelle und Version**; ein Motiv, das Lucide nicht hat, wird im Stil von Lucide gezeichnet (24er-Raster, Strich 2) und in der Summary als eigenes markiert.
- **Motive mit Bedeutung:** Zeilen vergleichen `git-compare` (neu, `Icons.CompareRows`), Schema-Vergleich `file-diff` (`Icons.SchemaCompare`, das Ergebnis ist ein Skript), Link auf Ansicht `bookmark` statt einer zweiten Kette, Formular `notebook-text`, Datensätze `workflow`. Das ungenutzte `Key` entfällt.

## Folgen

- Ein Update von Lucide ändert nichts von selbst: Pfade werden bewusst neu kopiert, README und Summary nachgezogen.
- Textzeichen, die als Icon dienten, sind ebenfalls Lucide (Entscheidung des Nutzers im selben Branch): ✕ als `Close` (`x`), ▾/▸ als `ChevronDown`/`ChevronRight`, ⋯ als `More` (`ellipsis`), ↑ ↓ ← → als `Arrow*`, ↶ ↺ ↷ als `Undo`/`Redo`, ▲ ▼ des Formulars als `ChevronUp`/`ChevronDown`, ✎ als `Rename`, ▶ ▶▶ als `Run`/`RunScript` (deshalb die Prozedur `square-play` statt `play`), ⚠ als `Warning`, ⎇ als `Branch`, ⤴ als `ShowRow`, ○ als `Empty`, ⧉ als `Copy`. Das gilt für ein Zeichen, das allein einen Knopf füllt oder als Markierung vor einer Zeile steht (Chevron, Sprungrichtung, Pfeil zwischen altem und neuem Wert). Pfeile im laufenden Text bleiben Schrift: „FK → KUNDEN“, „→ OWNER.NAME“ eines Synonyms, Tooltips und resx-Sätze, Sortiermarken in Spaltenköpfen und Fußzeile.
