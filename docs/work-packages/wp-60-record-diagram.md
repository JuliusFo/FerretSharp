# WP-60 Datensatz-Diagramm → Release 3.47.0

Branch `wp/60-record-diagram`, begonnen am 2026-10-23. ADR 0034. Baut auf der Traversierung aus WP-42 (`RowTraversal.FollowAsync`, `RowGraph`) und auf Layout und Darstellung aus WP-46 (`DiagramLayout`, `RelationsView`) auf.

## Planung (aus `docs/roadmap.md`)
- Von markierten Zeilen eines Tabs aus die verbundenen Datensätze als Diagramm: Knoten sind Zeilen (Tabellen-/Entity-Name, Schlüssel, Anzeigespalte über die Präsentationsschicht), Kanten die Beziehungen aus dem Graphen von WP-42, die diese Zeilen tatsächlich verbinden. Doppelklick lädt Eltern oder Kinder einer Kante nach, Klick öffnet den Tab mit Filter auf die Zeile.
- Laden über die Traversierung aus WP-42 (Leseweg der Workspace-Session, Blöcke mit `in`, abbrechbar), Darstellung und Layout aus WP-46. Viele Kinder an einer Kante werden zu einem Knoten zusammengefasst („Position ×200“), aufklappbar. Werte nur im Speicher.
- **Verbindung zum Kopieren (WP-42):** Knoten markieren und „Teilgraph kopieren“ mit diesen Zeilen als Start; die Kopier-Vorschau von WP-42 als Diagramm mit Zeilenzahl und Konflikten am Knoten.

## Entscheidungen des Nutzers (2026-10-23, Vorschläge 1–5 übernommen)
1. **Platz:** ein eigener Tab im Workspace (`RecordTab`). Er speichert Starttabelle, Schlüssel der Startzeilen und die Aufklapp-Schritte und liest die Zeilen beim Öffnen neu; Werte bleiben nur im Speicher.
2. **Griffe:** „+n“ zählt die noch nicht verfolgten **Beziehungen** einer Seite, nicht die Zeilen – eine `COUNT`-Abfrage je Beziehung kostet über das VPN zu viel. Eine Eltern-Beziehung zählt nur, wenn die Zeile dort Werte hat. Einzelne Beziehungen über das Kontextmenü. Kein Doppelklick (wie WP-46, `ui.md`).
3. **Grenzen:** mehr als 12 neue Zeilen über eine Beziehung einer Zeile werden zu einer Gruppe; höchstens 2 000 Zeilen je Diagramm.
4. **Kopieren:** Start aus markierten Zeilen **einer** Tabelle, der Dialog von WP-42 bleibt unverändert. Die Vorschau als Diagramm **ergänzt** die Tabelle (Umschalter im Dialog).
5. **Start aus SQL-Editor/LINQ-Konsole:** nicht jetzt (Backlog). **Mermaid:** ja, mit Werten, ohne eigene Sperre auf Prod.

## Gebaut
- **Core:**
  - `RecordDiagram` (Core/Relations): Startzeilen in Spalte 0, `ExpandAsync(rows, side)` folgt allen offenen Beziehungen einer Seite (eine Abfrage je Beziehung für alle Zeilen einer Tabelle), `ExpandAsync(row, relation, side)` einer. `Pending(row, side)` sind die Zahlen der Griffe. Ein Kind hat über seine Beziehung genau den Elternteil, von dem es kam – die Beziehung gilt bei ihm als verfolgt. Gruppen (`RecordGroup`, `Open`, `Members`), Kanten (`RecordEdge`) nur zwischen gezeigten Zeilen. `Steps` (`RecordStep`: Aufklappen einer Seite, einer Beziehung, Öffnen einer Gruppe) und `ReplayAsync` bauen das Diagramm neu; eine verschwundene Zeile oder Beziehung überspringt ihren Schritt.
  - Zu viele Zeilen über eine Beziehung: `RecordProblem` mit `TooMany`, die Beziehung wird nicht wieder angeboten. `RowTraversal.FollowAsync` liest dafür höchstens eine Zeile über die Grenze (`IDataAccess.ReadByKeysAsync(…, maxRows, …)`) und fügt bei Überschreitung nichts ein – vorher stand der halbe Teil im Graphen.
  - `RecordNode.Id`: Tabelle und Schlüssel als invarianter Text (`5` und `5.00` gleich), ohne U+0001 (Wegpunkt-Marke des Layouts).
  - `RecordKeys`: Schlüssel der Startzeilen als Text für die Workspace-Datei (Filtertexte, die exakt zurückgelesen werden; ROWID unverändert). Ein Schlüssel ohne exakten Text (BINARY_DOUBLE …) wird nicht gespeichert.
  - `RecordMermaid`: `flowchart LR`, Zeilen als Knoten mit ihrer Beschriftung, Kanten vom Elternteil zum Kind mit dem Namen der Beziehung, ohne Constraint gepunktet, Gruppen als ein Knoten.
  - `CopyPreviewDiagram`: die Tabellen eines Kopierplans, jede eine Spalte rechts ihres weitesten Elternteils (Reihenfolge des Einfügens), Kanten über die Beziehungen, die kopierte Zeilen verbinden.
  - `TabState.Records` (`RecordTabState`), `Workspace.Normalized` für Nullen von Hand.
