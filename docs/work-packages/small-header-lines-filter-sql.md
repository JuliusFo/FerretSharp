# Klein: Typ/NOT NULL im Spaltenkopf abschaltbar, `SELECT t.*` als SQL eines Filters → Release 3.47.0

Branch `feat/header-lines-sql-preview` (von `main`), 2026-10-22.

## Anlass
Zwei Wünsche des Nutzers: in der Datenansicht Typ und Nullability im Spaltenkopf ausblenden, beides getrennt einstellbar; beim SQL eines Filters statt jeder einzelnen Spalte `SELECT … *` ausgeben.

Entscheidungen des Nutzers:
- Typ und NOT NULL gelten im Tabellen-Grid **und** im SQL-Ergebnis-Grid (SQL-Editor, LINQ-Konsole); das Formular bleibt, wie es ist.
- `SELECT t.*` **ohne** OFFSET/FETCH und **ohne** ROWID.

Ohne Antwort übernommen (Vorschlag aus der Rückfrage): Standard bleibt das Statement des Grids; `t.*` ist per Einstellung wählbar. Die drei Schalter stehen in einem neuen Abschnitt „Datenansicht“ zwischen Darstellung und C#-Modell.

## Gebaut
- Core `AppSettings.ShowColumnTypes`, `ShowNotNull` (beide an), `FilterSql` (`FilterSqlColumns.Listed`/`Star`, Standard `Listed`).
- Core `QueryBuilder.BuildSelectStar`: `SELECT t.*`, dasselbe WHERE mit denselben Binds, ORDER BY aus der Sortierung plus Row-Key – ohne ROWID in der Spaltenliste, ohne LOB-Projektionen, ohne Seite. Eine Tabelle ohne Primärschlüssel endet ihr ORDER BY mit `t.ROWID` (Entscheidung des Nutzers im Review): Der SQL-Editor liest weitere Seiten durch erneutes Ausführen, eine nicht eindeutige Reihenfolge wiederholte oder übersprang Zeilen. Eine View ohne Schlüssel hat nur die Sortierung (`OrderBy(…, keylessViewByAllColumns: false)`), ohne Sortierung kein ORDER BY.
- UI State `ColumnHeaderLines` (Typ, NOT NULL): Text der letzten Kopfzeile (`TextOf`), ob es die Zeile gibt (`TypeLine`), was sie verbirgt, für den Tooltip des Kopfs (`HintOf`), Kopfhöhe (`HeightWith`: 62 mit C#-Zeile und Typzeile, 46 mit einer der beiden, 30 nur mit dem Namen). Den Text baut jetzt .NET; `grid.js` zeigt `meta.type`, nur wenn `meta.typeLine`, und `meta.typeHint` im Tooltip.
- Die Kopfzeilen laufen über die Präsentationsschicht: `PresentationService.Header` mit eigenem Ereignis `HeaderChanged` (kein `Changed`, das die Zeilen neu läse). `FerretGrid` und `SqlResultGrid` beschriften dann neu per `updateColumns` mit `keepEditing`; eine Änderung während des Aufbaus holen sie danach nach (R4a, im Review gefunden). `SqlResultPresentation.Columns`/`HeaderHeight` nehmen die Kopfzeilen als Parameter.
- `FilterStatement.Of` (UI State, getestet) wählt nach der Einstellung zwischen `BuildSelect` (erste Seite, wie bisher) und `BuildSelectStar`; `TabView.ShowSql` ruft es auf, „Im SQL-Editor öffnen“ übernimmt, was der Dialog zeigt. Der Ausführungsplan bleibt beim Statement des Grids.
- Einstellungen › Datenansicht (`DataViewSettings`, `SettingKey.HeaderTypes`/`HeaderNotNull`/`FilterSql`, `SettingsPart.DataView`), Texte Englisch und Deutsch.
- Tests: `QueryBuilderTests` (t.*, ohne Schlüssel, View), `ReadOnlyTests` (t.* passiert die Lesesperre), `SettingsStoreTests`, UI `ColumnHeaderLinesTests`, `SqlResultPresentationTests`, `FilterStatementTests`, `PresentationServiceHeaderTests`, `SettingsCatalogTests`.

## E2E
Eigene Testinstanz mit eigenem Container (Beispielschema), per CDP: Kopf im Tabellen- und im SQL-Grid mit beiden Teilen, nur NOT NULL und ohne Zeile (30 px), jeweils sofort nach dem Umschalten mit offenem Tab; `SELECT t.*` mit Sortierung im Dialog, im SQL-Editor geöffnet und ausgeführt (FK-Badge über die Herkunft der Spalten bleibt); „Jede Spalte“ zeigt das Statement wie bisher. Nach dem Review: Tooltip des Kopfs mit verborgenem Typ, `SELECT t.*` einer Materialized View ohne Schlüssel endet mit `ORDER BY t.ROWID` und läuft im SQL-Editor. Bedient nur per DOM-Klick – neu ist keine Mausinteraktion, die echte Eingabe bräuchte. Die Race beim Aufbau des Grids ist nicht nachgestellt (nur per Code und Muster R4a).
