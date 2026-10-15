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

Vom Nutzer am 2026-10-15 so bestätigt: eigene Alias-Auflösung im Core, Inline-Views vorerst ohne Herkunft.

## Gebaut
- **Core/Data:** `SqlColumn.Origin` (`SqlColumnOrigin`: Owner, Tabelle oder Alias, Spalte – roh wie vom Treiber). `OracleDataAccess.OriginOf` liest sie in `ReadSqlAsync` mit, ohne `KeyInfo`.
- **Core/Query `SqlResultOrigins.Resolve`:** auf `SqlAnalysis.Tables` (FROM/JOIN/Komma mit Alias und Tiefe, jetzt mit `NotAliases` auch hier genutzt) und `SqlCompletion.Resolve` (eigenes Schema, dann Synonyme). Keine Herkunft für: `WITH` am Anfang, Mengenoperator auf oberster Ebene, Aliase von Unterabfragen (`) x`, `) "t"`, auch `CROSS APPLY`), eine Tabelle, die das Statement nicht nennt oder das Schema nicht kennt, einen Namen, der hier Alias und dort Tabelle ist, einen Alias für verschiedene Tabellen, und eine Tabelle, die zweimal im eigenen FROM steht, wenn der Treiber nur den Tabellennamen meldet (Self-Join mit unquotierten Aliasen). Eine Tabelle in einer Unterabfrage stört die äußere nicht.
- **Core/Query `SqlResultSources`:** je Zeilenquelle (Alias bzw. Tabelle) die Tabellenstruktur aus dem `SchemaCache` und welche Ergebnisspalte welche Tabellenspalte hält (Spalten, die die Tabelle nicht hat, z. B. ROWID, fallen weg; jede Tabellenspalte einmal). `Projection` + `Project` machen Ergebniszeilen zu Tabellenzeilen, `FkNavigation.JumpsFor` läuft unverändert. Ausgehend nur, wenn alle FK-Spalten im Ergebnis stehen, eingehend nur mit allen Schlüsselspalten; derselbe Sprung über zwei Quellen einmal. `SqlResultJump.Involves` für „über die geklickte Spalte“.
- **UI State `SqlResultPresentation`:** Kopf mit C#-Name und -Typ (nach der Einstellung vorn oder daneben, unter dem Namen aus der Abfrage), FK-Badge (deklariert bzw. C#-Modell), Tooltip „Aus KUNDEN.NAME“ plus Spaltenkommentar; Enum-Namen in den Zellen (`__u`, `__t` wie `GridRows`). **Kein PK-Badge:** grid.js pinnt Schlüsselspalten nach links, die Reihenfolge der Abfrage soll bleiben. `GridOptions.ClrHeaderHeight` statt der Konstante in `FerretGrid`.
- **`SqlResultGrid`:** wartet höchstens 300 ms auf die Tabellenstrukturen (aus dem Cache sofort), sonst baut es das Grid schlicht und beschriftet nachher in place um (`updateColumns`, `updateRows` der geladenen Zeilen). Fehler beim Nachladen nur im Log (Debug), das Ergebnis erscheint immer.
- **`ResultMenu`:** „Verweist auf“/„Referenziert von“ mit `FkJumpItem`, Zählungen über `FkCounts` (sofort oder beim Darüberfahren wie im Grid), von mehreren markierten Zeilen, Sprünge über die geklickte Spalte zuerst. Ohne Sprünge kein Abschnitt (kein „keine Beziehungen“ – die meisten Ergebnisse haben keine Herkunft).
- **LINQ-Konsole:** kommt über das gemeinsame `SqlResultGrid` mit; die Herkunft stammt aus dem SQL, das EF erzeugt.
- **Tests:** Integration `SqlResultOriginDriverTests` (Verhalten des Treibers, 25 Fälle) und `FkNavigationTests.Rows_of_a_free_query_jump_like_table_rows` (EF-Join über zusammengesetzten und RAW-Schlüssel, echter Leseweg); Core `SqlResultOriginsTests` (Auflösung und Sprünge); UI `SqlResultPresentationTests`.
- Texte: `SqlEditorText.Result_ColumnOrigin` (EN/DE).

