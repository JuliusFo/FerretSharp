# WP-79 SQL-Bedingung als Filter eines Tabellen-Tabs → Release offen

Branch `wp/79-sql-where-filter` (von `main`), 2026-11-09. ADR 0049.

## Planung (aus `docs/roadmap.md`)
Anlass (2026-11-09): Rückmeldung aus dem Kollegenkreis – viele filtern in SQL Developer bzw. HORA fast immer über das WHERE-Feld der Tabellenansicht und steigen ohne so ein Feld kaum um. Ihr typischer Fall: `IN` auf eine andere Tabelle, darin ein weiteres `IN`. Geplant mit dem Nutzer: dieses Paket zuerst, der Beziehungsfilter (WP-80) später für die, die klicken statt tippen.
- **Eigener Modus statt neuer Filterart:** Ein Tab filtert entweder über die Filterleiste oder über eine selbst geschriebene Bedingung (ohne `WHERE`). Die Bedingung ist kein `FilterCondition` – die Invariante „Filtermodell 1:1 als LINQ-`Where`“ bleibt. Folge im SQL-Modus: kein Link, keine Vorlage, kein LINQ-Export; der Tab-Titel zeigt die Bedingung gekürzt.
- **Was bleibt:** Der Tab bleibt ein Tabellen-Tab (ROWID/PK, Sortierung PK → ROWID, Paging) – Bearbeiten, FK-Sprung, Anzeigespalten, Präsentation, Export und Zählen wie bisher.
- **Eingabe:** einzeiliges Monaco-Feld an der Stelle der Filterleiste (wächst bei Zeilenumbrüchen), Vervollständigung wie im SQL-Editor, Enter wendet an, ein Oracle-Fehler steht am Feld. Zuletzt benutzte Bedingungen je Tabelle zur Auswahl.
- **Umschalter Filterleiste ↔ SQL:** Wechsel zu SQL startet mit dem WHERE des aktuellen Filters, Bind-Werte als Literale. Zurück verlustfrei nur bei unverändertem Text, sonst nach Rückfrage „Bedingung verwerfen?“. Kein Rückweg SQL → Filterzeilen.
- **Prüfung vor dem Einsetzen** (Korrektheit, nicht Sicherheit – die Lesesperre bleibt unverändert und prüft das ganze Statement), eingesetzt als `WHERE (\n…\n)`.
- **Prod:** Literale der Bedingung nicht ins Log; der Verlauf behält sie. Workspace-Datei speichert Modus und Text.
- **Anzeige (Entscheidung des Nutzers):** Beide Eingaben nie gleichzeitig aktiv – je Tab ein Modus, Standard die Filterleiste; eine Einstellung „Filter beginnt mit: Filterleiste / SQL-Bedingung“. Kein UND aus beiden.
- **Tabs mit mitgebrachtem Filter (Entscheidung des Nutzers):** Der Inhalt bestimmt den Modus. FK-Sprung, Link und Vorlage öffnen in der Filterleiste, auch wenn die Einstellung auf SQL-Bedingung steht; die Einstellung gilt nur für Tabs ohne Filter.

Entscheidungen zum Plan (2026-11-09):
1. **Zeitreise:** Im Zeitreise-Tab gibt es den SQL-Modus nicht (Unterabfragen läsen dort den Stand von jetzt).
2. **Daten-Diff:** Ein Tab mit SQL-Bedingung lässt sich nicht hinzufügen, der Eintrag ist gesperrt mit Grund – „Passt.“

## Gebaut
- **Core:**
  - `RowFilter` (`Query/RowFilter.cs`): Filterzeilen oder Bedingung, nie beides; `[CollectionBuilder]` und Umwandlung aus Liste/Array. Leseweg (`IDataAccess` mit `OracleDataAccess`, `ReadSessionData`) und `QueryBuilder.BuildSelect`/`BuildSelectAll`/`BuildSelectStar`/`BuildColumnValues`/`BuildCount` nehmen ihn; `Builder.Where` setzt die Bedingung als `WHERE (\n…\n)` ein.
  - `WhereCondition` (`Query/SqlEditor`): `Problem` (Prüfung, Texte in `QueryText.Condition*`), `Wrap`, `ForCompletion` (Bedingung als WHERE einer Abfrage auf die Tabelle mit Alias `t`, für `SqlCompletion`).
  - `QueryBuilder.ConditionText`: Filterzeilen als Bedingung mit Literalen und Spaltennamen ohne Alias (`Builder` mit `alias: null`).
  - `HistoryFileStore<T>` (Dateibehandlung aus `SqlHistoryStore` herausgezogen), `ConditionHistoryStore` (`conditions\{id}.json`, je Tabelle 10, insgesamt 1000; Wiederholung rückt nach vorn).
  - `TabState.SqlCondition` (`SqlConditionState`: Text, angewendet, Ausgangstext; `Unchanged`), im Zeitreise-Tab beim Laden verworfen; `AppSettings.FilterStart`; `ShortcutAction.ToggleCondition` (ohne Standardtaste).
- **UI:**
  - `TableTab`: `SqlCondition`, `StartCondition`/`EditCondition`/`ApplyCondition`/`EndCondition`/`UseCondition`, `ReadFilter` (was das Grid liest), `EditedFilter` (für „SQL“), `ConditionSummary`; `FilterTexts`/`ActiveFilterCount` zeigen die Bedingung.
  - `ConditionBar` (an der Stelle der `FilterBar`): `MonacoEditor` mit `Field` (monaco.js `makeField`: wächst bis acht Zeilen, Enter → `OnFieldEnter` mit dem Text, außer bei offener Vorschlagsliste, Shift+Enter neue Zeile), „Zuletzt“, SQL, Plan, Filterleiste, Leeren, Anwenden.
  - `FilterBar`: Knopf „SQL-Bedingung“ (nicht im Zeitreise-Tab). `TabView`: Wechsel, Anwenden (Text vorher aus dem Editor), Rückfrage „Bedingung verwerfen?“, Eintrag in „Zuletzt“ erst nach der ersten Seite ohne Fehler; `TabCommand.ToggleCondition`.
  - Leseweg-Aufrufer auf `ReadFilter`: `FerretGrid`, `TabFooter`, `AllRowsExports`, `ColumnValueCopies`, `ObjectMenuFacts.SqlOf` (LINQ ohne Bedingung), Plan, `FilterStatement`.
  - Gesperrt mit Grund (`GridText.Condition_NotWithCondition`): „Link auf diese Ansicht“ (Zellmenü, Tab-Menü), Zeitreise und Daten-Diff (`WorkspaceLifecycle.DataDiffUnavailable`). „In anderer Verbindung öffnen“ nimmt die Tabelle ohne Filter mit.
  - `ShellState.OpenTable` startet nach der Einstellung im SQL-Modus, `DuplicateTab` übernimmt die angewendete Bedingung. Einstellung „Filter beginnt mit“ unter *Einstellungen › Datenansicht*.

## Erkenntnisse
- Ein offener Text, Name oder Kommentar verschluckt beim Tokenizer den Rest: `WhereCondition.Problem` hängt einen Zeilenumbruch an und prüft, ob das letzte Token Leerraum ist.
- Nur reservierte Wörter beenden ein WHERE in der Prüfung – eine Spalte darf `OFFSET`, `FETCH` oder `MODEL` heißen; `FETCH`/`OFFSET` deshalb nur in ihrer Form als Klausel.

## Offen / Fragen an den Nutzer
- Der Wechsel per Tastenkürzel ist als Aktion ohne Standardtaste angelegt („Filterleiste ↔ SQL-Bedingung“, auch in der Befehlspalette) – reicht das?
