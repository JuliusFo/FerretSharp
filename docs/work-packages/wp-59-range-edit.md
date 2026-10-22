# WP-59 Werte für markierte Zellen setzen → Release offen

Branch `wp/59-range-edit`. „Bulk-Update“, Rückmeldung eines Kollegen 2026-10-22; vom Nutzer am selben Tag als Paket eingeplant, zusammen mit „Entf auf einen Bereich“ und „Ausfüllen nach unten“ (aus WP-58 zurückgestellt). Offene Fragen am 2026-10-22 entschieden, am selben Tag begonnen.

## Planung (aus `docs/roadmap.md`)
- **Ausgangslage:** `ChangeTracker.SetValue` prüft eine Zelle und legt je Aufruf einen Schritt in `EditHistory` ab. Entf markierte Zeilen mit einem Undo-Schritt je Zeile; auf einem Zellbereich tat Entf nichts.
- **Drei Befehle, alle als ausstehende Änderungen** (Prod wie jedes Editieren nur nach Freischaltung, ohne weitere Abfrage):
  - **Wert setzen:** Wert in der fokussierten Zelle mit dem Editor der Spalte eingeben, Ctrl+Enter übernimmt ihn für die markierten Zeilen bzw. die Zellen des Bereichs (wie Excel); dazu „Wert setzen…“ im Zellmenü.
  - **Entf auf einen Bereich:** NULL; bei markierten Zeilen bleibt Entf „Zeilen löschen“.
  - **Ausfüllen nach unten (Ctrl+D):** je Spalte der Wert der obersten Zeile in die Zeilen darunter, wie angezeigt (A21), als Wert, nicht als Text.
- **Ein Undo-Schritt je Befehl**, Entf auf mehrere Zeilen ebenso.
- **Prüfen vor dem Setzen:** einmal je Spalte; ein ungültiger Wert setzt nichts. Nicht bearbeitbare Spalten und Zellen werden übersprungen und genannt.
- **Nicht geladene Zeilen** eines Bereichs liest .NET nach, wie das Kopieren in WP-58.
- **Nicht in diesem Paket:** mehrere Zellen aus Excel einfügen, Reihen fortsetzen, Ausfüllen nach rechts/oben, mengenbasiertes `UPDATE … WHERE <Filter>`.

## Entscheidungen des Nutzers (2026-10-22)
1. **Ctrl+Enter im Zelleditor gehört dem Grid:** mit mehreren markierten Zeilen oder einem Bereich setzt es den Wert für alle, sonst übernimmt es ihn wie Enter. Außerhalb des Editors bleibt es „Filter anwenden“ (bisher lud es im Editor das Grid neu, und der getippte Wert ging verloren).
2. **Markierte ganze Spalten** nur, wenn schon alle Zeilen geladen sind (letzter Block kürzer als die Blockgröße); sonst sind die Befehle aus, mit Hinweis auf einen Zellbereich. Das mengenbasierte UPDATE bleibt im Backlog.
3. **Obergrenze:** 10 000 Zeilen je Befehl (jede geänderte Zeile wird beim Schreiben ein UPDATE über das VPN); darüber wird nichts gesetzt.
4. **Mehrspaltiger Bereich bei „Wert setzen“:** alle Zellen, jede Spalte parst den Wert selbst; ist er in einer Spalte ungültig, wird nichts gesetzt (die Meldung nennt die Spalte).
5. **Entf und NOT NULL:** NOT-NULL-Spalten bleiben, wie sie sind, und werden genannt.
6. **Ctrl+D** nur mit einem Bereich ab zwei Zeilen.

