# Klein: SQL-Ergebnisse sortieren, Spalten- und Verwendungsansicht sortierbar → Release offen

Branch `feat/sort-results-and-column-lists` (von `main`), 2026-10-23.

## Anlass
Zwei Wünsche des Nutzers, die zusammenhängen: die Spalten eines SQL-Ergebnisses per Klick auf den Kopf sortieren – sind es weniger Zeilen als ein Block, ohne neues Lesen – und die Zeilen der Spalten- und der Verwendungsansicht nach ihren Spalten sortieren.

Entscheidungen des Nutzers:
- Beides in einem kleinen Paket.
- SQL-Ergebnisse **im Speicher** sortieren, wenn alle Zeilen geladen sind; sonst sortiert die Datenbank mit einem ORDER BY nach Position.

Ohne Rückfrage entschieden (einfachste Variante, Fragen unten):
- **ORDER BY in der Abfrage statt immer umschließen:** `SELECT * FROM (…)` scheitert an zwei Spalten gleichen Namens (ORA-00918, `oracle.md`: Joins von EF Core). Deshalb kommt das ORDER BY nach Position in die Abfrage selbst – vor ihr eigenes ORDER BY, das dann Gleichstände ordnet, sonst angehängt. Das entspricht der stabilen Sortierung im Speicher: Zeilen mit gleichem Wert bleiben in der Reihenfolge der Abfrage, auch von Seite zu Seite. Umschlossen wird nur, wenn die Abfrage oben selbst begrenzt (`FETCH`/`OFFSET`, auch `ORDER SIBLINGS BY`) – sortiert werden dann genau ihre Zeilen. `WHERE ROWNUM <= n` begrenzt vor dem ORDER BY und braucht das nicht.
- Begrenzt eine Abfrage mit gleichnamigen Spalten ihre Zeilen, kann die Datenbank sie nicht sortieren: Die Köpfe sortieren dann erst, wenn alle Zeilen geladen sind (sie werden dann neu beschriftet); bis dahin sagt der Tooltip des Kopfs, warum (`GridColumn.SortHint`, im E2E statt des Typ-Hinweises der LOBs).
- Reihenfolge im Speicher wie Oracle in einer FerretSharp-Session (`NLS_SORT=BINARY`): Text nach Zeichen (ordinal), Zahlen exakt (auch jenseits von `decimal`), BINARY_DOUBLE mit NaN über Unendlich, RAW nach Bytes, Zeit mit Zone nach dem Zeitpunkt; NULL aufsteigend zuletzt, absteigend zuerst. INTERVAL liest der SQL-Editor als Text: Danach sortiert nur die Datenbank.
- Spalten- und Verwendungsansicht: Klick auf den Kopf aufsteigend, absteigend, zurück zur Reihenfolge des Schemas; Text in der Kultur der Oberfläche ohne Groß/klein (wie andere Listen), Zeilen ohne Wert (ohne Property, ohne Kommentar) in beiden Richtungen zuletzt. Lesen/Schreiben nach den Zugriffen im Anwendungscode, dann in Tests. Die Sortierung bleibt, solange die Ansicht gemountet ist, und wird nicht gespeichert.

