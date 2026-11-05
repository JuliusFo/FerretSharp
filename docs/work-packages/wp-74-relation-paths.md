# WP-74 Wege zwischen zwei Tabellen → Release offen

Branch `wp/74-relation-paths` (von `main`), 2026-11-06. ADR 0044. Baut auf WP-46 (Beziehungsdiagramm) und WP-69 (Abfrage aus dem Diagramm).

## Planung (aus `docs/roadmap.md`)
- **Idee des Nutzers (2026-11-06):** Tabelle A und B wählen, die Ansicht zeigt alle Ketten von Beziehungen, über die man von A nach B kommt. Jeder Schritt geht zum Elternteil (↑, n:1) oder zu einem Kind (↓, 1:n).
- **Entscheidungen des Nutzers (2026-11-06):**
  1. **Platz:** ein Modus der Ansicht „Beziehungen“: „Weg zu …“ im Kopf, die Tabelle des Tabs ist A.
  2. **Keine feste Grenze** für Länge und Zahl der Wege (lange Wege sind beim Nutzer üblich). Kürzeste zuerst, nach und nach, im Hintergrund, abbrechbar, „Weitere Wege“ setzt fort.
  3. **„Nicht über diese Tabelle“** in der ersten Stufe, je Verbindung gespeichert.
  4. **„Abfrage bauen“** mit einem Weg gehört ins Paket.
- Beim Start entschieden (Plan, vom Nutzer angenommen): Blöcke zu 50, danach hält die Suche an; das Ziel wird mit dem Tab gespeichert, die Wege nicht.

## Gebaut
- **Core:**
  - `RelationPathSearch` (Core/Relations, ADR 0044): Breitensuche vom Ziel für die Abstände, dann Tiefensuche Länge für Länge mit eigenem Stapel – `Next(count, ct)` gibt den nächsten Block, ein Abbruch lässt den Stand stehen. `Length`, `Found`, `Done` für den Fortschritt. Selbstbezüge und ausgeschlossene Tabellen bleiben draußen (Start und Ziel nie).
  - `RelationPath` / `PathStep`: Schritte mit Richtung, `SingleRow` („1 : 1“), `SourceRank`, `Key`, `Compare` (Länge, 1 : 1 vor 1 : n, Art), `Text` (eine Zeile je Schritt mit Spalten, mehrspaltig als Tupel), `ToQuery` (ein Knoten je Teilweg, in eine bestehende Abfrage angehängt oder neu).
  - `PathDiagram`: alle Wege als Layout für `DiagramLayout` – Start links, Ziel rechts, jede Tabelle in der Spalte ihres kürzesten Abstands vom Start; Kanten vom Elternteil zum Kind wie in WP-46, ein Elternteil darf rechts vom Kind stehen.
  - `RelationMermaid.Write(paths, label)`: die Beziehungen der Wege, jede einmal.
  - `TableListMap` (Core/Settings), `AppSettings.PathAvoidedTables`: Verbindungs-ID → Tabellen (Owner und Name getrennt), `null` von Hand beim Laden leer.
  - `TabState.PathTarget`: das Ziel mit dem Tab gespeichert.
- **UI:**
  - `RelationPathsState` (State/Schema): Ziel, Ausschlüsse, Wege in Reihenfolge, gewählter Weg, Diagramm. `MoreAsync` sucht einen Block auf einem Pool-Thread, `Collect` holt die gefundenen Wege auf den UI-Thread (die Ansicht alle 200 ms, so füllt sich die Liste während des Laufs). Neues Ziel, andere Beziehungen (Schema neu, Art umgeschaltet) oder andere Ausschlüsse starten eine neue Suche; der Block der alten endet ungesehen. `Candidates`: die Tabellen für „Weg zu …“ wie die Schnellsuche (Name, Synonym, Entity-Name).
  - `PathTargetPicker`: „Weg zu …“ mit Suchfeld im Menü, ✕ zurück zu den Nachbarn.
  - `RelationPathsPanel`: links neben dem Diagramm – Chips „Nicht über:“ (Klick lässt wieder zu), Stand der Suche mit „Abbrechen“ bzw. „Weitere Wege“, je Weg die Kette mit ↑/↓, Schritte, „1 : 1“/„1 : n“ (mit Tooltip) und ein Menü „Abfrage bauen“, „Als Text kopieren“, „Als Mermaid kopieren“. Überfahren hebt den Weg im Diagramm hervor, Klick wählt ihn.
  - `RelationsView`: Im Wege-Modus fallen Ebenen und „Zurücksetzen“ weg, die Arten gelten weiter (auch für die Suche), „Als Mermaid kopieren“ nimmt alle Wege. Kontextmenü eines Knotens: „Nicht über diese Tabelle“. „Abfrage bauen“ eines Wegs fragt bei vorhandener Abfrage „Ersetzen“ oder „Anhängen“, schaltet den Modus „Abfrage bauen“ ein (`QueryDesignEditor.TakePath`, das Ziel gewählt) und klappt das Nachbarschaftsdiagramm entlang des Wegs auf (`RelationsDiagramState.ShowPath`, Gruppen werden geöffnet). „Abfrage bauen“ aus → zurück zu den Wegen.
  - Gelöschte Verbindungen nehmen ihre Ausschlüsse mit (`ConnectionGroups`).
- **Tests:** Core `RelationPathsTests` (Reihenfolge, beide Richtungen, Selbstbezug, Ausschlüsse, parallele Beziehungen, Blöcke, Abbruch und Fortsetzen, 29 Schritte, vollständiger Graph mit 65 Wegen, Text, Abfrage), `PathDiagramTests`, `SettingsStoreTests` (Round-Trip, `null` von Hand); UI `RelationPathsStateTests`, `ConnectionGroupsTests`.

## E2E
(folgt)
