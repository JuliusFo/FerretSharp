# WP-57 Werte einer Spalte kopieren → Release offen

Branch `wp/57-copy-column-values`. Aus dem Backlog („Export und Zwischenablage“: Frage des Nutzers nach Ctrl+Shift+↓ wie in Excel), vom Nutzer am 2026-10-21 als Paket eingeplant und am selben Tag begonnen.

## Planung (aus `docs/roadmap.md`)
- **Ausgangslage:** Das Grid markiert nur ganze Zeilen (Zellbereiche sind ein Enterprise-Feature von AG Grid). Ctrl+Shift+↑/↓ markiert Zeilen bis zur ersten/letzten, Ctrl+C kopiert mehrere Zeilen mit allen Spalten (`small-grid-select-all.md`). Die Werte einer einzelnen Spalte ließen sich nicht kopieren.
- **Zellmenü „Spalte X kopieren“** (Tabelle, SQL-Ergebnis, LINQ-Konsole): die Werte dieser Spalte aus den markierten Zeilen, in Grid-Reihenfolge, ein Wert je Zeile, wie angezeigt mit ausstehenden Änderungen (wie Ctrl+C, A21). Neue Aktion `ShortcutAction.CopyColumnValues` ohne Standardtaste, lokal im Grid. Nicht Ctrl+Shift+C: Das ist in WebView2 die Elementauswahl der DevTools.
- **Kopfmenü „Werte kopieren“** (Tabelle): alle Werte der Spalte unter dem aktiven Filter und in der Sortierung des Tabs, gelesen wie „Alle Zeilen“ (WP-54) im Hintergrund mit Zeilen und Abbrechen in der Statusleiste. Geschriebene Änderungen sind dabei, ausstehende nicht. Darüber eine Obergrenze.
- **SQL-Ergebnis:** ein eigenes, schmales Kopfmenü. „Alle Zeilen“ führt die Abfrage noch einmal aus. LINQ-Konsole: nur markierte Zeilen.
- **Format:** ein Wert je Zeile, NULL als leere Zeile. Wahlweise als `IN`-Liste für SQL mit Datenbankwerten; der Text entsteht in `Core/Query`.

## Entscheidungen des Nutzers (2026-10-21, alle Vorschläge übernommen)
1. **Obergrenze** 100 000 Werte. Darüber wird **nichts** kopiert, mit Verweis auf „Alle Zeilen“ als CSV/Excel: Eine still gekürzte Liste, vor allem eine `IN`-Liste, sähe vollständig aus.
2. **`IN`-Liste in der ersten Stufe:** ja, als zweiter Eintrag in beiden Menüs. Kopiert wird nur die Werteliste (`'A', 'B'`), ohne Spaltennamen. Ab 1000 Werten gibt es einen Hinweis auf ORA-01795.
3. **„Nur verschiedene Werte“** gibt es nicht als eigene Variante: Die `IN`-Liste ist immer eindeutig (beim Kopfmenü schon per `DISTINCT` in der Abfrage), die normale Liste behält Duplikate und die Reihenfolge.
4. **Anzeige- oder Rohwert:** Die normale Liste übernimmt die Werte wie angezeigt (Enum-Member), die `IN`-Liste immer die Datenbankwerte.
5. **Kopfmenü im SQL-Ergebnis:** nur die beiden Einträge, kein „Anheften“.
6. **LINQ-Konsole:** nur das Zellmenü (und die Taste) mit den markierten Zeilen, kein Kopfmenü.

