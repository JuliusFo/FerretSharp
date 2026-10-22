# Roadmap – geplante Pakete und offene Fragen

Was als Nächstes kommt, mit den Entscheidungen des Nutzers. Beim Start eines Pakets den Abschnitt lesen und den Plan mit dem Nutzer abstimmen. Ist das Paket released, wandert sein Abschnitt als „Planung“ in die Paketdatei unter `docs/work-packages/` (dort auch alle abgeschlossenen Pakete). Nicht eingeplante Ideen: `docs/backlog.md`.

## GitHub-Issues und Labels

Entscheidung des Nutzers (2026-10-09). Issues bekommen eine Art und einen Status.
- **Art:** `enhancement`, `bug`, `documentation`, `question`. Optional der Bereich: `area: grid`, `area: sql-editor`, `area: linq` oder `area: schema`.
- **Status**, je Issue genau einer:
  - `needs decision`: Das Issue hat offene Fragen. Die Antworten des Nutzers kommen beim Start des Pakets hierher in den Abschnitt des Pakets, danach wird das Label entfernt.
  - `planned`: Das Issue ist als Paket eingeplant. In der Überschrift des Pakets hier steht der Link zum Issue.
  - `done`: Das Issue ist umgesetzt und released. `planned` wird entfernt, dazu kommt ein Abschlusskommentar mit Version, Entscheidungen und dem, was offen bleibt (Backlog). Dann wird das Issue geschlossen.
- Labels setzen dürfen nur Leute mit Triage-Rechten. Wer ein Issue anlegt, setzt in der Regel keine Labels; das geschieht beim Einplanen.

## Geplant

Versionen: Minor-Releases 3.x (nichts Inkompatibles). Ein Paket bekommt seine Version erst beim Release (`CHANGELOG.md`, Spalte „Release“ im Index); die Roadmap legt keine fest, weil die Reihenfolge oft abweicht und Fix-Releases dazwischenkommen (Entscheidung des Nutzers, 2026-10-14). Der Release-Stand steht im Kopf der `CLAUDE.md`, die Historie im `CHANGELOG.md`.

### Pakete nach WP-36 (WP-37 … WP-47)
Ausgewählt mit dem Nutzer am 2026-10-14 aus einer Ideensammlung (Bewertung des Nutzers je Paket in Klammern). Reihenfolge nach Abhängigkeiten: erst kleine Pakete mit sofortigem Nutzen (WP-37 … WP-39), dann zwei Fundamente, die je mehrere Pakete tragen – der Code-Index (WP-40 → WP-41, WP-45) und der Beziehungsgraph (WP-42 → WP-43, WP-46) –, das Paket mit Mitschnitt, Quellzeile und Datenänderungen (WP-45) erst, wenn seine Teile stehen. Die Abschnitte sind Vorschläge; „Offen“ wird beim Start mit dem Nutzer entschieden, dann wie üblich der Plan.

### WP-55 Spaltenprofil
(„gut“; aus WP-38 herausgelöst, 2026-10-19 – gleicher Platz in der Reihenfolge. WP-38 ist umgesetzt: `docs/work-packages/wp-38-palette-quick-open.md`.) Branch `wp/55-column-profile`.
- **Spaltenprofil:** im Header-Menü (`ColumnMenu`) „Werte zählen“: häufigste Werte mit Anzahl (Top N), NULL-Anteil, Anzahl verschiedener Werte, Min/Max – unter dem aktiven Filter, in einer Abfrage aus dem `QueryBuilder` (`GROUP BY … ORDER BY COUNT(*) DESC FETCH FIRST n`), auf dem Leseweg der Workspace-Session (abbrechbar, Laufzeit sichtbar – große Tabellen über VPN). Werte über die Präsentationsschicht (Enum-Namen, Anzeigespalten). Klick auf einen Wert fügt eine Filterzeile `=` hinzu. Nicht für LOB/LONG.
- Offen: Top N (Vorschlag 20) und ob „Weitere laden“; Profil auch im Ergebnis von SQL-Editor/LINQ-Konsole (Vorschlag: nein, dort fehlt das Filtermodell); Klick auf einen Wert mit Shift „als ODER zur letzten Gruppe“ (Filtergruppen seit WP-50).

