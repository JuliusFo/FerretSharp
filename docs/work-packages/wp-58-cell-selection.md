# WP-58 Zellen und Spalten markieren → Release offen

Branch `wp/58-cell-selection`. Frage des Nutzers 2026-10-21: Zell- und Spaltenauswahl fehlt, weil AG Grid sie nur in der Enterprise-Edition hat. Ein eigenes Grid wurde verworfen (2–4 Monate für den heutigen Funktionsumfang, danach dauerhaft zu pflegen). Vom Nutzer als Paket eingeplant 2026-10-21, offene Fragen am 2026-10-22 entschieden, am selben Tag begonnen.

## Planung (aus `docs/roadmap.md`)
- **Ausgangslage:** Das Grid markiert nur ganze Zeilen (`rowSelection` per Klick, Anker und `span` in `grid.js`). Ziehen mit der Maus markiert Text (`enableCellTextSelection`), Ctrl+C kopiert die fokussierte Zelle oder die markierten Zeilen (`OnCopy`). Das `CellSelectionModule` von AG Grid ist Enterprise und bleibt ausgeschlossen; gebaut wird eine eigene Auswahl auf den Mitteln von Community.
- **Modell:** In `grid.js` neben der Zeilenauswahl entweder ein Rechteck (Anker- und Endzelle) oder eine Menge ganzer Spalten. Immer nur eine Art Auswahl. Spalten zählen in Anzeigereihenfolge (angeheftete zuerst). Verschieben oder Anheften einer Spalte, Sortieren, Filtern und Neuladen heben einen Zellbereich auf. JS kennt nur die Auswahl, was mit den Zellen geschieht, entscheidet .NET.
- **Darstellung:** `cellClassRules` mit `fs-range` und Randklassen, nach jeder Änderung `api.refreshCells`; markierte Spalten auch im Kopf; Farben als Tokens.
- **Maus:** Ziehen von einer Zelle in eine andere startet einen Zellbereich, innerhalb einer Zelle bleibt es Textmarkierung. Auto-Scroll am Rand. Shift+Klick erweitert einen bestehenden Zellbereich. Start im `mousedown`.
- **Tastatur:** Shift+←/→ macht aus der fokussierten Zelle einen Zellbereich; Shift+↑/↓ und Shift+Bild↑/↓ erweitern einen bestehenden. Esc hebt auf, Ctrl+A bleibt „alle Zeilen“.
- **Spalten:** Ctrl+Klick auf den Kopf markiert statt zu sortieren, Ctrl+Shift+Klick bis zur angeklickten Spalte, Ctrl+Klick auf eine markierte nimmt sie heraus; „Spalte markieren“ im Kopfmenü. Eine markierte Spalte meint alle Zeilen.
- **Kopieren:** Tabulator-getrennt (Zeilen × Spalten), wie angezeigt mit ausstehenden Werten (A21), der Text entsteht in Core; nicht geladene Zeilen liest .NET nach; markierte Spalten über den Weg von WP-57.
- **Fußzeile:** „3 × 4 Zellen“ bzw. „2 Spalten“.
- **Nicht in diesem Paket:** mehrere Zellen aus Excel einfügen, Entf auf einen Bereich, Ausfüllen nach unten, Summe und Anzahl in der Fußzeile, mehrere getrennte Bereiche.

## Entscheidungen des Nutzers (2026-10-22, alle Vorschläge übernommen)
1. **Einfacher Klick** markiert weiter die ganze Zeile. Einen Zellbereich startet erst Ziehen über mehrere Zellen oder Shift+←/→.
2. **Zeilen eines Zellbereichs** gelten nicht als markierte Zeilen. Formular, Zeilenvergleich und die zeilenbezogenen Exporte verhalten sich wie ohne Zeilenauswahl; „Bereich exportieren“ kommt in den Backlog.
3. **Spalten** per Ctrl+Klick auf den Kopf und „Spalte markieren“ im Kopfmenü, kein eigener Streifen.
4. **Kopfzeile:** Ctrl+C ohne, „Als Tabelle kopieren“ mit.
5. **Obergrenze** wie WP-57: 100 000, hier Zellen (Zeilen × Spalten); darüber wird nichts kopiert.
6. **Spalten im SQL-Ergebnis:** die Abfrage noch einmal; sind alle Zeilen geladen, aus den geladenen Blöcken.
7. **Menüs von WP-57:** Das Zellmenü bezieht sich auf die aktive Auswahl. Bei einem Zellbereich entfällt „Spalte X kopieren“, die IN-Liste gilt nur für eine Spalte; markierte Spalten bekommen dieselben Einträge wie das Kopfmenü; das Kopfmenü und „Spalte X kopieren“ für markierte Zeilen bleiben.
8. **Ausstehende Änderungen:** Ein Zellbereich kopiert wie angezeigt, markierte Spalten den Stand der Datenbank – mit Hinweis, wenn der Tab in diesen Spalten ausstehende Änderungen hat. Einmischen über den Zeilenschlüssel steht im Backlog.