## E2E (2026-10-15)
Eigene Instanz (`--data-dir`, eigener WebView2-Ordner, deutsch, dunkel), eigener Beispiel-Container auf Port 1531 mit verknüpftem `samples`-Projekt, CDP.
- SQL-Editor, EF-artiger Join `"a"`/`"k"` mit Spaltenalias `"ART"` und Ausdruck: Kopf mit C#-Name und -Typ, FK-Badge an `a.KUNDE_ID` und an `BEARBEITER_ID` (nur C#-Modell), Tooltip „Aus AUFTRAG.KUNDE_ID“ samt Spaltenkommentar, Enum-Namen in den Zellen (`Versendet (VERSANDT)`, `Behörde (3)`); der Ausdruck bleibt schlicht.
- Rechtsklick (CDP-Eingabe) auf `a.KUNDE_ID`: „Verweist auf“ KUNDEN (über die Spalte, hervorgehoben) und MITARBEITER (aus C#-Modell), „Referenziert von“ fünf Einträge mit Zählungen. Klick auf KUNDEN öffnet den Tab mit `KUNDE_ID = 8`, eine Zeile.
- LINQ-Konsole `db.Auftraege.Where(…).Include(a => a.Bearbeiter)`: dieselben Kennzeichen und Namen über das SQL von EF.
- Log ohne `[ERR]`/`[WRN]`.
- Nicht geprüft: Rechtsklick mit echter Windows-Eingabe (der Weg des Rechtsklicks ist unverändert, nur der Inhalt des Menüs ist neu); das nachträgliche Umbeschriften, wenn die Tabellenstrukturen erst nach 300 ms kommen (nur per Code und `updateColumns`/`updateRows` wie im Tabellen-Grid).

## Nach dem Code-Review (2026-10-15)
- Tabellenstrukturen über `Shell.RunDbAsync` (Verbindungsverlust → Banner, Fehler geloggt); das Grid entsteht immer, ein Fehler, der ein Bug ist, erreicht erst danach die ErrorBoundary.
- `SqlCompletion.Resolve`: unqualifiziert eigenes Objekt, dann privates, dann öffentliches Synonym (vorher das erste nach Anzeigename und Ziel-Owner) – betrifft auch die Vervollständigung.
- Aliase von Inline-Views (nach FROM, JOIN, Komma, APPLY, LATERAL) erkennt `SqlAnalysis` (`SqlStatementInfo.InlineViews`) statt einer zweiten Token-Schleife; `SqlAnalysis.Normalize` einmal statt dreimal.
- Tabellen werden gleichzeitig angefragt; `SqlResultSources.Of` entfernt.
- Derselbe Sprung über zwei Quellen behält beide (`SqlResultJump.Sources`): der Klick auf die zweite Kopie hebt ihn ebenfalls hervor.
- FK-Badge „aus C#-Modell“ nur für Beziehungen des C#-Modells, wie im Tabellen-Grid.
- Gemeinsamer Abschnitt `FkJumpSection` (Überschriften, Zählungen sofort/beim Darüberfahren, Sprung) für das Menü des Tabellen-Grids und des Ergebnisses.
- Geprüft und belassen: das nachträgliche Umbeschriften kann keinen Block schlicht stehen lassen – `GetRows` liest die Präsentation erst nach dem Lesen, direkt vor dem Formatieren, und die Antwort erreicht grid.js vor dem späteren `updateRows`.
- E2E erneut: Menü des Tabellen-Grids (AUFTRAG, Sprung nach MITARBEITER aus dem C#-Modell, Zählungen) und des SQL-Ergebnisses (Klick auf `k.KUNDE_ID`: eingehende Sprünge über KUNDEN vorn); Log ohne `[ERR]`/`[WRN]`.

## Offen
- Backlog: Inline-Views und `WITH` auflösen, Anzeigespalten im SQL-Ergebnis, `DefaultSchema` (unqualifizierte Namen löst FerretSharp im angezeigten Schema auf, Oracle im Schema des Users).