## Gebaut
- Core `SqlResultOrder` (`Query/SqlEditor`): `Sorted(query, columns, sorts)` – ORDER BY nach Position in der Abfrage oder um sie, null, wenn die Datenbank nicht sortieren kann; `CanSort`. Die oberste Ebene findet der Tokenizer des SQL-Editors (Klammern, Kommentare, Literale zählen nicht). Binds bleiben, der Text beginnt weiter mit `SELECT`/`WITH` und passiert die Lesesperre.
- Core `RowOrder` (`Data`): `CanSort(column)`, `Sort(rows, sorts)` (stabil), `Compare` – Regeln wie oben.
- UI State `ResultOrder`: Sortierung der Köpfe (`Of` aus AG Grids Sort-Model), `Apply` (neue Ordnung → Blöcke veraltet), `Ordered` (alle Zeilen in der Ordnung, wenn im Speicher bekannt), `Query` (zum Lesen in der Ordnung – auch für „Alle Werte kopieren“ und „Alle Zeilen exportieren“), `Sortable(column)`, `Loaded(all)` (alle Zeilen in der Reihenfolge der Abfrage gelesen).
- `SqlResultGrid`: `GetRows` nimmt das Sort-Model; eine neue Ordnung leert die Blöcke; im Speicher kommen die Blöcke ohne Lesen und mit bekannter Zeilenzahl (der Snapshot bleibt), sonst liest die sortierte Abfrage. Markierte Bereiche, Spaltenwerte und Export folgen der Ordnung. `SqlResultPresentation.Columns` bekommt die `ResultOrder` (`Sortable`, `SortHint`). Kopfklick und Sort-Model gab es in grid.js für Tabellen schon; neu ist nur, dass `updateColumns` auch `sortable` der Column-Def nachzieht (im E2E gefunden: Die Köpfe sortierten nach dem Laden aller Zeilen zwar über den eigenen Header, AG Grid selbst hielt die Spalte aber für nicht sortierbar) und der Tooltip `meta.sortHint` zeigt.
- `ListOrder<TKey>` (UI State) und `SortHeader` (Common): Kopf mit Knopf über die ganze Zelle, Pfeil ↑/↓, `aria-sort`. `ColumnsView` (#, Name, Typ, Property, C#-Typ, Null, Default, Kommentar) und `UsageView` (Name, Property, Lesen, Schreiben). CSS `table.columns .sort-btn`.
- Tests: Core `SqlResultOrderTests`, `RowOrderTests`; Integration `SqlResultOrderTests` (jede Form läuft gegen Oracle; für jede Spalte beider Richtungen gleiche Reihenfolge im Speicher und in der Datenbank); UI `ResultOrderTests`, `ListOrderTests`.

## E2E (2026-10-23)
Eigene Instanz (`--data-dir`, eigener WebView2-Ordner, deutsch, dunkel), eigener Container `ferret-e2e-sort` mit dem Beispielschema (danach entfernt), verknüpft mit einer Kopie des Beispielmodells im Scratchpad samt `Sample.slnx` und einer Klasse, die einige KUNDEN-Spalten liest und schreibt. Bedient per CDP, Statements in `V$SQL` des Containers nachgesehen.
- Großes Ergebnis (KUNDEN, über 130 000 Zeilen): Klick auf NAME liest mit `… FROM kunden ORDER BY 2` (drei Ausführungen für drei Blöcke), über die Blockgrenze 499/500 hinweg sortiert.
- Kleines Ergebnis (40 Zeilen, `ORDER BY kunde_id DESC`): nach Name, nach Umsatz absteigend – NULL vorn, gleiche Namen in der Reihenfolge der Abfrage, Fußzeile ohne Lesezeit, in `V$SQL` nur die eine Ausführung der Abfrage selbst.
- `FETCH FIRST 800` mit eindeutigen Namen: `SELECT * FROM (…) ORDER BY 3 DESC`, nur die 800 Zeilen. `FETCH FIRST 700` mit zweimal `KUNDE_ID`: Köpfe zuerst nicht sortierbar, mit dem Hinweis; nach dem Laden aller Zeilen sortierbar und im Speicher sortiert.
- LINQ-Konsole (`db.Kunden.Where(…).Select(…)`, 30 Zeilen): sortiert im Speicher.
- Spaltenansicht KUNDEN: Name auf/ab/zurück, Property (ANZAHL ohne Property in beiden Richtungen zuletzt), Null (NOT NULL zuerst). Verwendungsansicht: Lesen und Schreiben absteigend, Gleichstände in Schema-Reihenfolge, Property aufsteigend mit ANZAHL zuletzt.
- Log ohne `[ERR]`/`[WRN]`.
- **Nicht mit echter Eingabe geprüft:** `SendInput` brachte Bewegungen, aber kein `mousedown` ins WebView (Hook wie in `e2e-testing.md` beschrieben); nach der Regel dort nicht weitergeklickt. Die Klicks sind einfache Klicks auf einen Knopf bzw. den Kopf von AG Grid (derselbe Weg wie im Tabellen-Grid).

## Offen / Fragen
- Ein laufender Block (langsame Abfrage) wird bei einem Klick auf den Kopf nicht abgebrochen wie im Tabellen-Grid (`PageLoads.Supersede`): Die neue Ordnung wartet, bis er da ist. Bisher nicht gebraucht, weil das Ergebnis-Grid nicht sortierte.
- Sortierung im Speicher, die die Datenbank nicht nachbilden kann (gleichnamige Spalten mit `FETCH`): Export aller Zeilen und Spaltenwerte, die neu lesen, kommen dann in der Reihenfolge der Abfrage.