## Entscheidungen in der Umsetzung
- **Eigene Abfrage nur mit der Spalte** (`QueryBuilder.BuildColumnValues`): WHERE und ORDER BY wie `BuildSelectAll`, in der Select-Liste nur die Spalte. Über das VPN geht so nur ein Wert je Zeile, nicht die ganze Zeile. Für die `IN`-Liste steht `SELECT DISTINCT` da, sortiert nach dem Wert selbst (mit DISTINCT sortiert Oracle nur nach Ausgewähltem). Gelesen wird über `IDataAccess.ReadColumnAllAsync`, denselben Cursor-Weg wie WP-54 (`ExecuteStreamingReaderAsync`, Blöcke à 500, Stand der Session, kein neuer Snapshot).
- **SQL-Ergebnis:** ganze Zeilen über `ReadSqlAllAsync` (lehnt ab, wenn die Abfrage inzwischen andere Spalten liefert), daraus die Spalte. `SELECT col FROM (…)` würde an den doppelten Spaltennamen von Joins scheitern (ORA-00918), wie bei `ReadSqlAsync`.
- **Zwischenablage nach dem Lesen im Hintergrund über den Host** (`IClipboardService`, WPF `Clipboard.SetDataObject` auf dem UI-Thread, bis zu fünf Versuche): `navigator.clipboard.writeText` verweigert, solange das Fenster nicht vorn ist. Wer während des Lesens schon zu Excel wechselt, bekäme sonst „Zwischenablage nicht verfügbar“. Außerdem ist das Menü samt dom.js-Modul dann schon zu. Die markierten Zeilen (Menü, Taste) kopieren weiter über die Seite, wie alle anderen Kopien.
- **Statusleiste:** `FileExport` hat jetzt `Label` und `Title` statt `FileName` und trägt neben Dateien auch „ART → Zwischenablage“. Abbrechen und Zeilen wie beim Export.
- **Obergrenze im Lesen:** `ColumnValues.Add` liefert ab dem 100 001. Wert `false`, der Block-Handler hört damit auf, und das Lesen endet. Bei der `IN`-Liste zählen nur verschiedene Werte.
- **Zeilen-Format:** Notation von Ctrl+C (`ExportNotation`: Zahlen deutsch ohne Gruppierung, volle Werte). Ein Wert mit Zeilenumbruch, Tab oder Anführungszeichen steht in Anführungszeichen, wie Excel es beim Einfügen liest (`DelimitedExport.AppendField`, aus `AppendLine` herausgezogen). Am Ende steht keine Zeilenschaltung (für das Einfügen in Editor oder Filter).
- **`IN`-Liste:** Literale über `InsertExport.Literal` (`TO_DATE(…)`, `N'…'`, `HEXTORAW(…)`, Hochkommas verdoppelt), durch Komma getrennt in einer Zeile. Duplikate entfallen auch bei den markierten Zeilen (im Speicher, nach dem Literal), NULL entfällt mit Hinweis („IN findet NULL nie“).
- **Nur Spalten, die als Wert gelesen werden** (`ColumnValues.CanCopy`, gleichbedeutend mit `Projection.Value`): Für LOB, LONG, XMLTYPE und Ähnliche sind die Einträge deaktiviert, mit Tooltip. Die Taste tut dort nichts.
- **Wo die Einträge stehen:** Im Zellmenü stehen sie in der Exportgruppe direkt unter „Als Tabelle kopieren“ (`ExportMenuItems.Column`), damit haben Tabelle, SQL-Ergebnis und LINQ-Konsole sie ohne eigene Stelle. Im Kopfmenü bilden sie eine Gruppe mit der Überschrift von WP-54 („Alle Zeilen des Filters · N Zeilen“ bzw. „… des Ergebnisses“) in `ColumnValuesMenuItems`, gemeinsam für `ColumnMenu` und das neue `ResultColumnMenu`. Das Kopfmenü des Ergebnisses meldet grid.js nur mit `GridOptions.HeaderMenu` (SQL-Editor = `ExportsAllRows`).
- **Taste:** `copyColumn` in `ShortcutService.LocalCombos`, `onCopyColumnKey` in grid.js für beide Grids. Sie kopiert die markierten Zeilen, ohne Markierung die fokussierte Zeile, immer im Zeilen-Format.
- Kein ADR: Die Wege (Leseweg mit Cursor, Export im Hintergrund) stehen seit WP-54 (ADR 0029). Der Host-Dienst für die Zwischenablage ist ein schmales Interface wie `IFileSaveService`.

## Gebaut
- **Core:** `QueryBuilder.BuildColumnValues`, `IDataAccess.ReadColumnAllAsync` (`OracleDataAccess`), `Query/ColumnValues` (`ColumnValuesFormat`, `ColumnValuesText`, `MaxValues`, `CanCopy`), `DelimitedExport.AppendField`, `ShortcutAction.CopyColumnValues`. Texte `QueryText.ColumnValues*`, `SettingsText.ActionCopyColumnValues`/`HintCopyColumnValues`.
- **UI-State:** `ColumnValueCopies` (`ForTable`, `ForResult`, `CopyRowsAsync`, `CopyAllAsync`), `AllColumnValues`, `ExportActions.CopyColumnAsync`, `FileExport.Label`/`Title`, `GridOptions.HeaderMenu`, Host `IClipboardService`.
- **UI:** `ExportMenuItems` (Einträge für die geklickte Spalte), `ColumnValuesMenuItems`, `ColumnMenu` (mit `Tab`), `ResultColumnMenu`, `SqlResultGrid` (`OnHeaderContextMenu`, `OnCopyColumn`), `FerretGrid.OnCopyColumn`, `grid.js` (`headerMenu`, `onCopyColumnKey`), `ExportStatus`. Texte `GridText.GridMenu_CopyColumn*`, `ColumnMenu_CopyValues`/`CopyInList`/`AllValuesTitle`/`AllResultValuesTitle`, `ColumnValues_*`, `ShellText.Status_CopyValues*`.
- **App:** `ClipboardService`.
- **Tests:** Core `ColumnValuesTests` (Zeilen und Notation, Anführungszeichen wie Excel, Präsentation, `IN`-Liste mit Literalen, eindeutig, ohne NULL, Hinweis ab 1000, Obergrenze, Duplikate zählen nicht, `CanCopy`) und `QueryBuilderTests` (Spaltenabfrage wie „alle Zeilen“, DISTINCT, LOB abgelehnt). UI `ColumnValueCopiesTests` (Filter beim Öffnen des Menüs, DISTINCT nur für die `IN`-Liste, SQL-Ergebnis noch einmal, markierte Zeilen wie angezeigt mit fehlenden, `IN`-Liste, nur NULL, Zwischenablage verweigert, Hintergrund mit Statusleiste und Kopie am Ende, Obergrenze bricht das Lesen ab, Abbrechen, DB-Fehler). Integration `OracleDataAccessTests` (Werte in der Reihenfolge des Grids, DISTINCT mit NULL).

