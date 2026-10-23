# WP-46 Beziehungsdiagramm → Release offen

Branch `wp/46-relationship-diagram`, begonnen am 2026-10-23. ADR 0033. Baut auf dem Beziehungsgraphen aus WP-42 auf; das Datensatz-Diagramm (WP-60) nutzt Layout und Darstellung von hier.

## Planung (aus `docs/roadmap.md`)
- Nachbarschaft einer Tabelle über 1–2 Ebenen als Diagramm, Kanten nach `FkSource` gefärbt (deklariert, C#-Modell, später manuell/vermutet), Knoten mit Tabellen- und Entity-Namen; Klick öffnet den Tab, Doppelklick erweitert den Knoten. „Als Mermaid kopieren“ für Doku und Tickets.
- Das Layout muss schrittweises Erweitern und zusammengefasste Knoten tragen („Position ×200“, aufklappbar), weil WP-60 darauf aufbaut.

## Entscheidungen des Nutzers (2026-10-23)
1. **Platz:** eine neue Ansicht „Beziehungen“ im Tabellen-Tab neben „Abhängigkeiten“ (`TabMode.Relations`, mit dem Tab gespeichert). Was aufgeklappt ist, bleibt nur im Speicher.
2. **Klicks:** Ein Klick auf den Namen öffnet den Tab (wie im Explorer). Kleine Griffe „+n“ links und rechts am Knoten zeigen die Eltern bzw. Kinder. Das Kontextmenü stellt einen Knoten in die Mitte. Klick und Doppelklick aus der Roadmap hätten sich gebissen: Vor jedem Doppelklick feuert der Klick, und im WPF-Host gibt es ohnehin kein `dblclick` (`ui.md`).
3. **Richtung:** links nach rechts, also Eltern → Tabelle → Kinder. Viele Kinder ergeben eine Spalte zum Scrollen statt einer sehr breiten Zeile.
4. **Standard:** 1 Ebene. Mehr als 12 neue Nachbarn auf einer Seite werden zu einem Knoten zusammengefasst.
5. **Layout:** ein eigenes Layout im Core statt einer Bibliothek (ADR 0033).
6. WP-46 kommt zusammen mit WP-42 ins nächste Release.

## Gebaut
- **Core:**
  - `DiagramLayout` (Core/Relations): ein allgemeines Schichten-Layout über Knoten-IDs. Spalten nach Schicht, Baryzentrum beim ersten Layout, stabile Reihenfolge beim Wachsen (die vorige `DiagramPlacement` geht hinein), Höhe nach den Nachbarn. Kanten über mehrere Spalten laufen durch eine eigene Spur je Spalte dazwischen (Hilfsknoten, nicht in `Nodes`). Kanten als Folge von Bézier-Kurven (`EdgePath.Points`: Start, dann je Kurve zwei Kontrollpunkte und ein Ende; `Svg`), mit Schleife für den Selbstbezug, Bogen innerhalb einer Spalte und nebeneinanderliegenden Enden bei mehreren Kanten zwischen denselben Knoten.
  - `RelationDiagram`: Fokus, gezeigte Tabellen mit Spalte, `Expand(table, side)`, `HiddenCount` (Zahl am Griff) und Gruppen (`DiagramGroup`, `Open`, `Members` ohne inzwischen gezeigte Tabellen). Bei Tiefe 2 kommen die Eltern der Eltern und die Kinder der Kinder dazu, nicht die anderen Eltern der Kinder. Filter nach `FkSource` (`DiagramOptions`). `ToLayout` lässt Kanten vom Elternteil zum Kind laufen, eine Gruppe hängt an ihrer Tabelle.
  - `RelationMermaid`: `erDiagram` mit `PARENT ||--o{ CHILD : "NAME"`. Deklarierte Beziehungen sind durchgezogen, andere gestrichelt (`..`). Tabellen anderer Schemas stehen mit Owner da, Namen außerhalb von `[A-Za-z0-9_-]` in Anführungszeichen. Eine Tabelle ohne gezeigte Beziehung bekommt eine eigene Zeile, nicht geöffnete Gruppen einen Kommentar (`RelationText.MermaidHidden*`).
- **UI:**
  - `RelationsDiagramState` (State/Schema): Fokus, Tiefe, ausgeblendete Arten, Zoom-Stufen. Aufklappen und Öffnen von Gruppen werden als Schritte gemerkt und nach `Load` (F5, neu geladenes Schema, neues C#-Modell) sowie nach dem Umschalten einer Art erneut abgespielt. „In die Mitte stellen“ und „Zurücksetzen“ beginnen neu.
  - `RelationsView` (Components/Schema):
    - Kopfzeile mit Ebenen 1 | 2 und den Arten der Beziehungen des Schemas als Legende (abschaltbar, sobald es mehrere gibt), Zoom −/100 %/+, „Zurücksetzen“ und „Als Mermaid kopieren“.
    - Zeichenfläche: Knoten als HTML über einem SVG. Kanten in den Farben `--accent` (Constraint), `--clr` (C#-Modell) und `--warn` (andere), alles außer Constraints gestrichelt, ein Punkt am referenzierenden Ende. Der Tooltip einer Kante nennt Name, Spalten und Art.
    - Beim Überfahren eines Knotens treten seine Kanten und Nachbarn hervor, der Rest wird blasser. Kontextmenü: Öffnen, In die Mitte stellen, Eltern/Kinder zeigen.
    - Der Fokus wird beim Öffnen und nach „In die Mitte“ ins Bild gescrollt. Beim Aufklappen bleibt der angeklickte Knoten auf dem Bildschirm, wo er war: Die Zeichenfläche scrollt um seine Verschiebung mit (`dom.js scrollByPx`), sonst rückte eine neue Spalte links alles unter der Maus weg.
    - Die Griffe „+n“ sitzen an den oberen Ecken, in der Mitte der Seite verdeckten sie die Kantenansätze. Die Ansicht folgt `PresentationService.Changed`: Kommt das C#-Modell, ändern sich Entity-Namen und Beziehungen.
- **Tests:** Core `DiagramLayoutTests`, `RelationDiagramTests`, `RelationMermaidTests`; UI `RelationsDiagramStateTests`.

## E2E (2026-10-23)
Eigener Container mit der Beispiel-DB, verknüpftes `samples`-Projekt (C#-Modell geladen), dazu 14 Hilfstabellen mit FK auf eine Stammtabelle für die Gruppe.
- **Geprüft:**
  - AUFTRAG mit Eltern KUNDEN/MITARBEITER (C#-Beziehung gestrichelt) und Kindern AUFTRAG_POSITION/RECHNUNG.
  - Hervorhebung beim Überfahren, Kanten-Tooltip, Griffe „+1“ (Tooltips), Aufklappen nach links und rechts.
  - Kontextmenü „In die Mitte stellen“, Ebene 2 mit Bogen RECHNUNG → AUFTRAG innerhalb einer Spalte, Mermaid in die Zwischenablage (mitgeschnitten).
  - Gruppe „14 Tabellen“ → „Zeigen“; F5 behält die geöffnete Gruppe, „Zurücksetzen“ klappt sie zu.
  - Abschalten einer Art (nur Constraints aus → nur die C#-Beziehung bleibt), Zoom 125 %, Dunkelmodus lesbar.
  - Klick auf einen Namen öffnet den Tab in der Datenansicht. Log ohne `[ERR]`/`[WRN]`.
- **Befunde, behoben:**
  1. Kanten über zwei Spalten liefen durch den Knoten dazwischen und sahen aus wie eine zweite Kante zu ihm – Spuren im Layout (ADR 0033).
  2. Die Griffe verdeckten die Kantenansätze – an die oberen Ecken.
  3. Eine neue Spalte links schob das Diagramm unter der Maus weg – der angeklickte Knoten bleibt stehen.
  4. Die Hervorhebung blieb nach dem Verlassen eines Knotens, bis die Maus die Fläche verließ – `mouseleave` am Knoten.
  5. Der Tooltip einer Kante sagte „– Constraints“ (die Mehrzahl der Legende) – eigener Text in der Einzahl.
- **Nicht geprüft:** echte Windows-Eingabe. Per `SendInput` kamen weder Bewegungen noch Klicks im WebView an (wie in `docs/e2e-testing.md` beschrieben), alles lief per CDP. Neu sind nur einfache Klicks, Rechtsklick und Hover, kein Doppelklick und kein Ziehen.