## Entscheidungen in der Umsetzung
- **Ein Weg im Core:** `ChangeTracker.SetCells(rows, columns, CellFill)` für alle drei Befehle (`CellFill.Value(text)`, `.Null`, `.Down`), Ergebnis `CellFillResult` (gesetzte Zellen, übersprungene Spalten mit Grund, ausgelassene Zeilen zum Löschen, zu genaue Zahlen, Fehler). Je Spalte wird einmal entschieden (`FillValueOf`): nicht bearbeitbar (`NotEditableReason`, LOB) → übersprungen; Wert ungültig → Fehler, nichts gesetzt; NULL in NOT NULL → übersprungen; nach unten: oberster Wert zu genau oder NULL in NOT NULL → übersprungen. Danach je Zeile: zum Löschen markiert → ausgelassen, Zahl über 28 Stellen → ausgelassen. Ein `Remember` mit den Zuständen aller berührten Zeilen. Eine Zeile, die zweimal vorkommt, zählt einmal.
- **`ChangeTracker.FillRefusal(rows, fill)`** (zu viele Zeilen, Ausfüllen mit einer Zeile) fragt das Grid **vor** dem Nachlesen nicht geladener Zeilen und das Zellmenü zum Ausgrauen – derselbe Satz wie im Tracker.
- **Übersprungene Spalten statt Ablehnung:** Ein Bereich über PK und Namen setzt den Namen und nennt den PK („ID unverändert: …“), statt den ganzen Befehl abzulehnen. Abgelehnt wird nur, was nicht passt (ungültiger Wert).
- **Wert statt Text beim Ausfüllen:** Der oberste Wert geht als Oracle-Wert nach unten (über `ChangeTracker.ValueOf` des obersten Zeilenstands, sonst des geladenen Werts) – ein Umweg über den Anzeigetext verlöre Sekundenbruchteile und Stellen.
- **Ctrl+Enter in allen drei Editoren** (Text, Enum-Liste, Flags): `fillAll` in `grid.js` fragt `fillTargetOf` (rein, `gridSelection.js`, mit Tests): `'marks'`, wenn die bearbeitete Zelle in der Markierung liegt, `'rows'`, wenn mehrere Zeilen markiert sind und ihre Zeile dazugehört, sonst `null` (dann ein normales Enter). .NET setzt alles in einem Aufruf (`OnFillValue`); der Editor schließt dann **ohne** eigenes Edit (`stopEditing(true)`), sonst bliebe ein zweiter Undo-Schritt. Ein Fehler steht unter dem Editor wie bei Enter.
- **`shortcuts.js` lässt Ctrl+Enter durch, solange eine Zelle bearbeitet wird** (`cellEditing()`) – fest, unabhängig davon, auf welche Taste „Filter anwenden“ gelegt ist.
- **Entf und Ctrl+D nur auf einem beschreibbaren Workspace** (`editState.enabled`): Auf einem gesperrten tun sie still nichts, wie Entf auf Zeilen. Ctrl+D steht in der Tastenkette **hinter** den einstellbaren Grid-Aktionen (Kopieren der Spalte, Formular, Undo/Redo): eine Aktion des Nutzers auf Ctrl+D geht vor, die Einstellungen warnen dann (`TakenFromGrid` → `SettingsText.GridFillDown`). Die festen Tasten stehen in `ShortcutMap.Fixed`.
- **Zellmenü:** bei Markierungen eine Gruppe „Bearbeiten · 3 × 4 Zellen“ mit „Wert setzen…“, „Auf NULL setzen“, „Nach unten ausfüllen“ (ausgegraut mit Grund: Spalten nicht ganz geladen, zu viele Zeilen, eine Zeile); bei mehreren markierten Zeilen in der Zeilengruppe „Wert in SPALTE setzen…“.
- **„Wert setzen…“** ist ein Dialog mit dem Feld des Formulars (`FormFieldView`, WP-21) für die geklickte Spalte – Enum-Liste mit Tippen (`typeInLists`), Flags, langer Text –, Startwert wie angezeigt. Kann die geklickte Spalte keinen Wert nehmen (PK), zeigt er den Editor der ersten markierten Spalte, die es kann. Enter setzt (im langen Text Ctrl+Enter); Fehler bleiben im Dialog.
- **Undo für mehrere Zeilen:** `ChangeTracker.Delete(IReadOnlyList<RowData>)` und `Revert(IReadOnlyList<RowKey>)` sind je ein Schritt – Entf, „Zeile löschen“ und „Änderungen verwerfen“ im Zellmenü nehmen sie.
- **Nachlesen** über `CellCopies.RowsAsync` (jetzt mit Funktionen statt `MarkedCells`): fürs Setzen die Zeilen, wie die Datenbank sie hat (`FerretGrid.ReadRowsAsync`), fürs Kopieren wie angezeigt (`ReadShownRowsAsync`).
- Kein ADR: kein neuer Schreibweg (alles bleibt ausstehende Änderung im `ChangeTracker`, geschrieben wie bisher), keine neue Abhängigkeit.