## E2E (2026-10-21)
Eigene Instanz (`--data-dir`, eigener WebView2-Ordner, Build im Scratchpad, `--lang=de`), eigener Container `ferret-e2e-wp57` auf Port 1535 (danach entfernt), Testverbindung danach in der App gelöscht (mit dem Eintrag im Credential Manager). Menüs und Tasten per CDP; Rechtsklick auf den Kopf des SQL-Ergebnisses, der Klick auf „Spalte X kopieren“ und die Taste mit echter Windows-Eingabe (`SendInput`), die Zwischenablage über `Get-Clipboard`.
- Kopfmenü KUNDEN (150 000 Zeilen): „Werte kopieren“ auf KUNDE_ID bricht nach gut 100 000 Werten ab, kopiert nichts und meldet es; die Statusleiste zeigt währenddessen „KUNDE_ID → Zwischenablage · 86.000 Zeilen · Abbrechen“. „Als IN-Liste kopieren“ auf KUERZEL (CHAR(3)) liefert 500 Literale und den Hinweis auf einen NULL-Wert.
- AUFTRAG, absteigend nach KUNDE_ID sortiert: „Werte kopieren“ auf STATUS liefert 50 000 Zeilen in der Reihenfolge des Grids. Auf NOTIZ (CLOB) sind beide Einträge deaktiviert, mit Tooltip.
- Zellmenü nach Ctrl+A: beide Einträge in der Exportgruppe („Export · 500 Zeilen“); der echte Klick auf „Spalte KUNDE_ID kopieren“ kopiert die 500 geladenen Werte über die Seite.
- SQL-Ergebnis (Join, 1200 Zeilen): Der echte Rechtsklick auf den Spaltenkopf öffnet das neue Menü mit „Alle Zeilen des Ergebnisses“. Die `IN`-Liste ergibt 71 verschiedene Werte mit dem Hinweis auf 1129 NULL-Werte, „Werte kopieren“ 1200 Zeilen.
- Taste: in den Einstellungen mit Ctrl+Shift+K belegt (Ctrl+Alt lehnt die Seite wie vorgesehen als AltGr ab). Vier markierte Zeilen kopieren vier Werte; ein ausstehender, nicht geschriebener Wert kommt so, wie er angezeigt wird.
- Log der Testinstanz ohne `[ERR]`/`[WRN]`.
- **Nicht geprüft:** die LINQ-Konsole (kein verknüpftes C#-Projekt in der Testverbindung) und damit auch Enum-Member in der Liste (Logik in `ColumnValuesTests`/`ColumnValueCopiesTests`); das Kopieren nach dem Lesen im Hintergrund, während ein anderes Fenster vorn ist (der Grund für `IClipboardService`); Abbrechen über die Statusleiste mit echtem Klick (Logik in `ColumnValueCopiesTests`); das dunkle Theme (keine neuen Stile); das Lesen über das VPN.

## Nachtrag: Code-Review (2026-10-21)
Acht Befunde, alle umgesetzt:
- **NULL bei der `IN`-Liste aus der Tabelle:** Mit `SELECT DISTINCT` sind alle NULL-Zeilen eine, der Hinweis sagte deshalb immer „1 NULL-Wert“ (im E2E bei KUNDEN.KUERZEL gesehen). Er nennt dann keine Zahl mehr (`ColumnValues.NullsCollapsed`, gesetzt über `AllColumnValues.DistinctInQuery`). Markierte Zeilen und das SQL-Ergebnis zählen weiter.
- **TIMESTAMP(8) und (9):** Gelesen wird als `DateTime` (100 ns), ein Literal eines Werts mit feineren Bruchteilen fände seine Zeile nicht. Die `IN`-Liste solcher Spalten sagt es jetzt (`QueryText.ColumnValuesFractionsCut`).
- **Tasten des Grids:** `ShortcutMap.Check` lehnt Ctrl+C für eine Aktion im Grid ab (die Kopier-Taste des Grids kommt zuerst, die Aktion bekäme sie nie) und warnt bei Ctrl+A, Ctrl+Leertaste und Ctrl+Shift+↑/↓ (die Aktion nimmt dem Grid die Taste). Gilt auch für die Formular-Taste.
- **Menüs des SQL-Ergebnisses** schließen, wenn das Grid eine neue Abfrage startet; ihre Zeilen- und Spaltenindizes zeigten sonst ins neue Ergebnis.
- Aufgeräumt: Überschrift und Zeilenzahl aus `AllRowsExports` (`Heading`, `TotalOf`) statt einer Kopie; `ColumnValueCopies.CopyRowsAsync` nimmt die geladenen Zeilen und die Zahl der fehlenden statt Platzhaltern; Kommentar der Optionen in `grid.js`; geschätzte Menühöhe der Gruppe.

## Offen
- Kopfmenü der LINQ-Konsole: „alle Werte“ zusammen mit „alle Zeilen“ (Backlog, WP-54).
