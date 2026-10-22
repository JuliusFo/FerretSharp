---
paths:
  - "src/FerretSharp.UI/Components/Grid/**"
  - "src/FerretSharp.UI/Components/Sql/SqlResultGrid*"
  - "src/FerretSharp.UI/State/Grid/**"
  - "src/FerretSharp.Core/Data/RowBlocks*"
  - "src/FerretSharp.UI/wwwroot/js/grid.js"
  - "src/FerretSharp.UI/wwwroot/js/gridSelection.js"
  - "tests/js/**"
---

# Grid (AG Grid Community, Infinite Row Model)

- Datenblöcke à 500 und Sortierung kommen aus .NET (`IDataAccess`), gekapselt in `FerretGrid` (Tabellen) bzw. `SqlResultGrid` (SQL/LINQ) über `GridBridge` + `wwwroot/js/grid.js`; Spalten als `GridColumn`, Rohzeilen der geladenen Blöcke in `RowBlocks`. Zellen gehen als fertig formatierte Strings über die Grenze (null = NULL), Spalten-IDs `c0`, `c1` … (Oracle-Namen dürfen Punkte enthalten).
- Ein Ergebnis-Grid (`table: false`) meldet Fokus und Formular-Taste nur mit der Option `form` (WP-56); alles andere, was `table` steuert (Kopfmenü, Entf, Anheften, Scrollposition), bleibt Tabellen vorbehalten.
- AG Grid liegt lokal unter `wwwroot/lib/ag-grid/` (Herkunft/Hash in der README dort, kein CDN). Keine Enterprise-Features (Kontextmenü, Zellbereich) – eigene Lösungen in Blazor.
- Seit AG Grid 36 fehlt das `ValidationModule` in `AllCommunityModule`: falsche oder veraltete Optionen werden **still ignoriert**. Nach einem Update oder neuen Optionen im E2E `agGrid.enableDevValidations()` aufrufen und ein Grid neu anlegen (WP-36 fand so `tooltipValueGetter` → `tooltip`).
- Eigene Overlays des Infinite Row Model („No Rows“, seit 35) sind per `suppressOverlays` aus – Zeilenzahl und Fehler zeigt die Fußzeile des Tabs.
- Laden zeigt `FerretGrid` selbst (Fix 3.45.1, `PageLoads`): Ein Block, der länger als `ShowAfter` braucht, bekommt `RunProgress` über dem Grid, die Fußzeile „Lädt …“. AG Grid zeigt nach `purgeInfiniteCache` nur **eine** Skeleton-Zeile (`infiniteInitialRowCount: 1`) – leer aussehend. Wer neu lädt (`refresh`, `reload`, `setSorts`, Dispose), ruft vorher `_loads.Supersede()`: grid.js fragt nur einen Block zur Zeit (`maxConcurrentDatasourceRequests: 1`), und die laufende Abfrage hält das Gate der Session – die neue wartete sonst dahinter (ein verworfener Block zählt in AG Grid bis zu seiner Antwort weiter gegen das Limit). Ein Klick auf den Kopf meldet sich über `onSortChanged` → `OnSortChanged` **mit** der neuen Sortierung: Läuft nichts, kommt das Ereignis erst nach dem `GetRows` der neuen Ordnung an; ein Laden dieser Ordnung bleibt (`Supersede(keep)`). Das Ergebnis eines verdrängten Ladens wird verworfen (`End` → false).
- Farben nur als Tokens in `ferretsharp.css` (`--accent`, `--surface`, `--text`, `--grid-*`); `grid.js` liest sie beim Erzeugen und bei jedem Theme-Wechsel.
- grid.js loggt gescheiterte Aufrufe per `callFailed` statt `.catch(() => {})`.
- Objekte in Column-Defs (`headerComponentParams`) werden mit `defaultColDef` **tief kopiert** – veränderliche Metadaten als Funktion (`getMeta`). Kopfhöhe nur als Theme-Parameter, nicht als `headerHeight` (setzt die Zeilenhöhe zurück). Angeheftete Zeilen haben `row-index="t-0"`.
- `GridBridge.CallAsync(…, params object?[] args)`: Eine Liste als einziges Argument immer als `List<…>` übergeben, **kein Array** – ein `GridRowUpdate[]` wird wegen Array-Kovarianz selbst zum params-Array, grid.js bekommt ein Objekt statt einer Liste.
- AG Grid startet die Bearbeitung beim Tippen nur ohne Ctrl, Alt und Meta. Windows meldet AltGr als Ctrl+Alt: `@ € \ { }` startet `onAltGrKey` selbst (`startEditingCell` mit `key`, kommt als `eventKey` im Editor an). Jeder Editor muss `eventKey` übernehmen – der Enum-Editor verlor die Taste bis zu `small-enum-editor-typing.md`; seine Suche nach Wert und Beschriftung steht im reinen Modul `cellTyping.js`.
- `stopEditingWhenCellsLoseFocus` beendet eine Zelleingabe erst **nach** dem Klick, der den Fokus weggenommen hat (Folgen für Knöpfe außerhalb: `ui.md`).
- Sortierung von .NET aus setzen (`setSorts`, Filter-Vorlagen WP-37): `applyColumnState` mit `defaultState: { sort: null }` lässt das Infinite Row Model selbst ab Zeile 0 neu laden – nicht zusätzlich `refresh` aufrufen, sonst zwei Abfragen.
- AG Grid ruft Event-Callbacks wie `onCellFocused` **asynchron** auf – nach dem `keyup` der Taste, die den Fokus bewegt hat (Bild↓ erst nach dem Scrollen). Wer im Callback wissen muss, ob eine Taste den Fokus bewegt hat, merkt sie sich über das `keyup` hinaus (Zeilenauswahl: 250 ms, Fokuswechsel aus .NET verwerfen sie). Zeilen liegen seit 36 in `.ag-grid-scrolling-container`, weggescrollte fehlen im DOM – Auswahl über `agGrid.getGridApi(element)` prüfen.
- AG Grids eigene Auswahl-Tasten taugen im Infinite Row Model nicht: Ctrl+A wählt nur im Client-Side Row Model (`canSelectAll`) und ruft trotzdem `preventDefault`, die Leertaste kennt den Anker von `grid.js` nicht. Beide fängt `onSelectKey` vorher ab. Zeilen, die noch nicht geladen sind, wählt `selectArrived` beim Ankommen nach (`span`) – eine Auswahl „alle“ oder „bis zum Ende“ nie über Indizes außerhalb der geladenen Blöcke versuchen. Auch `onCellClicked` kommt asynchron: Was ein Klick sofort beenden muss (den `span`), im `mousedown` erledigen.
- Kopieren (Ctrl+C, Als Tabelle/INSERT/C#) nimmt den ausstehenden Wert, wie angezeigt und wie das Formular (Entscheidung des Nutzers, A21).
