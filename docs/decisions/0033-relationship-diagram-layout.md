# ADR 0033: Beziehungsdiagramm mit eigenem Schichten-Layout

- Status: akzeptiert
- Datum: 2026-10-23
- Paket: WP-46

## Kontext

WP-46 zeigt die Nachbarschaft einer Tabelle als Diagramm, WP-60 danach konkrete Datensätze in derselben Darstellung. Beide brauchen ein Layout, das schrittweises Erweitern trägt (ein Knoten bekommt Nachbarn, der Rest bleibt, wo er war) und zusammengefasste Knoten („45 Tabellen“, „Position ×200“) kennt. Zur Wahl standen eine Layout-Bibliothek wie elkjs (lokal unter `wwwroot/lib`, Rechnung in JS) oder ein eigenes Layout im Core. Der Stack verbietet Geschäftslogik in JS; eine Bibliothek, die beim Erweitern alles neu anordnet, ließe sich für WP-60 nur schwer bändigen.

## Entscheidung

**Eigenes Schichten-Layout im Core** (`Core/Relations/DiagramLayout`), ohne neue Abhängigkeit (Entscheidung des Nutzers, 2026-10-23):
- **Spalten nach Richtung:** Die Schicht eines Knotens legt sein Aufrufer fest – Eltern (referenzierte Tabellen) links, Kinder rechts, je Ebene eine Spalte. Beim Erweitern kommen neue Knoten eine Spalte weiter nach außen; vorhandene behalten ihre Spalte, auch wenn sie später über einen anderen Weg erreichbar wären.
- **Reihenfolge in der Spalte:** beim ersten Layout die Baryzentrum-Heuristik (vier Durchläufe abwechselnd nach rechts und links). Beim Wachsen behalten die vorhandenen Knoten ihre Reihenfolge, ein neuer Knoten wird auf Höhe der Mitte seiner schon platzierten Nachbarn eingeordnet.
- **Höhe:** Die größte Spalte wird gestapelt, jede weitere stellt ihre Knoten auf die Höhe ihrer Nachbarn, soweit die Knoten darüber es zulassen, und rückt danach als Ganzes nach oben.
- **Kanten über mehrere Spalten** bekommen in jeder Spalte dazwischen eine Spur, einen Hilfsknoten so hoch wie der Abstand zwischen Knoten, der wie ein Knoten geordnet und platziert wird. Die Kante läuft so an den Knoten dieser Spalte vorbei statt durch sie hindurch (im E2E gefunden: RECHNUNG → KUNDEN lief mitten durch AUFTRAG).
- **Kanten** als kubische Bézier-Kurven von der rechten Seite des linken Knotens zur linken Seite des rechten, durch die Spuren gerade hindurch; innerhalb einer Spalte als Bogen rechts, ein Selbstbezug als Schleife. Mehrere Kanten zwischen denselben Knoten liegen nebeneinander.
- Das Layout kennt nur Knoten-IDs, Schichten und Kanten, keine Tabellen. WP-60 legt Zeilen damit aus.

**Zeichnen in Razor:** Knoten als HTML-Elemente über einem SVG mit den Kanten, ohne JS außer Scrollen und Zwischenablage. So greifen Ellipsis, Fokus, `title`-Tooltips (`tooltip.js`) und der Dunkelmodus über Tokens wie überall. Zoom über `transform: scale` der Zeichenfläche.

**Zustand im Core, Schritte in der UI:** `RelationDiagram` hält, welche Tabellen in welcher Spalte stehen, welche Seiten aufgeklappt und welche Nachbarn zusammengefasst sind (mehr als 12 neue auf einer Seite). `RelationsDiagramState` (UI) merkt sich Aufklappen und Öffnen von Gruppen als Schritte und spielt sie nach F5, neu geladenem Schema oder einer umgeschalteten Art von Beziehung erneut ab.

## Folgen

- Keine Bibliothek, kein ADR zum Stack; das Layout ist einfach und vorhersagbar, aber kein volles Sugiyama: Kreuzungen werden nur heuristisch verringert, Kanten innerhalb einer Spalte nehmen keine Rücksicht auf Knoten dazwischen. Für die Nachbarschaft einer Tabelle (1–2 Ebenen) reicht das; wächst das Diagramm stark, ist „In die Mitte stellen“ der Weg zu einem frischen Layout.
- Mermaid (`RelationMermaid`) gibt das Diagramm als `erDiagram` aus. Deklarierte Beziehungen sind durchgezogen, andere gestrichelt. Mermaid legt selbst aus, das eigene Layout geht nicht mit.