### WP-39 Workspaces anheften und wechseln
(Aus dem Backlog, dort als ein Paket vorgeschlagen.) Branch `wp/39-workspace-pins`.
- Workspaces pinnen, Varianten aus dem Backlog: a) fester Platz vorn in der Leiste, b) Schutz vor versehentlichem Schließen/Löschen, c) beim Verbinden immer mit öffnen; d) über Verbindungen hinweg ist größer (ein Workspace gehört heute genau einer Verbindung) – Vorschlag a–c, d bleibt im Backlog.
- Workspace-Übersicht je Verbindung (Tabs, offene Transaktion, zuletzt benutzt, Notizen) und Wechseln per Tastenkürzel.
- Offen: welche der Varianten a–c; Ctrl+1 … 9 ist im Backlog für das Wechseln reserviert, aber neue Aktionen bekommen keine Standardtaste (`docs/architecture.md` 3) – Ausnahme dafür oder vom Nutzer belegen lassen; Oberfläche für `Workspace.Notes` mit in dieses Paket?

### WP-41 Designer-Änderungen ins C#-Projekt
Branch `wp/41-designer-to-code`. Baut auf WP-40 (released 3.43.0, ADR 0030): dessen Index der Verwendungen und der aktive Arbeits-Klon je Gruppe tragen WP-41 und WP-45.
- Nach einer Änderung im Tabellen-Designer (WP-23) die passende Änderung an Entity-Klasse und `IEntityTypeConfiguration` vorschlagen: Spalte hinzu → Property und Konfiguration im Stil des Projekts (`PropertyCode`, `EntityCode`), Länge/NULL geändert → Facetten anpassen, Spalte gelöscht → Property entfernen. Neue Tabelle → neue Dateien wie „Entity aus Tabelle“.
- Dateien finden über den Index aus WP-40 (Dokument der Property-Getter, Implementierungen von `IEntityTypeConfiguration<T>`). Änderung als Roslyn-Syntax-Umbau (nur Syntax, kein Kompilieren; aktuelles Roslyn im .NET-10-Prozess), Kodierung, BOM und Zeilenenden der Datei bleiben.
- Vorschau als Diff, Schreiben erst nach Bestätigung und nur, wenn die Datei seit der Analyse unverändert ist; kein Build, kein Commit. Nach dem Build des Nutzers lädt das Modell neu, der Abgleich (WP-27) zeigt, ob es passt.
- ADR: FerretSharp schreibt zum ersten Mal in das Repo des Nutzers.
- **Ziel ist der aktive Arbeits-Klon der Gruppe (WP-40, `WorkClones`), nie der verknüpfte Branch-Klon** (der muss dem Stand der DB entsprechen und wird gepullt). Die Dateien des Arbeits-Klons werden selbst untersucht – sein Feature-Branch kann vom Branch-Klon abweichen, der Index aus WP-40 ist nur ein Hinweis, wo zu suchen ist. Die Vorschau nennt Ordner und Branch des Ziels und bietet den Wechsel an; ohne passenden Arbeits-Klon wird nicht geschrieben. Warnung, wenn der Klon auf dem Branch einer Verbindung steht (z. B. `dev`) oder die Zieldatei ungespeicherte Änderungen hat.
- Offen: Umfang der ersten Stufe (Vorschlag: hinzufügen, Facetten ändern, löschen; Umbenennen später); woran „Branch einer Verbindung“ erkannt wird (Branches der verknüpften Klone oder Liste von Hand).

