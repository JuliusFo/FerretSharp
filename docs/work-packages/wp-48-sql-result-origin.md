# WP-48 FK-Navigation und C#-Namen im Ergebnis des SQL-Editors ([#14](https://github.com/JuliusFo/FerretSharp/issues/14))
Branch `wp/48-sql-result-origin`. Planung: `docs/roadmap.md` › WP-48.

## Entscheidungen des Nutzers (2026-10-15)
- Paket vorgezogen (vor WP-38).
- Anzeigespalten (Namen neben FK-Werten) im SQL-Ergebnis: **nicht** in diesem Paket (ADR 0022 bleibt).
- LINQ-Konsole kommt mit, ohne eigene Herkunft aus dem EF-Modell: Sie teilt das `SqlResultGrid`, die Herkunft kommt aus dem SQL, das EF erzeugt.
- Springbare Spalten im Kopf dezent kennzeichnen, wie im Tabellen-Grid.
- Spike zuerst.

## Spike: Herkunft der Spalten aus ODP.NET (2026-10-15)
Gemessen mit ODP.NET managed 23.x gegen Oracle Free 23.26 (Testcontainer), festgehalten in `SqlResultOriginDriverTests` (Integration.Tests, 25 Fälle). Gelesen werden `BaseSchemaName`, `BaseTableName`, `BaseColumnName` aus `GetSchemaTable()`.

**ODP.NET zerlegt das Statement selbst auf dem Client**, die Datenbank liefert keine Herkunft. Daraus folgt:
- **Ohne `CommandBehavior.KeyInfo`** sind die drei Felder schon gefüllt: ein Roundtrip (die Ausführung selbst), nichts extra. `KeyInfo` kostet beim ersten Mal **+2 Roundtrips** und bis zu ~1000 rekursive Dictionary-Abfragen auf dem Server (danach gecacht) und liefert nur zusätzlich `IsKey`. Das kennen wir aus dem Schema → **kein KeyInfo**.
- **Tabelle so, wie sie im SQL steht:** Synonyme kommen als Synonym zurück (`S_PRODUKT`, `FERRET_KATEGORIE`), Views als View. Das Schema nur, wenn es im SQL steht, sonst leer.
- **Gut:** einfache Abfragen, `*` und `k.*`, Spaltenaliase (`KUNDE_ID AS ID` → Basis `KUNDE_ID`), Kleinschreibung, Joins (ANSI und mit Komma) mit **unquotierten** Aliasen, Tabellenname als Präfix, quotierte Tabellen und Spalten (`"MixedCase"."Id"`), `GROUP BY`/`DISTINCT` auf Spalten, Bind-Variablen, Kommentar vorweg, ROWID.
- **Keine Herkunft (leer, also sicher):** Ausdrücke, Aggregate, Analytik, `CASE`, skalare Unterabfragen, `WITH` (alle Spalten), `JOIN … USING` (nur die Spalte, ohne Tabelle).
- **Alias statt Tabelle:** Ein **quotierter Alias** wird nie aufgelöst, egal in welcher Schreibweise (`"a"`, `"A"`) – genau so schreibt EF Core jede Abfrage (`FROM "AUFTRAG" "a"` → Tabelle `a`). Ebenso Aliase von Inline-Views (`x` → `X`, EF: `"t"` für eine Unterabfrage mit `FETCH`) und nach `CROSS APPLY` sogar unquotierte Aliase (`k` → `K`).
- **Falsch:** Mengenoperatoren (`UNION`, `MINUS` …) melden die Tabelle des **ersten** Zweigs, obwohl die Zeilen aus allen kommen.

## Folgerung für den Bau (Vorschlag)
Die Angabe des Treibers ist ein **Hinweis**, den der Core gegen das Schema prüft, keine Tatsache:
1. **Herkunft lesen:** `ColumnOf` bekommt die drei Felder als optionale `SqlColumnOrigin` an `SqlColumn` (roh, wie gemeldet).
2. **Auflösen im Core** (reine Klasse, Unit-Tests ohne DB), auf dem Token-Strom von `SqlTokenizer`:
   - Mengenoperator auf oberster Ebene → keine Spalte hat eine Herkunft.
   - Alias-Tabelle aus allen `FROM`/`JOIN`/`,`/`APPLY`-Stellen: `[owner.]name alias`. Nennt der Treiber einen Namen, der dort Alias ist, gilt die Tabelle dahinter; ist der Alias der einer Unterabfrage oder auf verschiedenen Ebenen verschieden belegt → keine Herkunft. Damit gehen die EF-Abfragen mit `"a"`, `"k"`.
   - Den Namen auflösen wie Oracle: eigenes Schema, dann private, dann öffentliche Synonyme (`SchemaCache` kennt die Ziele schon, `TableSummary.Synonym`). Tabelle muss bekannt sein und die Spalte haben.
   - Aliase von Inline-Views (`"t"` bei `Take` + `Include`) bleiben vorerst ohne Herkunft; durch die Unterabfrage hindurch auflösen erst, wenn es im Alltag fehlt (Backlog).
3. Danach wie geplant: je Basistabelle eine Projektion (`TableDetails` + `RowData`) und `FkNavigation.JumpsFor` unverändert; C#-/Enum-Namen über die Präsentationsschicht.

Damit bleibt das Paket in der geschätzten Größe; der Alias-Teil ist neu, aber klein (Tokenizer vorhanden, eine Ebene Grammatik).

## Offen
- Vorschlag oben bestätigen (vor allem: eigene Alias-Auflösung statt nur Treiberangabe; Inline-Views später).