## Gebaut
- **Core:** `Data/CellFill` (`CellFill`, `CellFillKind`, `CellFillResult`, `SkippedColumn`), `ChangeTracker.SetCells`/`FillRefusal`/`MaxFillRows`, `Delete`/`Revert` für mehrere Zeilen; `ShortcutMap.Fixed` (Ctrl+Enter, Entf, Ctrl+D), `TakenFromGrid` (Ctrl+D). Texte `DataText.Fill*`, `SettingsText.Fixed*`/`GridFillDown`/`HintInCellEditor`.
- **UI-State:** `GridEditing.FillAsync`/`FillRows` (Ziele, Grenze vor dem Lesen, ein Schritt, Neuzeichnen, Namen nachschlagen), `IEditableGrid.AllRows`/`ReadRowsAsync`, `GridFill`, `FillNotice`, `SetValueRequest`; `CellCopies.RowsAsync` mit Funktionen.
- **UI:** `grid.js` (`fillAll` in den drei Editoren, `editState.fillTargets`, Entf auf Markierungen, `onFillDownKey`), `gridSelection.js` (`fillTargetOf`), `shortcuts.js` (Ctrl+Enter im Zelleditor), `FerretGrid` (`OnFillValue`, `OnClearMarks`, `OnFillDown`, `FillAsync`, `FillRowsAsync`, `FillMarksAsync`), `GridContextMenu`, `SetValueDialog`, `TabView`, CSS `.set-value-dialog`.
- **Tests:** Core `CellFillTests` (Wert in alle Zellen als ein Schritt, ungültiger Wert mit Spalte, übersprungene Spalten, NULL und NOT NULL, nach unten wie angezeigt, nicht ausfüllbarer oberster Wert, gelöschte Zeilen und zu genaue Zahlen, Wert der Datenbank beendet die Änderung, Grenze, mehrere Zeilen löschen/verwerfen), `ShortcutMapTests`; UI `GridEditingTests` (Bereich mit nachgelesenen Zeilen, ganze Spalten erst vollständig, Grenze vor dem Lesen, markierte Zeilen, Entf als ein Schritt, Meldung); JS `gridSelection.test.mjs` (`fillTargetOf`).

## E2E (2026-10-22)
Eigene Instanz (`--data-dir`, eigener WebView2-Ordner, Build im Scratchpad, `--lang=de`, dunkel), eigener Container `ferret-wp59` auf Port 1539 (danach entfernt), zuletzt mit verknüpftem Beispielmodell (`samples`). Tasten, Klicks und Rechtsklicks per CDP (Stufe 2), Zustand über die Grid-API und die Statusleiste, die Datenbankseite per sqlplus.
- **Ctrl+Enter:** Bereich UMSATZ × ANZAHL über drei Zeilen → sechs Zellen gesetzt, Editor zu, Zeile darunter unberührt; Ctrl+Z nimmt alle sechs in einem Schritt zurück, Ctrl+Y setzt sie wieder. Drei markierte Zeilen → die Spalte der Editorzelle in allen dreien. Ungültiger Wert in einem zweispaltigen Bereich → Editor bleibt offen mit „UMSATZ: „abc“ ist keine Zahl …“, nichts gesetzt. Eine einzelne Zelle → wie Enter, **keine** neue Abfrage (Log: weiterhin eine `Rows 0+`-Zeile; vorher wendete die Taste die Filter an). Im Enum-Editor (`3` → „Behörde (3)“) und im Flags-Editor (`5` → Flags 1 und 4) über je drei Zellen.
- **Entf:** Bereich NAME (NOT NULL) × KUERZEL → KUERZEL NULL, „2 Zellen gesetzt. NAME unverändert: NAME darf nicht leer sein (NOT NULL).“, keine Zeile zum Löschen markiert; Undo stellt her. Ganze Spalte in KUNDEN (150 000 Zeilen, nicht alle geladen) → Hinweis, nichts gesetzt; das Zellmenü graut die drei Einträge mit demselben Grund aus. Ganze Spalte in MITARBEITER (40 Zeilen, alle geladen) → 40 Zellen NULL, Undo in einem Schritt.
- **Ctrl+D:** oberste Zeile mit ausstehendem „ABC“ → KUERZEL und ERSTELLT_AM der drei Zeilen darunter wie angezeigt; ganze Spalte in MITARBEITER → 39 Zellen.
- **Zellmenü und Dialog:** Gruppe „Bearbeiten · 3 × 2 Zellen“ mit den drei Einträgen; „Wert setzen…“ zeigt das Feld der Spalte (Startwert der geklickten Zeile, UK-Abzeichen, Hinweis bei mehreren Spalten). „K1“ für KUERZEL × ERSTELLT_AM → Dialog bleibt mit „ERSTELLT_AM: „K1“ ist kein Datum …“. Markierte Zeilen → „Wert in KUERZEL setzen…“ setzt die Spalte; mit Enum-Liste (Tippen über `typeInLists`, Enter) ebenso.
- **Gefunden und behoben:** Nach erfolgreichem Setzen blieb der Dialog offen – `OnApply` ist ein Delegat, kein `EventCallback`, `TabView` renderte nach `_setValue = null` nicht neu (`ui.md`, „Rendern“). Jetzt `StateHasChanged` dort.
- **Schreiben:** dreimal „Z9“ in KUERZEL lehnt Oracle ab (`UK_KUNDEN_KUERZEL`, Dialog nennt Zeile und Constraint) – richtig so. Drei UMSATZ-Werte geschrieben: offene Transaktion von „Workspace 1“ in `V$TRANSACTION`, danach Rollback, keine Transaktion mehr. Log der Testinstanz ohne `[ERR]`/`[WRN]`.
- **Nicht geprüft:** echte Windows-Eingabe (`SendInput`) – die Befehle sind Tasten und Menüklicks, die Maus-Markierung stammt unverändert aus WP-58; mehr als 10 000 Zeilen (nur Unit-Tests); Lesen nicht geladener Zeilen eines Bereichs beim Setzen (Weg von WP-58, Unit-Test); Prod mit gesperrtem Workspace (Entf/Ctrl+D tun dann nichts, `editState.enabled`).

