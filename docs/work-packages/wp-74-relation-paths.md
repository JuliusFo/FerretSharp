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
  - `RelationPathsState` (State/Schema): Ziel, Ausschlüsse, Wege in Reihenfolge, gewählter Weg, Diagramm. `MoreAsync` sucht einen Block auf einem Pool-Thread, `Collect` holt die gefundenen Wege auf den UI-Thread (die Ansicht alle 200 ms, so füllt sich die Liste während des Laufs). Neues Ziel, andere Beziehungen (Schema neu, Art umgeschaltet) oder andere Ausschlüsse starten eine neue Suche; der Block der alten endet ungesehen. `TargetChoices`: die Tabellen für „Weg zu …“ ohne den Start (bis `small-fk-target-picker` als `Candidates` mit eigener Suche, siehe Nachtrag).
  - `PathTargetPicker`: „Weg zu …“ über `TablePicker` (Suche wie die Schnellsuche: Name, Synonym, Entity-Name; Entity-Name statt PK neben der Tabelle), ✕ zurück zu den Nachbarn.
  - `RelationPathsPanel`: links neben dem Diagramm – Chips „Nicht über:“ (Klick lässt wieder zu), Stand der Suche mit „Abbrechen“ bzw. „Weitere Wege“, je Weg die Kette mit ↑/↓, Schritte, „1 : 1“/„1 : n“ (mit Tooltip) und ein Menü „Abfrage bauen“, „Als Text kopieren“, „Als Mermaid kopieren“. Überfahren hebt den Weg im Diagramm hervor, Klick wählt ihn.
  - `RelationsView`: Im Wege-Modus fallen Ebenen und „Zurücksetzen“ weg, die Arten gelten weiter (auch für die Suche), „Als Mermaid kopieren“ nimmt alle Wege. Kontextmenü eines Knotens: „Nicht über diese Tabelle“. „Abfrage bauen“ eines Wegs fragt bei vorhandener Abfrage „Ersetzen“ oder „Anhängen“, schaltet den Modus „Abfrage bauen“ ein (`QueryDesignEditor.TakePath`, das Ziel gewählt) und klappt das Nachbarschaftsdiagramm entlang des Wegs auf (`RelationsDiagramState.ShowPath`, Gruppen werden geöffnet). „Abfrage bauen“ aus → zurück zu den Wegen.
  - Gelöschte Verbindungen nehmen ihre Ausschlüsse mit (`ConnectionGroups`).
- **Tests:** Core `RelationPathsTests` (Reihenfolge, beide Richtungen, Selbstbezug, Ausschlüsse, parallele Beziehungen, Blöcke, Abbruch und Fortsetzen, 29 Schritte, vollständiger Graph mit 65 Wegen, Text, Abfrage), `PathDiagramTests`, `SettingsStoreTests` (Round-Trip, `null` von Hand); UI `RelationPathsStateTests`, `ConnectionGroupsTests`.

## E2E
2026-11-06, eigener Container (`New-SampleDb.ps1 -Name ferret-e2e-paths -Port 1536`), dazu eine Sammeltabelle `E2E_BENUTZER` (FK aus KUNDEN, AUFTRAG, ARTIKEL, HERSTELLER), eine Kette `E2E_K01` … `E2E_K12` ab AUFTRAG und ein vollständiger Teilgraph `E2E_D1` … `E2E_D9` (13 700 Wege zwischen D1 und D9). `--lang=de`, dunkel und hell; Klicks, Rechtsklicks und Hover mit echter Windows-Eingabe (`Invoke-RealClick.ps1`, Probe vorher bestanden), Tippen per CDP, Zwischenablage per CDP mitgeschnitten.
- Geprüft: LIEFERUNG → E2E_K12 (7 Wege, 14 bis 18 Schritte, „alle gefunden“); „Nicht über diese Tabelle“ im Kontextmenü (1 Weg, Chip, `settings.json` mit Owner und Name) und Zulassen über den Chip; Hervorhebung beim Überfahren; „Abfrage bauen“ (17 Knoten, Ziel gewählt, Nachbarschaft entlang des Wegs aufgeklappt), aus und wieder an, „Ersetzen“/„Anhängen“ (31 Knoten); E2E_D1 → E2E_D9 in Blöcken zu 50 mit „Weitere Wege“; Ziel nach Neustart wiederhergestellt; „Als Text kopieren“, „Als Mermaid kopieren“ (36 Beziehungen); Auswahl des Ziels mit ↓ und Enter. Log ohne `[ERR]`/`[WRN]`.
- Gefunden, behoben: Das Menü „Weg zu …“ öffnete nach links über den Explorer (`ShiftX`) und schob sich mit der langen Liste über den Knopf (Liste scrollt jetzt, höchstens 300 px).
- Nicht geprüft: „Abbrechen“ in der Oberfläche – ein Block ist im Beispielschema in Millisekunden fertig (Abbrechen und Fortsetzen nur in `RelationPathsTests`/`RelationPathsStateTests`).
- Werkzeug: `Start-TestInstance.ps1` beendete die eigene Instanz wieder als fremd (Backlog, ergänzt); von Hand gestartet.
- Offene UX-Frage: „Weitere Wege“ steht über der Liste, nach dem Scrollen ans Ende also außer Sicht. Zusätzlich am Ende der Liste?