## Entscheidungen in der Umsetzung
- **Die Rechnung in `gridSelection.js`** (`rectOf`, `markOf`, `movedEnd`, `markedColumns`, `inDisplayOrder`), rein und mit Tests in `tests/js`; `grid.js` wendet sie an. Der Zustand je Grid: `cells` (Anker und Ende) oder `marked` (Spalten-IDs), dazu die Anzeigereihenfolge beim letzten Zeichnen.
- **Der Fokus bleibt an der Startzelle** (wie Excels aktive Zelle). Tasten und Ziehen bewegen nur das Ende des Rechtecks; ←/→/↑/↓ ohne Shift heben die Markierung auf und gehen wie bisher.
- **Der Klick nach dem Ziehen gehört keiner Zeile:** Endet das Ziehen in derselben Zeile, käme der `click` auf der Zeile an und AG Grid wählte sie aus. `grid.js` schluckt diesen einen Klick in der Capture-Phase. Shift+Klick auf einen bestehenden Bereich stoppt schon das `mousedown`, damit AG Grid keine Zeilen dazwischen wählt.
- **Markierte Spalten bleiben beim Sortieren und Filtern** – sie meinen alle Zeilen. Ein Zellbereich geht dabei, ebenso beim Verschieben oder Anheften einer Spalte (die Spalten der Markierung bekommen dann nur ihren Umriss neu).
- **Spalten markieren nur, wo es „alle Zeilen“ gibt:** Tabelle und SQL-Editor (wie das Kopfmenü, `GridOptions.HeaderMenu`). In der LINQ-Konsole gibt es Zellbereiche, aber keine ganzen Spalten.
- **Das Zellmenü** hat im markierten Bereich eine eigene Gruppe (`MarkedCellsMenuItems`: „Kopieren“ bzw. „Werte kopieren“, „Als Tabelle kopieren“, „Als IN-Liste kopieren“). Die Exportgruppe der Zeile lässt dann „Als Tabelle kopieren“ und die Spalten-Einträge weg (`ExportMenuItems.Marked`), alles andere nimmt die angeklickte Zeile, ohne sie im Grid auszuwählen.
- **Kopieren in `CellCopies`** (UI State, Seite des Grids als `MarkedCells`): der Text eines Bereichs in Core (`CellValues`, bei einer Spalte derselbe Text wie WP-57), der Bereich über die Seite in die Zwischenablage (wie Ctrl+C). Ganze Spalten lesen im Hintergrund mit Statusleiste und kopieren über den Host (`IClipboardService`) wie WP-57; die Tabelle liest nur die markierten Spalten (`BuildColumnValues` über mehrere Spalten). Spalten mit LOB, LONG und Ähnlichem lehnt das ab (die Abfrage liest nur Werte).
- **Nicht geladene Zeilen eines Bereichs** liest .NET Lücke für Lücke in einer Abfrage (bis 5000 Zeilen je Abfrage, `CellCopies.ReadSize`), nicht blockweise – über das VPN zählen die Roundtrips. Im E2E 30 001 Zeilen ab Zeile 0 nach einem Sprung auf Zeile 30 000 in unter einer halben Sekunde (lokal).
- **Ein vollständig geladenes SQL-Ergebnis** liefert seine Spalten aus den gehaltenen Blöcken (`ColumnValueCopies.ForLoaded`) – auch für das Kopfmenü von WP-57, das vorher immer neu abfragte.
- **Der Hinweis auf ausstehende Änderungen** (`ChangeTracker.PendingIn`: ausstehender Wert in einer der Spalten, ausstehendes Löschen, neue Zeile) gilt auch für das Kopfmenü von WP-57: Dort fehlten sie bisher still.
- **Auto-Scroll** misst am Zeilenbereich von AG Grid 36 (`.ag-grid-viewport` unter `.ag-header`): Zeilen umso schneller, je weiter die Maus über den Rand hinaus ist (Schritte von `Abstand / 8` Zeilen alle 60 ms), Spalten eine je Schritt. `.ag-body-viewport` älterer Versionen gibt es nicht mehr (im E2E gefunden).
- Kein ADR: keine neue Abhängigkeit, kein neuer DB-Weg (Leseweg und Hintergrund-Lesen wie WP-54/57).