- **UI:**
  - Zellmenü „Datensatz-Diagramm · n Zeilen“ › „Verbundene Datensätze zeigen“ (aus bei Views und bei Schlüsseln ohne exakten Text) → `ShellState.OpenRecords`, neuer Tab nach dem aktiven.
  - `RecordTab` (Titel „AUFTRAG 4711“ bzw. „AUFTRAG ×3“, Symbol `Icons.Records`), Speichern und Wiederherstellen, verschwindet mit seiner Tabelle (`RemoveVanishedTabs`).
  - `RecordDiagramState` (State/Schema): Laden (ein neues Diagramm klappt die Startzeilen nach beiden Seiten auf, ein gespeichertes spielt die Schritte ab), gegen die vorige Anordnung ausgelegt (F5 behält die Reihenfolge), ein gescheitertes oder abgebrochenes Laden lässt das gezeigte Diagramm stehen. „Zurücksetzen“, Zoom (`DiagramZoom`, mit WP-46 geteilt), markierte Zeilen (`MarkedTable` nur bei einer Tabelle).
  - `RecordDiagramView` (Components/Schema): Zeichnung wie `RelationsView` (`DiagramStyle` gemeinsam). Knoten mit Tabellen- bzw. Entity-Namen und „Schlüssel · Anzeigespalte“ über die Präsentationsschicht (Enum-Namen). Klick auf den Namen öffnet die Tabelle gefiltert auf die Zeile (wie ein FK-Sprung, nur mit PK), Klick auf die Zeile markiert sie, Griffe laden, Kontextmenü mit Öffnen, Markieren und den offenen Beziehungen je Seite. Lesen auf dem UI-Thread über `CallDbAsync` mit `RunProgress` (Zeit, Abbrechen, „Session trennen“); die angeklickte Zeile bleibt beim Wachsen stehen. Beziehungen über der Grenze stehen über der Zeichnung, mit „Im Tab zeigen“ (FK-Sprung von den Zeilen aus). Lädt neu, wenn sich die Beziehungen des Schemas wirklich ändern (C#-Modell kommt), nicht bei jeder neuen Liste.
  - Markierte Zeilen einer Tabelle → `SubgraphCopyDialog` (gemeinsamer Start `CopyTargets.Draft`/`Unavailable` mit dem Zellmenü).
  - Kopier-Vorschau: Umschalter „Tabelle | Diagramm“; im Diagramm je Tabelle Zeilen, neu, vorhanden und der Konfliktmodus, Rahmen in der Farbe des schwersten Befunds der Prüfung (Meldungen als Tooltip).
- **Tests:** Core `RecordDiagramTests`, `RecordKeysTests`, `SubgraphCopyPlannerTests` (Vorschau-Diagramm); UI `RecordDiagramStateTests`, `RecordTabTests`, `ShellActionsTests`.

## E2E (2026-10-23)
Eigener Container mit der Beispiel-DB (`ferret-e2e60`, danach entfernt), zwei Verbindungen darauf (DEV mit verknüpftem `samples`-Projekt, TEST ohne), Englisch und Deutsch, hell und dunkel.
- **Geprüft:**
  - Zellmenü „Record diagram · 1 row › Show connected records“ auf KUNDEN und AUFTRAG; neuer Tab „AUFTRAG 1“ mit Kunde (Constraint) und Mitarbeiter (C#-Modell, gestrichelt), Entity-Namen am Knoten.
  - Griffe „+n“ mit Zahl der Beziehungen; Aufklappen nach rechts, die angeklickte Zeile bleibt stehen. Zwei Gruppen „VERTRAG ×500“ und die Grenze (Auftrag.Bearbeiter > 2 000 Zeilen) mit „Im Tab zeigen“; eine Gruppe mit 500 Zeilen öffnet in unter 0,1 s.
  - Kontextmenü (Öffnen, Markieren, Beziehungen je Seite, „Alle Beziehungen“), einer Beziehung folgen, Klick auf den Namen öffnet KUNDEN gefiltert auf `KUNDE_ID = 8`.
  - Markieren per Klick, Kopie aus Zeilen zweier Tabellen gesperrt (mit Grund), aus einer Tabelle öffnet den Kopier-Dialog („1 row of AUFTRAG from E2E DEV“), Lesen, Vorschau als Diagramm mit Rahmenfarbe der Prüfung.
  - „Copy as Mermaid“ (mitgeschnitten), „Reset“, Neu-laden-Knopf (Anordnung bleibt), Wiederherstellen nach Neustart mit allen Schritten. Log ohne `[ERR]`/`[WRN]`.
- **Befunde, behoben:**
  1. F5 erreichte den Tab nicht: `ShellActions` ließ „Neu laden“ nur in Tabellen-, SQL-, LINQ- und PL/SQL-Tabs zu.
  2. Der letzte Schritt fehlte nach dem Neustart: `RecordTab.ToState` gab die Schrittliste des Diagramms selbst weiter, der gespeicherte Stand wuchs mit, und der Vergleich vor dem Speichern sah nie eine Änderung. Jetzt eine Kopie.
  3. Der Tooltip „Klick: markieren“ lag bei jedem Überfahren über dem Knoten und verdeckte Kanten – jetzt ein Hinweis in der Kopfzeile, solange nichts markiert ist.
  4. Zwei Gruppen derselben Tabelle waren nur am Tooltip zu unterscheiden – die Gruppe nennt ihre Beziehung.
  5. Die Meldung zur Grenze sagte nicht, von welcher Zeile aus – jetzt „Mitarbeiter 2: …“.
  6. Eine Beziehung ohne neue Zeilen zog nur den Griff herunter (+3 → +2) – jetzt der Hinweis „Dort gibt es keine weiteren Zeilen.“
  7. Beim Aufklappen stand „Lese die Startzeilen“ im Fortschritt, bis die erste Tabelle gemeldet war – jetzt „Lese Zeilen“.
  8. Kopier-Vorschau: „1 rows“, „(0 differing)“, drei Spalten breiter als der Dialog, und der Text war zentriert – die Klasse `copy-node` gab es im Dialog schon (Knoten des Beziehungsbaums). Einzahl, Abweichungen nur wenn vorhanden, schmalere Knoten, eigene Klasse `copy-table-node`.
- **Nicht geprüft:** echte Windows-Eingabe (alles per CDP, wie in WP-46: neu sind nur einfache Klicks, Rechtsklick und Hover); Abbrechen eines laufenden Lesens (lokal unter einer Sekunde).

## Review (2026-10-23), behoben
1. Fand F5 keine Startzeile (gelöscht in einem anderen Workspace, noch nicht committet), wurden die Schritte durch die leere Liste des neuen Diagramms ersetzt und gespeichert – nach einem Rollback standen nur noch die Startzeilen da. Die Schritte bleiben jetzt.
2. Ein Tab, gespeichert bevor sein erstes Laden fertig war (geschlossen, abgebrochen, Verbindung weg), hatte keine Schritte und kam ohne Eltern und Kinder zurück. Keine Schritte heißen jetzt „neues Diagramm“.
3. Das Neuladen ging über die Schrittliste des alten Diagramms; „Zeigen“ einer Gruppe war während des Lesens nicht gesperrt und hängte an dieselbe Liste an – die Schleife warf. Ursache wie beim E2E-Befund 2: `RecordDiagram.Steps` gab die Liste selbst heraus und ist jetzt eine Momentaufnahme (die Kopie in `RecordTab.ToState` entfällt).
4. Während eines Lesens sind auch „Zeigen“, Markieren, „Markierung aufheben“ und Mermaid aus.
5. Die Lesegrenze zählt den freien Platz und die Zeilen, die das Diagramm von der Zieltabelle schon hat: Ein fast volles Diagramm las vorher noch 2 001 Zeilen, eine Beziehung zu überwiegend bekannten Zeilen wurde abgewiesen.
6. Jede Zeile baute bei jedem Rendern (jedem Hover) ihre eigene `TablePresentation`; jetzt eine je Tabelle, bis sich C#-Modell oder Anzeigespalten ändern. Die Kopier-Vorschau wird einmal je Plan ausgelegt.
7. Farbe, Name und Tooltip einer Beziehungsart und die Hover-Zustände stehen einmal in `DiagramStyle` (Beziehungs- und Datensatz-Diagramm, Kopier-Vorschau).
8. Ein unsichtbares Steuerzeichen im Quelltext (`ShellState.OpenRecords`) ist jetzt die Escape-Folge ``.
