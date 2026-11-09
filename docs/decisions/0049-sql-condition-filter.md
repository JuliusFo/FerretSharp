# ADR 0049: SQL-Bedingung als eigener Filtermodus neben dem Filtermodell (`RowFilter`)

- Status: akzeptiert
- Datum: 2026-11-09
- Paket: WP-79 (`docs/work-packages/wp-79-sql-where-filter.md`)

## Kontext

Kollegen, die aus SQL Developer bzw. HORA kommen, filtern fast immer über das WHERE-Feld der Tabellenansicht, typischerweise mit `IN` auf eine andere Tabelle und darin einem weiteren `IN`. Die Filterleiste kann das nicht, und das Filtermodell soll es auch nicht können: Die Invariante „Filtermodell 1:1 als LINQ-`Where`“ trägt Links, Vorlagen und den LINQ-Export. Der SQL-Editor kann es, ist aber kein Tabellen-Tab (kein Bearbeiten über den Row-Key, keine Anzeigespalten, keine Präsentation).

## Entscheidung (des Nutzers, 2026-11-09)

- **Eigener Modus, keine neue Filterart.** Ein Tabellen-Tab filtert entweder über die Filterleiste oder über eine selbst geschriebene Bedingung (ohne `WHERE`), nie über beides (kein UND aus beiden – dann gälten Link und LINQ nur halb). Die Bedingung ist kein `FilterCondition`.
- **`RowFilter` im Leseweg:** `IDataAccess.ReadPageAsync`, `CountAsync`, `ReadAllAsync`, `ReadColumnAllAsync` und die zugehörigen `QueryBuilder.Build*` nehmen ein `RowFilter` – Filterzeilen (`Conditions`) oder eine Bedingung (`Sql`). So liest alles, was ein Tab liest (Grid, Fußzeile, Export, Spaltenwerte, Plan, „SQL“), über einen Weg. Collection-Ausdrücke und Listen werden zu einem `RowFilter` (`[CollectionBuilder]`, implizite Umwandlung), sodass die meisten Aufrufer unverändert bleiben. Wege, die nur Filterzeilen kennen (Daten-Diff, FK-Sprung, Link, Vorlage, LINQ), behalten `IReadOnlyList<FilterCondition>`.
- **Eingesetzt als `WHERE (\n…\n)`:** Ein `--` am Ende kommentiert weder `ORDER BY` noch das Paging aus.
- **Prüfung vor dem Einsetzen dient der Korrektheit, nicht der Sicherheit** (`WhereCondition.Problem`, Tokenizer des SQL-Editors): kein offener Text, Name oder Kommentar, Klammern ausgeglichen und nie unter null, auf oberster Ebene keine reservierten Wörter, die ein WHERE beenden (`ORDER`, `GROUP`, `HAVING`, `CONNECT`, `START`, `UNION`, `INTERSECT`, `MINUS`, `FOR`), kein `;`, kein `FETCH FIRST/NEXT`, kein `OFFSET n ROW(S)`, keine Bind-Variablen. Die Lesesperre bleibt unverändert und prüft das ganze Statement wie jede Abfrage – sie ist die Grenze, nicht diese Prüfung.
- **Kein Rückweg SQL → Filterzeilen** (das wäre ein Parser). Der Weg Filterleiste → SQL schreibt den Filter mit Literalen (`QueryBuilder.ConditionText`, derselbe Literal-Weg wie für Views in WP-69). Zurück geht es ohne Rückfrage nur, solange der Text noch der ist, mit dem der Modus begann; die Filterzeilen bleiben im SQL-Modus erhalten.
- **Tabs mit mitgebrachtem Filter** (FK-Sprung, Link, Vorlage) öffnen in der Filterleiste; die Einstellung „Filter beginnt mit“ gilt nur für Tabs ohne Filter.
- **Ein Zeitreise-Tab hat keinen SQL-Modus.** Seine eigene Tabelle liest `AS OF SCN`, Unterabfragen in der Bedingung läsen den Stand von jetzt; die Bedingung umzuschreiben wäre ein Parser. Aus demselben Grund nehmen Zeitreise und Daten-Diff einen Tab mit angewendeter Bedingung nicht an.

## Folgen

- Im SQL-Modus gibt es keinen Link auf die Ansicht, keine Vorlagen, keinen LINQ-Export; „Link kopieren“, „Zeitreise …“ und „Diesen Tab vergleichen“ sind dann gesperrt mit Grund.
- Die zuletzt angewendeten Bedingungen je Tabelle liegen in einer eigenen Datei je Verbindung (`conditions\{id}.json`, `ConditionHistoryStore`, dieselbe Dateibehandlung wie der SQL-Verlauf über `HistoryFileStore`). Sie behalten ihre Literale – wie der SQL-Verlauf, auch auf Prod (Invariante: der Verlauf ohne Bind-Werte, Literale bleiben). Ins Log kommt die Bedingung nicht; ein Oracle-Fehler geht wie bei jeder Grid-Abfrage über `QueryErrorLog`, das auf Prod die Literale entfernt.
- Der Tab-Titel zeigt die angewendete Bedingung gekürzt auf einer Zeile.
- Eine Bedingung, die Oracle ablehnt (ORA-00904 …), steht wie jeder Lesefehler unter dem Feld; eine, die die Prüfung ablehnt, wird gar nicht erst ausgeführt.