### WP-42 Beziehungsgraph und Teilgraph kopieren
(Fundament für WP-43 und WP-46.) Branch `wp/42-subgraph-copy`.
- **Beziehungsgraph** im Core: Kanten aus deklarierten FKs und Navigations des C#-Modells, später auch `Manual`/`Convention` (`FkSource`), je Kante Spaltenpaare und Quelle.
- **Kopieren:** markierte Zeilen eines Tabs als Start; Eltern werden immer mitgenommen (sonst FK-Verletzung), Kinder je Kante wählbar mit begrenzter Tiefe. Vorschau als Baum mit Zeilenzahl je Tabelle. Lesen auf der Quelle nur über den Leseweg, in Blöcken mit `in` (Grenze 1000 Werte je Liste).
- **Ziel:** ein Workspace einer anderen offenen Verbindung; Einfügen in dessen Sandbox-Transaktion in FK-Reihenfolge (topologisch sortiert), als **eine** Aktion im Undo-Stapel (ADR 0018); Commit wie immer nur auf Befehl. Ziel Prod nur nach Freischalten; Quelle Prod ist erlaubt (nur lesend).
- **Konflikte:** gleicher Schlüssel im Ziel vorhanden → überspringen, aktualisieren oder abbrechen, in der Vorschau sichtbar. Spalten, die im Ziel fehlen oder abweichen, warnen (wie der Schema-Vergleich); Identity ALWAYS lässt sich nicht einfügen; Trigger im Ziel (z. B. Audit) laufen mit – Hinweis.
- ADR (Kopieren über Verbindungen hinweg).
- Offen: Standardtiefe für Kinder; Standard bei Konflikten; Spalten beim Kopieren maskieren (Namen, E-Mail) – in diesem Paket oder später; neue Schlüssel im Ziel (Sequenzen, Umschreiben der FKs) – Vorschlag: später; Zyklen im Graphen (Vorschlag: melden statt auflösen).

### WP-43 Datenqualität der Beziehungen
Branch `wp/43-relationship-check`.
- Bericht je Verbindung über den Graphen aus WP-42: für jede Beziehung **ohne** FK-Constraint die Zahl verwaister Werte (`NOT EXISTS`), mit Beispielwerten; Enum-Spalten mit Werten, die das C#-Enum nicht kennt; deklarierte Constraints im Zustand DISABLED/NOVALIDATE mit verletzenden Zeilen.
- Je Zeile: „Zeilen zeigen“ – das Filtermodell kennt kein `NOT EXISTS`, deshalb ein Tab mit `in`-Filter auf die verwaisten Werte (bis zu einer Grenze) oder die Abfrage im SQL-Editor; „Constraint anlegen“ bei 0 Verwaisten und PK/UK auf der Gegenseite, über den DDL-Baustein aus WP-23 (mit Index für den FK).
- Kosten: volle Scans großer Tabellen über das VPN – Auswahl der Schemas/Beziehungen, je Beziehung abbrechbar, Laufzeit sichtbar, eigene Lese-Session (die Explorer-Session arbeitet einen Aufruf nach dem anderen ab). Auf Prod erlaubt (nur lesend).
- Offen: Ergebnis speichern, um Läufe zu vergleichen; Platz in der Oberfläche (Seite oben rechts wie der Schema-Vergleich oder Ansicht je Verbindung).

### WP-44 Daten-Diff
Branch `wp/44-data-diff`. Zwei Stufen, die dritte kommt mit WP-45.
- **Stufe 1, Snapshot:** den Filter eines Tabs festhalten (Rohwerte, Grenze z. B. 50 000 Zeilen), später „Vergleichen“: neue, gelöschte und geänderte Zeilen über den Schlüssel, geänderte Zellen markiert (`RowComparison`). Funktioniert überall, sieht nur, was im Filter liegt.
- **Stufe 2, Flashback (ganze Tabellen, ohne Snapshot vorher):** Startpunkt per `SELECT TIMESTAMP_TO_SCN(SYSTIMESTAMP) FROM dual` merken, später `tabelle AS OF SCN :start` gegen den aktuellen Stand über den Schlüssel vergleichen – reine Abfragen auf dem Leseweg (`AS OF` entsteht im `QueryBuilder`). Verglichen wird der committete Stand auf einer Session ohne eigene Änderungen. Grenzen: Recht `FLASHBACK` auf die Tabelle (oder Owner), Undo muss reichen (ORA-01555/ORA-08180 verständlich melden), DDL dazwischen bricht den Vergleich, LOBs nur über Länge.
- Snapshot und Ergebnis nur im Speicher (Werte, auch von Prod).
- Offen: Grenze für Stufe 1; Auswahl der Tabellen für Stufe 2 bis WP-45 (Vorschlag: Liste von Hand); Darstellung (Grid mit Status-Spalte wie die Änderungsübersicht?).

