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

## E2E
2026-11-09, eigener Container (`New-SampleDb.ps1 -Name ferret-e2e-wp79 -Port 1579`), Testinstanz mit `--data-dir` (deutsch), Verbindungen „E2E WP79“ (Test) und „E2E WP79 Prod“ (read-only) als `connections.json`, Passwort nicht gespeichert. Klicks mit echter Windows-Eingabe (Probe vorher bestanden), Tippen und Tasten per CDP.
- Geprüft: Filterzeile „NAME enthält a“ → „SQL-Bedingung“ übernimmt `UPPER(NAME) LIKE UPPER('%a%') ESCAPE '\'`, Feld mit Fokus; Shift+Enter neue Zeile (Feld wächst auf 48 px), Enter wendet an; verschachteltes `IN` über AUFTRAG mit Kommentar am Ende: 99 Zeilen wie direkt in der DB; Vervollständigung der Spalten der Tabelle und eines Alias in der Unterabfrage; Enter bei offener Vorschlagsliste übernimmt nur den Vorschlag; offene Klammer und `ORDER BY` am Feld, ORA-00904 darunter; „Zuletzt“ ohne die gescheiterte Bedingung, ein Eintrag wendet an; „Filterleiste“ mit geänderter Bedingung fragt, „Verwerfen“ bringt die Filterzeile zurück (neu gelesen), unverändert ohne Frage und ohne Abfrage; Einstellung → AUFTRAG aus dem Explorer im SQL-Modus, FK-Sprung zu KUNDEN in der Filterleiste („KUNDE_ID = 8“, als Text übernommen); mit angewendeter Bedingung „Link auf diese Ansicht“, „Diesen Tab vergleichen“, „Zeitreise …“, „Link“ gesperrt mit Grund; Tab-Titel mit der Bedingung; nach dem Neustart Tab im SQL-Modus mit Bedingung und Zeilen. Prod: ORA-00904 einer Bedingung mit Literal steht im Log als `NAME = '***'`.
- Gefunden und behoben: „Zuletzt“ hatte dieselbe Bedingung zweimal, einmal mit `\r\n` – Monaco schreibt die Zeilenenden der Plattform in einen gesetzten Text. Vergleich jetzt ohne Zeilenenden (`ConditionHistoryStore.Same`, auch `SqlConditionState.Unchanged`); Test.
- Gefunden und behoben: `SqlConditionState.Unchanged` stand als `"unchanged"` in der Workspace-Datei (`[JsonIgnore]`).
- Log: nur die zwei erwarteten Warnungen der ORA-00904, kein `[ERR]`.

## Nachtrag: Code-Review (2026-11-09)
Review per `/branch-code-review` (eigener Reviewer-Agent, Befunde nachgeprüft). Entscheidung des Nutzers: „Alle beheben“ – bei 3 nur die Doku, bei 4 erst nach dem Nachstellen (Empfehlungen übernommen).
1. M Enter/Shift+Enter des Bedingungsfelds wirkten in jedem Monaco-Editor (`addCommand` bindet im gemeinsamen Keybinding-Service): Enter im SQL-Editor wendete die Bedingung des zuletzt erzeugten Felds an. **Behoben:** Context-Key je Feld in der `when`-Bedingung; Regel in `ui-monaco.md`. E2E mit zwei Feldern und dem SQL-Editor.
2. M `;` und `FOR UPDATE` in einer Unterabfrage passierten die Prüfung, die Lesesperre warf, der Tab stürzte ab – nach jedem Neustart wieder. **Behoben:** beide auf jeder Ebene abgelehnt; Test, dass alles Durchgelassene die Lesesperre passiert.
3. K Doku versprach „keine Literale im Prod-Log“; maskiert werden Text-Literale (wie beim SQL-Editor). **Doku korrigiert** (ADR).
4. K Verdacht: Ein einzelnes `\r` könnte für Oracle einen `--`-Kommentar früher beenden als für den Tokenizer. **Nicht nachgestellt:** Oracle 23 liest bis zum Zeilenvorschub (Integrationstest hält das fest), keine Codeänderung.
5. K Ein `RowFilter` mit Bedingung verlor sie still, wenn man ihn als Filterzeilen aufzählte. **Behoben:** `GetEnumerator` wirft; Test.
6. K Nach „Leeren“ blieben Link, Zeitreise und Daten-Diff gesperrt. **Behoben:** `ConditionApplied` nur bei filternder Bedingung, diese Wege nehmen `ReadFilter.Conditions` statt der wartenden Filterzeilen; Test, E2E.
7. K `docs/architecture.md` nicht nachgezogen. **Behoben.**

Zweiter Durchgang (derselbe Reviewer, nur die Fix-Commits): 1–7 passen, nichts kaputt. Freigabe des Nutzers für die Nachzügler: „Ja, beheben und pushen“.
- 8 K Kein Test merkte, wenn Diff/Zeitreise/Link wieder die wartenden Filterzeilen nähmen. **Behoben:** `DataDiffTests.A_cleared_condition_compares_every_row_not_the_hidden_filter_rows`.
- 9 K Dieser Nachtrag fehlte. **Behoben.**
- 10 K Übersetzer-Kommentar zu `ConditionTopLevel` nannte nur „außerhalb von Klammern“. **Behoben.**

## Offen / Fragen an den Nutzer
- Der Wechsel per Tastenkürzel ist als Aktion ohne Standardtaste angelegt („Filterleiste ↔ SQL-Bedingung“, auch in der Befehlspalette) – reicht das?