## Nachtrag: Code-Review (2026-11-06)
Review per `/branch-code-review` (eigener Reviewer-Agent), Entscheidung des Nutzers: „Alle beheben“; für die weiteren Durchgänge: „Wenn dir bei den nachfolgenden Reviews Dinge auffallen, die behoben werden müssen, setze deine Empfehlung um.“
1. S `Collect` ersetzte die Liste, in die der laufende Block schrieb – nach dem ersten Sammeln (alle 200 ms) gingen alle Wege des Blocks verloren, das Durchlaufen konnte auf „Collection was modified“ treffen. **Behoben** (Liste unter der Sperre leeren), Test `Collecting_while_a_block_runs_loses_no_way` (ohne Fix 20 statt 1000 Wege).
2. M Jeder Build des C#-Projekts startete die Suche neu: `ForeignKeyInfo` vergleicht seine Spaltenlisten nach Referenz. **Behoben** (`RelationGraph.SameRelations` nach Inhalt), Test mit kopierten Listen.
3. M Hover und der 200-ms-Takt renderten die ganze Liste samt Tooltip-Texten und Menüs neu. **Behoben:** `RelationPathRow` rendert nur bei neuem Weg, neuer Auswahl oder neuen Namen, Text und Menü einmal je Weg; der Takt rendert nur bei neuen Wegen oder neuer Länge; die Kanten des aktiven Wegs zwischengespeichert. Nicht gemessen, im E2E mit 150 Wegen Hover, Auswahl und Menü geprüft.
4. K Die Abstände liefen durch den Start, „alle gefunden“ kam erst Längen nach dem letzten Weg. **Behoben** (die Breitensuche geht über den Start nicht weiter), Test (ohne Fix Länge 5 statt 1).
5. K Der Weg unter der Maus blieb nach F5 oder einem Build hervorgehoben. **Behoben** (`LoadPaths` verwirft ihn, wenn er nicht mehr unter den Wegen ist).
6. K Zwei Beziehungen des C#-Modells mit gleichem Namen an einer Tabelle ergaben dieselbe `RelationPath.Key` (doppelter `@key`). **Behoben** (Zieltabelle, FromColumns und – im zweiten Durchgang – ToColumns in der Key), Tests.
7. K `docs/architecture.md` ohne `PathAvoidedTables` und `TabState.PathTarget`. **Behoben.**
- Zweiter Durchgang: alle sieben Fixes bestätigt; zwei Reste, nach Empfehlung behoben – `ToColumns` in der Key (siehe 6) und die Zeile baut ihren Text neu, wenn sich die Tabellenliste des Schemas ändert (ein Synonym kam oder ging, die Wege bleiben).
- Dritter Durchgang (nur diese beiden Fixes): ohne Befund.
- Vorher schon da, unverändert: Beziehungen mit derselben `KeyOf` teilen im Diagramm eine Kante (wie im Nachbarschaftsdiagramm).

## Nachtrag: gemeinsame Tabellenauswahl (2026-11-06)
Aus dem Code-Review von `small-fk-target-picker` (Entscheidung des Nutzers: „Lass uns mal alle mitmachen, auch 3.“): Die Zieltabelle eines neuen FK im Designer und „Weg zu …“ hatten je eine eigene Auswahl mit Suche – verschiedene Rangfolge (`ObjectSearch.FindTables` gegenüber einer eigenen über `NameRank`), Grenze (100 mit „N weitere“ gegenüber 30 ohne Hinweis), Leertext und Bedienung. „Weg zu …“ nutzt jetzt `TablePicker` (`ButtonContent`, `Class`, `Title`, `SearchPlaceholder`, `ShowKeys=false`); `RelationPathsState.Candidates`/`CandidateLimit` und die `.path-candidate*`-Klassen sind entfernt, den Start nimmt `TargetChoices` heraus. Geändert für den Nutzer: Enter im Suchfeld nimmt den ersten Treffer, nach der Wahl hat der Knopf den Fokus, ohne Suche stehen die Tabellen in Listenreihenfolge, das Menü öffnet unter dem Knopf ab seiner linken Kante (über ihm, wenn unten kein Platz ist) statt an der Maus. E2E mit echter Maus: Lage am Kopf, Start nicht in der Liste, Enter, ✕; nicht geprüft: schmales Fenster.