### WP-45 Mitschnitt mit Quellzeile und Datenänderungen
(braucht WP-40 und WP-44.) Branch `wp/45-recording-source-data`. Fortsetzung von WP-33 (ADR 0023).
- **Quellzeile je Statement:** EventPipe liefert zu Ereignissen den Aufrufstapel; der erste Rahmen außerhalb von EF Core/`System`/`Microsoft` ist die Methode des Nutzers, über den Index aus WP-40 mit Datei und Zeile. Aus „18× Lazy Loading `Auftrag.Positionen`“ wird „… aus `AuftragService.GetOffene`, `AuftragService.cs:142`“, ohne Code-Änderung und ohne `TagWithCallSite`. **Spike zuerst:** Stapel in Echtzeit inklusive Methoden-Rundown, Kosten für die App.
- **Datenänderungen (Daten-Diff Stufe 3):** „Aufzeichnen“ merkt die SCN der zugeordneten Verbindung; die Zieltabellen der INSERT/UPDATE/DELETE/MERGE-Statements des Mitschnitts bestimmen, welche Tabellen „Stopp“ per Flashback (WP-44) vergleicht. Ergebnis: alle Änderungen der App in allen betroffenen Tabellen, ohne vorher etwas auszuwählen. Audit-Tabellen (WP-26) auf Wunsch mit, weil ihre Trigger nicht im Mitschnitt stehen.
- Feldliste bleibt fest (ADR 0023): aus dem Stapel nur Methodennamen, nie Werte – Nachtrag zum ADR.
- Offen: Ergebnis des Spikes; ob die Quellzeile nur für die Top-Statements ermittelt wird (Kosten).

