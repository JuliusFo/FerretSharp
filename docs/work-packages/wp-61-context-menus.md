# WP-61 Kontextmenüs aufräumen (Zelle und Spaltenkopf) → Release offen

Branch `wp/61-context-menus`, 2026-10-23. Wunsch des Nutzers vom selben Tag: Das Zellmenü war mit WP-37 … WP-60 auf rund 40 Zeilen gewachsen. Von drei Varianten (Untermenüs, Schnellleiste mit Export-Matrix, zwei Spalten) hat er die Schnellleiste gewählt, Zell- und Spaltenmenü als ein Paket, vor WP-55.

## Planung (aus `docs/roadmap.md`)
- **Warum es lang war:** Der Export (14 Zeilen samt „Alle Zeilen“ und C#) ist eine Matrix aus Format × Ziel (Zwischenablage/Datei) × Umfang (Auswahl/alle), als Liste wiederholt er sich. Dazu kamen Gruppen mit nur einem Eintrag (Datensatz-Diagramm, andere Verbindung) und FK-Sprünge, die NULL sind – im Beispiel des Nutzers 7 von 13 bei „Points to“.
- **Zellmenü** (`GridContextMenu`, dieselben Bausteine im `ResultMenu`): Kopf wie bisher; **Schnellleiste** mit Icons für die Einzelaktionen (Beschriftung im `title`, deaktiviert mit Grund, eigene SVGs in `Icons.cs` – seit `feat/icons` alle aus Lucide, ADR 0035); **Filterfeld** (Wunsch des Nutzers), das Aktionen und FK-Ziele eingrenzt; **Export-Matrix** mit den Formaten als Zeilen und „kopieren“, „Auswahl speichern“, „alle Zeilen speichern“ als Spalten, Strich, wo es die Kombination nicht gibt; darunter die Werte der geklickten Spalte als Chips. **FK-Sprünge nicht kürzen** – der Nutzer springt häufig: nur NULL-Referenzen zu „7 weitere sind NULL“ zusammenfassen, „Referenced by“ erst bei vielen Einträgen mit „… weitere“; die Sprünge der geklickten Spalte bleiben oben und hervorgehoben.
- **Spaltenmenü** (`ColumnMenu`, `ResultColumnMenu`) mit denselben Bausteinen, ohne Filterfeld: Kopf mit Infos zur Spalte (NULL, PK, FK mit Ziel, C#-Property und CLR-Typ, Kommentar), Leiste mit kurzer Beschriftung (Anheften, Spalte markieren, Anzeigespalte – umrandet, wenn gesetzt), Werte als Chips.

## Entscheidungen des Nutzers (2026-10-23)
Zu den offenen Punkten der Roadmap hat der Nutzer die Empfehlungen übernommen:
1. **Reihenfolge:** vor WP-55; in der Leiste des Spaltenmenüs ist Platz für „Werte zählen“.
2. **Bearbeiten, markierte Zellen:** als Textzeilen unter der Leiste (eingeblendet nur, wo sie gelten; „Zeile löschen“ bleibt rot und lesbar). **LOB öffnen** kommt als Icon in die Leiste.
3. **Leiste im Zellmenü** nur mit Icons und Tooltip, im Spaltenmenü mit Beschriftung.
4. **Links** (Zeilen, Ansicht) als zwei Icons statt eines Untermenüs.
5. **Tastatur:** Tippen landet ohne Klick im Filterfeld, ↑ ↓ und Enter laufen durch die sichtbaren Einträge; Pfeiltasten in zwei Richtungen durch Leiste und Matrix in den Backlog.
6. **„… weitere“** ab 8 eingehenden Sprüngen, der aufgeklappte Zustand wird nicht gemerkt.
7. **Kleine Zusätze** mitgenommen: Spaltennamen kopieren, Werte ohne Dubletten („distinct“), FK-Ziel im Spaltenmenü öffnen.
8. **„ALTER TABLE … ADD“** auch im Spaltenkopf-Menü des Grids, für die markierten Spalten; das Menü der Spaltenansicht mit denselben Bausteinen, bei vielen Verbindungen mit Filterfeld.
- Dazu: Screenshots der neuen Menüs in den CHANGELOG.

## Entscheidungen in der Umsetzung
- **Bausteine unter `Components/Grid`:** `QuickBar` + `QuickAction` (Icon, Name und Beschreibung im Tooltip, deaktiviert mit Grund, `Active` umrandet, `Caption` im Spaltenmenü), `ExportMatrix` (ersetzt `ExportMenuItems`; Zeilen als `ExportMatrixRow` mit `ExportMatrixCell`, weitere Formate wie C#-Objekte und HasData kommen vom Menü über `More`), `ValueChips` (Zeilen, IN-Liste, distinct; neben dem Spaltennamen im Zellmenü, unter der Überschrift im Spaltenmenü), `AllValueChips` (alle Werte, ersetzt `ColumnValuesMenuItems`), `MenuEntry` (Textzeile), `FormQuickAction` (Formular oder Vergleich, ersetzt `FormMenuItems`), `ColumnMenuHead`; unter `Components/Schema` `AddColumnsMenuItems` für Spaltenansicht und Spaltenkopf.
- **Filter:** `MenuFilter` (rein, getestet) als kaskadierter Wert; jeder Baustein fragt ihn selbst (`Matches` über Name und Gruppe, jedes getippte Wort muss in einem der Texte stehen). Beim Filtern klappen „… weitere“ und „NULL“ auf, Überschriften ohne passende Zeile verschwinden. „Nichts passt.“ steht per CSS (`:has`), wenn weder Leiste noch Rumpf etwas zeigen – keine Zählung in C#.
- **Tastatur und Größe in `dom.js menuKeys`** (`PopoverMenu KeyNavigation`): Fokus ins Filterfeld (ohne Feld ins Menü), ↑ ↓ über alle sichtbaren, nicht deaktivierten Knöpfe und das Feld in Bildschirmreihenfolge, ein Zeichen oder Backspace auf einem Eintrag fokussiert erst das Feld (die Taste landet dort), Leertaste und Enter klicken wie im Browser. Ein `ResizeObserver` hält das Menü im Fenster, wenn es wächst oder schrumpft (aufgeklappte Liste, Filter): an der Klickhöhe, sonst so weit oben wie nötig. Die geschätzte Höhe zählt deshalb nur noch grob.
- **FK-Sprünge einzeilig** (`FkJumpItem OneLine`): Tabelle links, rechts die Werte (`FkJump.Values`: `4711`, `1, 4711`, `3 Werte`) bzw. die Zahl der Zeilen eingehender Sprünge; die Bedingung steht im Tooltip. Das Formular behält die zweizeilige Form. NULL-Sprünge (`FkJump.IsNull`: in allen Zeilen ist der Schlüssel NULL) werden ab zwei zusammengefasst, nie der über die geklickte Spalte (`FkJumpLists.FoldNull`); eingehende ab 10 auf 8 plus „… 2 weitere“ (`FoldIncoming` – eine einzelne weitere Zeile wäre so lang wie der Eintrag).
- **Export-Matrix:** Spalten „kopieren“, „N Zeilen“ (Auswahl speichern), „alle“ (Tooltip: die Überschrift „Alle Zeilen des Filters · 12.345 Zeilen“). „Als INSERT-Skript speichern“ (nur Tabellen, wie bisher) ist das Feld INSERT × Auswahl (`InsertFile`). Das Feld „Tabelle kopieren“ fehlt in markierten Zellen wie bisher (deren Gruppe kopiert sie).
- **distinct** ist ein drittes Format von `ColumnValues` (`DistinctLines`): ein Wert je Zeile wie angezeigt, jeder einmal in der Reihenfolge seines ersten Vorkommens, ohne NULL (mit Hinweis wie die IN-Liste); alle Werte einer Tabelle liest es mit `SELECT DISTINCT` wie die IN-Liste.
- **Spaltenkopf-Menü:** Fakten aus Schema und Annotation-Schicht (`ColumnFacts.ForeignKeysOf`: jede Zieltabelle einmal, auch Beziehungen des C#-Modells; `TablePresentation.PropertyOf`). „ALTER TABLE … ADD“ nimmt alle ganz markierten Spalten, wenn die geklickte dazugehört: `grid.js` schickt `marksOf()` mit dem Rechtsklick auf den Kopf, `ColumnMenuRequest.Marked`. Nur für Tabellen. Die Verbindungen sind Chips mit Art-Kürzel.
- **Kopieren aus dem Menü** (Spaltenname, ADD-Skript) über `DomModule.CopyTextAsync` wie die übrigen Kopien aus Menüs (`ui.md`), der Hinweis danach wie gehabt.
- Kein ADR: Bausteine aus vorhandenen Mustern (`PopoverMenu`, `SearchBox`, Tooltips als `title`), keine neue Abhängigkeit, kein neuer Weg zur Datenbank.

## Gebaut
- **Core:** `FkJump.IsNull`, `FkJump.Values`, `ColumnValuesFormat.DistinctLines`; Texte `QueryText.FkConditionValues`, `ColumnValuesDistinctNullsLeftOut*`.
- **UI-State:** `MenuFilter`, `FkJumpLists.FoldNull`/`FoldIncoming`/`ShownIncoming`, `ColumnFacts`, `ExportMatrixRow`/`ExportMatrixCell`, `ColumnMenuRequest.Marked`, `ColumnValueCopies` (distinct liest `DISTINCT`, eigener Hinweis).
- **UI:** `QuickBar`, `QuickAction`, `ExportMatrix`, `ValueChips`, `AllValueChips`, `MenuEntry`, `FormQuickAction`, `ColumnMenuHead`, `AddColumnsMenuItems`; umgebaut `GridContextMenu`, `ResultMenu`, `ColumnMenu`, `ResultColumnMenu`, `ColumnsView` (Menü), `FkJumpSection`, `FkJumpItem`, `MarkedCellsMenuItems`, `PopoverMenu` (`KeyNavigation`); `FerretGrid`/`SqlResultGrid` (`OnHeaderContextMenu` mit Markierung), `grid.js`, `dom.js` (`menuKeys`), `Icons` (Braces, Document, Form, Transfer, Link, LinkView, Download, SelectColumn, Star), CSS `.quick-*`, `.export-matrix`, `.matrix-*`, `.value-chips`, `.menu-chip`, `.ctx-fact*`, `.ctx-fold`.
- **Tests:** Core `ColumnValuesTests` (distinct, NULL mit und ohne Zahl), `FkNavigationTests` (`IsNull`, `Values`); UI `FkJumpListsTests` (Falten, Filter), `ColumnFactsTests`.

## E2E (2026-10-23)
Eigene Instanz (`--data-dir`, eigener WebView2-Ordner, Build im Scratchpad, `--lang=en`, dunkel), eigener Container `ferret-wp61` auf Port 1531 (danach entfernt), verknüpftes Beispielmodell (`samples`). Klicks, Rechtsklicks und Tasten per CDP (Stufe 2), Zustand über das DOM, Zwischenablage über `Get-Clipboard`.
- **Gefunden und behoben:** Ein Klick auf „3 more are NULL“ entfernte die Zeile samt Fokus – danach erreichten Esc und ↑ ↓ das Menü nicht mehr (der Fokus lag auf `body`). Die Zeile bleibt jetzt stehen und faltet wieder zu.
- **Nicht mit echter Eingabe geprüft:** Per `SendInput` kamen nur Mausbewegungen an, kein `mousedown` (der in `docs/e2e-testing.md` beschriebene Hook) – laut Anleitung nicht weitergeklickt. Die Menüs bestehen aus gewöhnlichen Knöpfen, der Weg des Rechtsklicks ist unverändert.
- **Nicht geprüft:** die Speichern-Felder der Matrix bis zum Dialog (unveränderte Aufrufe von `ExportActions`), „Open in the SQL editor of“ mit einer zweiten verbundenen Verbindung (unverändert aus `small-column-add-ddl`), die LINQ-Konsole (dasselbe `ResultMenu` wie der SQL-Editor).

## Review (2026-10-23)
Code-Review über den Branch in der Session (Entscheidung des Nutzers: keine eigenen Agenten für Pakete):
- **Anheften einer PK-Spalte** hieß im Zellmenü „Unpin column“ (gesperrt) – jetzt „Pinned (primary key)“ wie im Spaltenmenü.
- **Verwaiste Texte** der alten Menüs (`GridMenu_ExportGroup`, `GridMenu_CopyColumn`, `GridMenu_CopyColumnInList`, `GridMenu_CopyToConnectionGroup`, `GridMenu_RecordsGroup`, `Columns_CopyAddScript`) entfernt.
- Geprüft ohne Befund: deaktivierte Aktionen nur mit Grund (C#-Wert: `Problem` gibt es genau dann, wenn `Code` fehlt; Link auf Zeilen: `Unavailable` genau dann, wenn `Link` fehlt), Kopien aus Menüs über `DomModule`, kein SQL in der UI, `OnHeaderContextMenu` in beiden Grids mit gleicher Parameterzahl (sonst lehnt Blazor den Aufruf aus `grid.js` ab).

## Offen
- ← → durch Leiste und Matrix (Backlog).