## Review (2026-10-22)
Code-Review über den Branch, alle sieben Befunde behoben:
- **Leere Zeilenliste:** `SetCells` las `rows[0]` auch ohne Zeilen – eine ganze Spalte einer leeren Tabelle (oder ein Nachlesen, das keine Zeile mehr fand) warf. Jetzt 0 Zellen ohne Fehler.
- **Ctrl+Enter speicherte die Editorzelle ein zweites Mal:** `params.stopEditing(true)` bricht in AG Grid 36 nicht ab – der Parameter heißt dort `suppressNavigateAfterEdit`, der Wert ging als eigenes Edit an .NET (meist unsichtbar, weil derselbe Wert kein Schritt ist; in einer Zeile zum Löschen öffnete sich der Editor danach mit Fehler). Jetzt `params.api.stopEditing(true)`. Das ältere `cancelEditor` geht nur deshalb, weil die Editoren dort `isCancelAfterEnd` melden.
- **Nach dem Nachlesen** prüft `FillAsync` erneut, ob gerade geschrieben wird, und ob der Tab noch denselben `ChangeTracker` hat (Commit/Rollback auf gesperrter Verbindung, neue Struktur) – sonst wird nichts gesetzt.
- **Neu gezeichnet** wird nur eine Zeile, die noch an ihrem Index steht (Zeilenschlüssel verglichen): Sortieren oder F5 während des Nachlesens zeichnete sonst die gefüllten Werte in eine andere Zeile.
- **Nur geänderte Zeilen** werden neu gezeichnet und nach Namen nachgeschlagen (`CellFillResult.Changed`), nicht alle bis zu 10 000.
- **Eine Spaltenregel:** `ChangeTracker.FillUnavailable` für Tracker und Zellmenü (vorher doppelt).
- **Dialog:** „Wert setzen…“ schließt sich nach dem Setzen selbst über sein `OnClose` (`EventCallback`); das `StateHasChanged` in `TabView` ist weg.
- Nicht erneut im E2E geprüft (Container entfernt): das Abbrechen des Editors nach Ctrl+Enter und das Schließen des Dialogs über `OnClose`.

## Offen
- Keine. Beide UX-Fragen aus dem E2E hat der Nutzer am 2026-10-22 entschieden: Es bleibt, wie es ist – „N Zellen gesetzt“ zählt auch Zellen, die den Wert schon hatten, und Ctrl+D ohne Markierung tut nichts.