### WP-46 Beziehungsdiagramm
(braucht WP-42.) Branch `wp/46-relationship-diagram`.
- Nachbarschaft einer Tabelle über 1–2 Ebenen als Diagramm, Kanten nach `FkSource` gefärbt (deklariert, C#-Modell, später manuell/vermutet), Knoten mit Tabellen- und Entity-Namen; Klick öffnet den Tab, Doppelklick erweitert den Knoten. „Als Mermaid kopieren“ für Doku und Tickets.
- Offen: Platz (Ansicht im Tabellen-Tab oder eigener Tab); Layout – SVG aus Razor mit einem einfachen Schichten-Layout im Core (keine neue Bibliothek, keine Logik in JS) oder eine Layout-Bibliothek wie elkjs lokal unter `wwwroot/lib` (dann ADR zum Stack).

### WP-47 Startseite „Seit deinem letzten Besuch“ mit Schema-Cache
Branch `wp/47-since-last-visit`.
- **Voraussetzung, aus dem Backlog („Reaktionszeit über das VPN“, Punkt 1):** letzten Schema-Stand je Verbindung speichern, den Explorer sofort daraus füllen und im Hintergrund abgleichen (stale-while-revalidate, sichtbares „Stand von …“). Vorher messen, wie dort vorgeschlagen.
- **Startseite nach dem Verbinden:** Unterschiede zum letzten Stand über den Schema-Vergleich (WP-20, zwei Snapshots derselben Verbindung) – neue/entfernte Tabellen und Spalten, geänderte Typen, neu ungültige Objekte –, neue Abweichungen zum C#-Modell (WP-27), offene Transaktionen anderer Workspaces, zuletzt benutzte Workspaces. Der Release-Check (Backlog, „später“) käme dort als weiterer Block dazu.
- Offen: eigene Seite oder Hinweis „3 Änderungen seit …“, der die Seite öffnet; was vor dem Abgleich erlaubt ist (Backlog-Eintrag).

### WP-58 Zellen und Spalten markieren
(Frage des Nutzers 2026-10-21: Zell- und Spaltenauswahl fehlt, weil AG Grid sie nur in der Enterprise-Edition hat. Ein eigenes Grid wurde verworfen, weil es für den heutigen Funktionsumfang 2–4 Monate kosten würde und danach dauerhaft gepflegt werden müsste. Vom Nutzer als Paket eingeplant 2026-10-21.) Branch `wp/58-cell-selection`. Mittel; baut auf WP-57 auf (Kopfmenü, Werte einer Spalte über `ColumnValues`) und auf der Zeilenauswahl (`small-grid-keyboard-selection.md`, `small-grid-select-all.md`). Gilt für Tabelle, SQL-Ergebnis und LINQ-Konsole (alle über `grid.js`).
- **Ausgangslage:** Das Grid markiert nur ganze Zeilen (`rowSelection` per Klick, Anker und `span` in `grid.js`). Ziehen mit der Maus markiert Text (`enableCellTextSelection`), Ctrl+C kopiert die fokussierte Zelle oder die markierten Zeilen (`OnCopy`). Das `CellSelectionModule` von AG Grid (Zellbereich, Kopieren, Ausfüllen) ist Enterprise und bleibt ausgeschlossen. Gebaut wird eine eigene Auswahl auf den Mitteln von Community.
- **Modell:** In `grid.js` neben der Zeilenauswahl entweder ein Rechteck (Anker- und Endzelle als Zeilenindex und Spalten-ID) oder eine Menge ganzer Spalten. Es gilt immer nur eine Art Auswahl: Ein Zellbereich hebt die Zeilenauswahl auf und umgekehrt. Spalten zählen in Anzeigereihenfolge (`getAllDisplayedColumns`, angeheftete zuerst). Verschieben oder Anheften einer Spalte, Sortieren, Filtern und Neuladen heben den Bereich auf (andere Zellen an denselben Stellen, wie beim `span`). JS kennt nur die Auswahl; was mit den Zellen geschieht, entscheidet .NET.
- **Darstellung:** `cellClassRules` mit `fs-range` für die Fläche und Randklassen oben/unten/links/rechts für den Umriss. Nach jeder Änderung `api.refreshCells`: Das betrifft nur gerenderte Zellen und bleibt auch bei großen Bereichen billig. Markierte Spalten auch im Kopf (`FerretHeader`). Farben als Tokens (`--grid-range-*`) in `ferretsharp.css`.
- **Maus:** Ziehen von einer Zelle in eine andere startet einen Zellbereich. Bleibt die Maus in einer Zelle, markiert sie wie bisher Text (Text aus einer Zelle kopieren bleibt möglich). Sobald die Maus die Startzelle verlässt, wird die Textmarkierung aufgehoben (`removeAllRanges`). Am Rand des Grids scrollt es automatisch (Intervall mit `ensureIndexVisible`/`ensureColumnVisible`). Shift+Klick erweitert einen bestehenden Zellbereich; ohne Bereich bleibt es Zeilenauswahl. Der Start passiert im `mousedown`, nicht in `onCellClicked` (das kommt asynchron, `ui-grid.md`). Im E2E mit echter Windows-Eingabe prüfen.
- **Tastatur:** Shift+←/→ macht aus der fokussierten Zelle einen Zellbereich und erweitert ihn. Shift+↑/↓ und Shift+Bild↑/↓ erweitern einen bestehenden Zellbereich; ohne Bereich bleiben sie Zeilenauswahl. Esc hebt den Bereich auf, Ctrl+A bleibt „alle Zeilen“.
- **Spalten:** Ctrl+Klick auf den Kopf markiert die Spalte statt zu sortieren (`FerretHeader` ruft `progressSort` heute bei jedem Klick). Ctrl+Shift+Klick erweitert die Markierung bis zur angeklickten Spalte, Ctrl+Klick auf eine markierte Spalte nimmt sie heraus. Das Kopfmenü bekommt „Spalte markieren“. Eine markierte Spalte meint alle Zeilen, auch die noch nicht geladenen.
- **Kopieren (Ctrl+C, Zellmenü):** Ein Bereich wird als Tabulator-getrennter Text kopiert (Zeilen × Spalten), wie angezeigt mit ausstehenden Werten (A21). Neuer Aufruf `OnCopyRange(fromRow, toRow, colIds)` an `FerretGrid`/`SqlResultGrid`, der Text entsteht in Core. Liegen im Bereich noch nicht geladene Zeilen (selten, weil Ziehen mit Auto-Scroll sie lädt), liest .NET die fehlenden Blöcke nach. Markierte Spalten liefern die Werte aller Zeilen über den Weg von WP-57 (Abfrage wie „Alle Zeilen“, mit Obergrenze), mehrere Spalten nebeneinander.
- **Fußzeile:** zeigt „3 × 4 Zellen“ bzw. „2 Spalten“ statt der Zahl markierter Zeilen.
- **Nicht in diesem Paket (bleibt im Backlog bzw. kommt dorthin):** mehrere Zellen aus Excel einfügen (Backlog, Editieren), Entf auf einen Bereich (NULL setzen) und Ausfüllen nach unten (seit 2026-10-22: WP-59), Summe und Anzahl markierter Zahlen in der Fußzeile, mehrere getrennte Bereiche (Ctrl+Ziehen).
- Entscheidungen des Nutzers (2026-10-22):
  - **Einfacher Klick:** markiert weiter die ganze Zeile, nicht die Zelle wie in Excel. Export, Formular, Zeilenvergleich, Entf und Ctrl+C hängen an der Zeilenauswahl. Einen Zellbereich startet erst Ziehen über mehrere Zellen oder Shift+←/→.
  - **Zeilen eines Zellbereichs:** gelten **nicht** als markierte Zeilen, es bleibt bei einer Art Auswahl. Formular, Zeilenvergleich und die zeilenbezogenen Exporte (INSERT, CSV, Excel) verhalten sich bei einem Zellbereich wie ohne Zeilenauswahl. „Bereich exportieren“ (CSV/Excel nur der markierten Zellen) kommt in den Backlog.
  - **Spalten markieren:** per Ctrl+Klick auf den Kopf, dazu „Spalte markieren“ im Kopfmenü; kein eigener Streifen (kostet Höhe und kollidiert mit Verschieben und Breite ändern). Ctrl ist im Kopf frei: Shift+Klick bleibt die Mehrfachsortierung (`progressSort(e.shiftKey)`), `FerretHeader` sortiert bei Ctrl nicht.
  - **Kopfzeile:** Ctrl+C kopiert ohne Kopf, wie Excel, damit der Bereich direkt in eine bestehende Tabelle oder einen Filter passt. „Als Tabelle kopieren“ im Zellmenü kopiert bei einem Zellbereich bzw. markierten Spalten genau diese mit Kopf; ein eigener Eintrag „Mit Spaltennamen kopieren“ ist damit nicht nötig.
  - **Obergrenze:** dieselbe Regel wie WP-57, 100 000 Werte, hier gezählt als Zellen (Zeilen × Spalten), für Zellbereiche und markierte Spalten (`ColumnValues.MaxValues`). Darüber wird **nichts** kopiert, mit Hinweis auf „Alle Zeilen“ als CSV/Excel – ein still gekürzter Bereich sähe vollständig aus.
  - **Spalten im SQL-Ergebnis:** Die Abfrage läuft noch einmal, wie bei WP-57 (der Leseweg lässt nur SELECT zu). Sind schon alle Zeilen geladen (letzter Block kürzer als die Blockgröße), kommen die Werte aus den geladenen Blöcken, ohne neuen Roundtrip über das VPN. Beim Start prüfen, ob WP-57 das schon so macht.
  - **Menüs von WP-57:** Das Zellmenü bezieht sich immer auf die aktive Auswahl: bei einem Zellbereich auf seine Zellen, bei markierten Spalten auf alle Zeilen (Weg von WP-57), sonst wie heute auf die geklickte Spalte der markierten Zeilen.
    - Bei einem Zellbereich entfällt „Spalte X kopieren“, Ctrl+C liefert bei einer Spalte schon dasselbe (ein Wert je Zeile, Anführungszeichen wie Excel über `AppendField`). „Als IN-Liste“ gilt für die Zellen des Bereichs (Datenbankwerte, ohne Duplikate) und nur, wenn er genau eine Spalte breit ist.
    - Der Rechtsklick auf markierte Spalten zeigt dieselben Einträge wie das Kopfmenü („Werte kopieren“, „Als IN-Liste“, gleicher Code über `ColumnValuesMenuItems`).
    - Bleiben: das Kopfmenü als direkter Weg zu allen Werten ohne vorheriges Markieren (im SQL-Ergebnis der einzige Einstieg) und „Spalte X kopieren“ im Zellmenü für markierte Zeilen.
  - **Ausstehende Änderungen:** Ein Zellbereich kopiert wie angezeigt mit ausstehenden Werten (A21), markierte Spalten lesen über den Weg von WP-57 den Stand der Datenbank (geschriebene Änderungen ja, ausstehende nein). Hat der Tab in diesen Spalten ausstehende Änderungen, zeigt die Statusleiste nach dem Kopieren einen Hinweis („Stand der Datenbank, N ausstehende Änderungen nicht enthalten“). Die ausstehenden Werte über den Zeilenschlüssel einzumischen kommt in den Backlog.

### WP-59 Werte für markierte Zellen setzen
(„Bulk-Update“, Rückmeldung eines Kollegen 2026-10-22; vom Nutzer als Paket eingeplant 2026-10-22, zusammen mit „Entf auf einen Bereich“ und „Ausfüllen nach unten“, die aus WP-58 zurückgestellt waren.) Branch `wp/59-range-edit`. Mittel; baut auf WP-58 auf (Zellbereich und markierte Spalten, `marksOf` in `grid.js`, `GridMarks`) und auf dem Editieren aus WP-09/WP-30 (`ChangeTracker`, `EditHistory`, `GridEditing`). Nur Tabellen-Tabs: SQL-Ergebnis und LINQ-Konsole sind nicht editierbar (`IEditableGrid` hat nur `FerretGrid`).
- **Ausgangslage:** `ChangeTracker.SetValue` nimmt den Text einer Zelle, prüft sie (`NotEditableReason`, LOB, `IsEditableValue`, Zeile zum Löschen markiert) und legt je Aufruf einen Schritt in `EditHistory` ab. Ein Schritt kann schon mehrere Zeilen umfassen (`DiscardPending`). Entf markiert die markierten Zeilen zum Löschen, mit einem Undo-Schritt **je Zeile** (`Delete` ruft `Remember` für jede Zeile einzeln). Bei einem Zellbereich tut Entf nichts, weil es dann keine Zeilenauswahl gibt.
- **Drei Befehle, alle als ausstehende Änderungen:** Geschrieben wird erst auf Befehl des Nutzers, wie bei jedem Edit. Auf Prod gilt dieselbe Freischaltung wie beim Editieren, ohne zusätzliche Abfrage.
  - **Wert setzen:** Den Wert in der fokussierten Zelle mit dem Editor der Spalte eingeben (Text, Zahl, Datum, Enum, Flags, Bool – derselbe Weg über die Präsentationsschicht wie beim einzelnen Edit). Ctrl+Enter übernimmt ihn für dieselbe Spalte aller markierten Zeilen bzw. für die Zellen des Bereichs, wie in Excel. Dazu im Zellmenü „Wert für markierte Zellen setzen…“: ein kleiner Dialog mit dem Editor der Spalte, für den Weg mit der Maus.
  - **Entf auf einen Bereich:** setzt die Zellen des Bereichs auf NULL. Bei markierten Zeilen bleibt Entf „Zeilen löschen“.
  - **Ausfüllen nach unten (Ctrl+D):** Je Spalte des Bereichs geht der Wert der obersten Zeile in die Zeilen darunter, wie angezeigt mit ausstehenden Werten (A21). Übernommen wird der Wert selbst, nicht sein Text (sonst gingen Stellen bei TIMESTAMP oder NUMBER verloren): ein neuer Weg am `ChangeTracker`, der einen Oracle-Wert setzt statt Text zu parsen. Ctrl+D ist im Grid frei; in Monaco heißt es „nächstes Vorkommen“ (`Shortcuts.TakenFromEditors`).
- **Ein Undo-Schritt je Befehl:** Ein neuer Weg am `ChangeTracker` setzt viele Zellen und ruft einmal `Remember` mit den Zuständen aller berührten Zeilen davor (gemeinsamer Stapel, ADR 0018). Entf auf mehrere Zeilen wird gleich mit auf einen Schritt umgestellt.
- **Prüfen vor dem Setzen:** Das Parsen hängt nur von der Spalte ab, nicht von der Zeile. Der Wert wird deshalb einmal je Spalte geprüft; ist er ungültig, wird nichts gesetzt. Spalten, die sich gar nicht bearbeiten lassen (PK, virtuell, Identity, LOB), lehnen den Befehl mit Grund ab. Zellen, die nur in einzelnen Zeilen nicht gehen (Zeile zum Löschen markiert, Zahl zu genau), werden übersprungen und in der Statusleiste genannt („12 Zellen gesetzt, 2 übersprungen: zum Löschen markiert“).
- **Nicht geladene Zeilen:** Liegen im Bereich Zeilen, die noch nicht geladen sind (selten, weil Ziehen mit Auto-Scroll sie lädt), liest .NET die fehlenden Blöcke nach, wie beim Kopieren in WP-58.
- **Nicht in diesem Paket (Vorschlag):** mehrere Zellen aus Excel einfügen (Backlog, Editieren); Reihen fortsetzen (1, 2, 3 …), Ausfüllen nach rechts oder oben; ein mengenbasiertes `UPDATE … WHERE <Filter>` für markierte Spalten (siehe Offen).
- Offen (beim Start mit dem Nutzer):
  - **Ctrl+Enter** ist global „Filter anwenden“ bzw. „Ausführen“ (`ShortcutAction.ApplyFilters`, einstellbar). Vorschlag: In einem offenen Zelleditor gehört Ctrl+Enter dem Grid, wenn mehrere Zeilen oder ein Bereich markiert sind; sonst bleibt es „Filter anwenden“. Beim Start prüfen, wer die Taste heute im Zelleditor bekommt. Alternative ist eine eigene Taste (Alt+Enter ist schon das Formular).
  - **Markierte Spalten** meinen alle Zeilen, auch die nicht geladenen. Wert setzen oder Entf wäre dort ein mengenbasiertes `UPDATE … WHERE <Filter>`: ein eigener Schreibweg mit ADR, Bestätigung samt Zeilenzahl, auf Prod heikel. Vorschlag: nicht in diesem Paket. Die Befehle sind bei markierten Spalten aus, mit Hinweis auf einen Zellbereich; die Idee bleibt im Backlog.
  - **Mehrspaltiger Bereich bei „Wert setzen“:** Excel setzt alle Zellen. Vorschlag: ebenso; jede Spalte parst den Wert selbst, und ist er in einer Spalte ungültig, wird nichts gesetzt (die Meldung nennt die Spalte).
  - **Entf und NOT NULL:** Vorschlag: NOT-NULL-Spalten nicht anfassen und nennen – Oracle lehnte die Zeile beim Schreiben ohnehin ab.
  - **Ctrl+D ohne Bereich:** Excel holt dann den Wert der Zeile darüber. Vorschlag: nur mit einem Bereich ab zwei Zeilen.
  - **Obergrenze:** Jede geänderte Zeile wird beim Schreiben ein eigenes UPDATE, also ein Roundtrip über das VPN. Vorschlag: höchstens 10 000 Zeilen je Befehl, darüber nichts setzen, mit Hinweis.

## Offene UX-Fragen
- Workspaces: Standardname ist „Workspace N“ mit der kleinsten freien Nummer (nach Umbenennen von „Workspace 1“ heißt der nächste wieder „Workspace 1“) – so lassen oder fortlaufend zählen?
