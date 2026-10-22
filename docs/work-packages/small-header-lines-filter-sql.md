# Klein: Typ/NOT NULL im Spaltenkopf abschaltbar, `SELECT t.*` als SQL eines Filters

Branch `feat/header-lines-sql-preview` (von `main`), 2026-10-22.

## Anlass
Zwei Wünsche des Nutzers: in der Datenansicht Typ und Nullability im Spaltenkopf ausblenden, beides getrennt einstellbar; beim SQL eines Filters statt jeder einzelnen Spalte `SELECT … *` ausgeben.

Entscheidungen des Nutzers:
- Typ und NOT NULL gelten im Tabellen-Grid **und** im SQL-Ergebnis-Grid (SQL-Editor, LINQ-Konsole); das Formular bleibt, wie es ist.
- `SELECT t.*` **ohne** OFFSET/FETCH und **ohne** ROWID.

Ohne Antwort übernommen (Vorschlag aus der Rückfrage): Standard bleibt das Statement des Grids; `t.*` ist per Einstellung wählbar. Die drei Schalter stehen in einem neuen Abschnitt „Datenansicht“ zwischen Darstellung und C#-Modell.

## Gebaut
- Core `AppSettings.ShowColumnTypes`, `ShowNotNull` (beide an), `FilterSql` (`FilterSqlColumns.Listed`/`Star`, Standard `Listed`).
- Core `QueryBuilder.BuildSelectStar`: `SELECT t.*`, dasselbe WHERE mit denselben Binds, ORDER BY aus der Sortierung plus Primärschlüssel – ohne ROWID (auch nicht im ORDER BY), ohne LOB-Projektionen, ohne Seite; ohne Sortierung und Schlüssel kein ORDER BY. Dafür `OrderBy(…, total: false)`: ROWID bzw. alle sortierbaren Spalten einer View ohne Schlüssel braucht nur das Blättern des Grids.
- UI State `ColumnHeaderLines` (Typ, NOT NULL): Text der letzten Kopfzeile (`TextOf`), ob es die Zeile gibt (`TypeLine`), Kopfhöhe (`HeightWith`: 62 mit C#-Zeile und Typzeile, 46 mit einer der beiden, 30 nur mit dem Namen). Den Text baut jetzt .NET; `grid.js` zeigt `meta.type`, nur wenn `meta.typeLine`.
- `FerretGrid` und `SqlResultGrid` folgen der Einstellung sofort (`AppSettingsService.Changed`): Beschriftung neu per `updateColumns` mit `keepEditing`, die Zeilen werden nicht neu gelesen. `SqlResultPresentation.Columns`/`HeaderHeight` nehmen die Kopfzeilen als Parameter.
- `TabView.ShowSql` wählt nach der Einstellung zwischen `BuildSelect` (erste Seite, wie bisher) und `BuildSelectStar`; „Im SQL-Editor öffnen“ übernimmt, was der Dialog zeigt. Der Ausführungsplan bleibt beim Statement des Grids.
- Einstellungen › Datenansicht (`DataViewSettings`, `SettingKey.HeaderTypes`/`HeaderNotNull`/`FilterSql`, `SettingsPart.DataView`), Texte Englisch und Deutsch.
- Tests: `QueryBuilderTests` (t.*, ohne Schlüssel, View), `ReadOnlyTests` (t.* passiert die Lesesperre), `SettingsStoreTests`, UI `ColumnHeaderLinesTests`, `SqlResultPresentationTests`, `SettingsCatalogTests`.

## E2E
Eigene Testinstanz mit eigenem Container (Beispielschema), per CDP: Kopf im Tabellen- und im SQL-Grid mit beiden Teilen, nur NOT NULL und ohne Zeile (30 px), jeweils sofort nach dem Umschalten mit offenem Tab; `SELECT t.*` mit Sortierung im Dialog, im SQL-Editor geöffnet und ausgeführt (FK-Badge über die Herkunft der Spalten bleibt); „Jede Spalte“ zeigt das Statement wie bisher. Bedient nur per DOM-Klick – neu ist keine Mausinteraktion, die echte Eingabe bräuchte.