## Gebaut
- **Core:** `Query/CellValues` (`CellValuesText`, `Fits`), `QueryBuilder.BuildColumnValues` und `IDataAccess.ReadColumnAllAsync` über eine Liste von Spalten (DISTINCT nur für eine), `ChangeTracker.PendingIn`, `ShortcutMap.Fixed` mit Shift+←/→. Text `SettingsText.FixedMarkCells`.
- **UI-State:** `GridMarks`, `GridSelection` (markierte Zellen und Spalten), `MarksMenu`, `CellCopies`/`MarkedCells`/`MarksCopy`, `AllColumnValues.ValueIndexes`, `ColumnValueCopies.ForLoaded`/`PendingLeftOut`.
- **UI:** `grid.js` (Zustand, Zeichnen, Maus mit Auto-Scroll, Tasten, `OnCopyCells`, `OnCellContextMenu` mit den Markierungen, `OnSelectionChanged` mit Größe, Export `markColumn`), `gridSelection.js`, `FerretHeader` (Ctrl+Klick), `MarkedCellsMenuItems`, `GridContextMenu`/`ResultMenu`, `ExportMenuItems.Marked`, `ColumnMenu`/`ResultColumnMenu` („Spalte markieren“), `ColumnValuesMenuItems.Pending`, `FerretGrid`/`SqlResultGrid`. Tokens `--grid-range`, `--grid-range-border`. Texte `GridText.Footer_Marked*`, `Marks_*`, `GridMenu_CopyCells*`, `GridMenu_CopyColumnsTitle`, `GridMenu_CopyInListOneColumn`, `ColumnMenu_MarkColumn*`, `ResultColumnMenu_MarkColumnTitle`, `ColumnMenu_LoadedResultValuesTitle`, `ColumnValues_PendingLeftOut*`.
- **Tests:** Core `CellValuesTests`, `QueryBuilderTests` (mehrere Spalten, DISTINCT nur für eine), `EditingTests` (`PendingIn`), `ShortcutMapTests`; UI `CellCopiesTests` (Bereich wie angezeigt, Kopfzeile, Lücken am Stück und in Teilen, früh endendes Lesen, gescheitertes Lesen, Obergrenze, IN-Liste nur für eine Spalte, ganze Spalten mit Hinweis, LOB abgelehnt, ohne „alle Zeilen“, geladenes Ergebnis), `GridSelectionTests`, `ColumnValueCopiesTests`; JS `gridSelection.test.mjs`; Integration `OracleDataAccessTests` (mehrere Spalten in der Reihenfolge des Grids).

## E2E (2026-10-22)
Eigene Instanz (`--data-dir`, eigener WebView2-Ordner, Build im Scratchpad, `--lang=de`, hell und dunkel), eigener Container `ferret-wp58` auf Port 1532. Tasten und Maus per CDP (Stufe 2), die Zwischenablage der Seite per Mitschrift, die des Hosts über `Get-Clipboard`.
- **Echte Windows-Eingabe kam nicht an:** Per `SendInput` erreichten nur Bewegungen die Seite, kein `mousedown` – der Eingabefilter auf dem Rechner aus `docs/e2e-testing.md`. Ziehen, Shift+Klick und Ctrl+Klick im Kopf sind deshalb nur per CDP geprüft; der Test mit echter Maus steht beim Nutzer aus.
- **Gefunden und behoben:** Auto-Scroll griff erst außerhalb des ganzen Grids (`.ag-body-viewport` fehlt seit AG Grid 36) und lief mit einer Zeile je Schritt zu langsam; nicht geladene Zeilen wurden zu je 500 gelesen.
- Bestätigt: `refreshCells` ohne `force` wendet die `cellClassRules` neu an. Der Klick nach dem Ziehen wählt keine Zeile. Sortieren über den Kopf hebt einen Zellbereich auf, Ctrl+Klick sortiert nicht. Log der Testinstanz ohne `[ERR]`/`[WRN]`.
- **Nicht geprüft:** echte Maus (s. o.), die LINQ-Konsole (kein verknüpftes C#-Projekt), Lesen über das VPN, Abbrechen des Lesens ganzer Spalten über die Statusleiste (Weg von WP-57).

## Offen
- Test mit echter Maus durch den Nutzer: Ziehen mit Auto-Scroll, Shift+Klick, Ctrl+Klick und Ctrl+Shift+Klick im Kopf, Doppelklick zum Bearbeiten (der WPF-Host schickt dabei das `mousedown` doppelt).
- UX-Frage: Soll Ctrl+C auf markierte Spalten bei bekannter Zeilenzahl über der Grenze gar nicht erst lesen? Heute liest es bis zur Grenze und meldet dann „nichts kopiert“.
