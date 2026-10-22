# WP-42 Beziehungsgraph und Teilgraph kopieren → Release offen

Branch `wp/42-subgraph-copy`, begonnen am 2026-10-22. ADR 0032. Fundament für WP-46 (Beziehungsdiagramm), WP-60 (Datensatz-Diagramm) und WP-43 (Datenqualität der Beziehungen).

## Planung (aus `docs/roadmap.md`)
- **Beziehungsgraph** im Core: Kanten aus deklarierten FKs und Navigations des C#-Modells, später auch `Manual`/`Convention` (`FkSource`), je Kante Spaltenpaare und Quelle.
- **Kopieren:** markierte Zeilen eines Tabs als Start; Eltern werden immer mitgenommen (sonst FK-Verletzung), Kinder je Kante wählbar mit begrenzter Tiefe. Vorschau mit Zeilenzahl je Tabelle. Lesen auf der Quelle nur über den Leseweg, in Blöcken mit `in` (Grenze 1000 Werte je Liste).
- **Traversierung als eigener Baustein im Core**, getrennt vom Einfügen ins Ziel, schrittweise erweiterbar (WP-60).
- **Ziel:** ein Workspace einer anderen offenen Verbindung; Einfügen in dessen Sandbox-Transaktion in FK-Reihenfolge, als **eine** Aktion im Undo-Stapel (ADR 0018); Commit nur auf Befehl. Ziel Prod nur nach Freischalten; Quelle Prod erlaubt.
- **Konflikte:** gleicher Schlüssel im Ziel → überspringen, aktualisieren oder abbrechen. Abweichende Spalten warnen; Identity ALWAYS lässt sich nicht einfügen; Trigger im Ziel laufen mit – Hinweis.

## Entscheidungen des Nutzers (2026-10-22)
1. **Kanten:** alles aus `SchemaCache.ForeignKeys`. Heute sind das deklarierte FKs und das C#-Modell; `Manual` und `Convention` erzeugt noch kein Code (nur Tests), sie kommen dann ohne Änderung dazu. In der Vorschau abwählbar.
2. **Kinder:** standardmäßig eine Ebene entlang jeder Kind-Beziehung, je Pfad bis drei Ebenen.
3. **Konflikte:** standardmäßig überspringen, je Tabelle umstellbar.
4. **Maskieren** und **neue Schlüssel im Ziel**: später (Backlog, „Teilgraph kopieren“).
5. **Zyklen** zwischen Tabellen melden statt auflösen.
6. **Plan bestätigt** mit Array Binding für die INSERTs, Grenze 10 000 Zeilen und nur einer **anderen** Verbindung als Ziel.

## Gebaut
- **Core/Relations** (neuer Namespace `FerretSharp.Core.Relations`):
  - `RelationGraph`: Eltern/Kinder je Tabelle, `KeyOf` (Tabelle, Name, Quelle; überlebt das Neuladen des Schemas), `Order` (Kahn, alphabetisch bei Gleichstand, Kreise per Tarjan als `RelationCycle`, Selbstbezüge zählen nicht).
  - `RowTraversal` → `RowGraph`: `StartAsync` (Startzeilen nach PK oder ROWID neu gelesen), `FollowAsync` (eine Beziehung in eine Richtung, verknüpft auch schon bekannte Zeilen, kennt Eltern per PK ohne Abfrage), `CollectAsync` (Pfade der Kinder, dann Eltern bis zum Ende der Kette, außer `SkippedParents`). Schlüsselvergleich über `KeyText`: `5` und `5.00` sind gleich, CHAR mit und ohne Auffüllung ebenso. Probleme (Tabelle unbekannt, Spalte fehlt, Typ nicht vergleichbar) landen in `RowGraph.Problems`. `TooManyRowsException` ab 10 000 Zeilen.
  - `SubgraphCopyPlanner` → `SubgraphCopyPlan`/`TableCopy`: Owner-Abbildung, Spalten nach Namen, Identity ALWAYS, Konfliktschlüssel (PK, sonst UK), vorhandene Zeilen per Lesen im Ziel, Zeilenordnung bei Selbstbezug, Trigger-Hinweise, Fehler/Warnungen/Hinweise als `CopyIssue`. `IssuesWith(modes)`, `CanCopy(modes)`, `ToWrite(modes, source, wait)` (Updates, dann INSERT-Blöcke à 500 je Tabelle).
- **Core sonst:**
  - `QueryBuilder.BuildRowsByKeys` (Tupel-IN, ROWID, ganze LOBs auf Wunsch; Größen wie `LookupSizes`) und `IDataAccess.ReadByKeysAsync` (in Blöcken à 500).
  - `DmlBuilder.InsertBatch` → `BatchSpec`/`BatchParameter` (`Expand` für die Anzeige), `OracleSession.ExecuteNonQueryAsync(BatchSpec)` (intern, Array Binding), `OracleParameters.FromArray`.
  - `IDataEditor.CopyAsync(CopyWrite)` mit `CopyInsert`/`CopyUpdate`, `WriteActionKind.Copy`, `WriteAction.Copy` (für Redo), `CopyFailedException` (eine `RefusedException`), Redo für Copy in `OracleDataEditor`. `WorkspaceManager.CopyAsync` lehnt nicht beschreibbare Workspaces ab.
  - `ColumnInfo.IdentityAlways` (Unterabfrage auf `ALL_TAB_IDENTITY_COLS` nur für Identity-Spalten, gleiche Positionen der Select-Liste), `ISchemaReader.GetTableTriggersAsync`/`SchemaCache.GetTableTriggersAsync`.
  - `OracleErrors`: ORA-24381 (Array-DML) wird zum ersten Fehler einer Zeile.
- **UI:**
  - Zellmenü „In andere Verbindung kopieren …“ (aus, wenn keine andere Verbindung offen ist oder der Tab eine View zeigt).
  - `SubgraphCopyDialog` mit `SubgraphCopyDraft` (State/Copy): Ziel (Workspaces anderer verbundener Verbindungen, `CopyTargets`; gesperrte erscheinen mit Hinweis), Baum der Kind-Beziehungen (aufklappbar bis drei Ebenen, Häkchen an zieht die Pfade davor mit, aus die Pfade dahinter), „Zeilen lesen“ mit Fortschritt und Abbrechen, Vorschau je Tabelle (Ziel, Zeilen, neu, vorhanden, Konfliktmodus), Baum der Eltern-Beziehungen (vorab abwählbar), Zeilenzahl je Strang nach dem Lesen, Prüfung (Fehler, Warnungen, Hinweise). Jede Änderung der Auswahl verlangt neues Lesen.
  - `WorkspaceEditing.CopyAsync` (Ziel-Tabs laden neu); in der Änderungsübersicht Quelle „Kopie“, Redo mit eigenem Tooltip und der Bestätigung wie bei Statements.
  - `ChangeOverview.Sql` zeigt höchstens 100 Statements einer Aktion und zählt den Rest.
- **Texte:** Core `RelationText` (neu), `DataText.CopyFailed`; UI `CopyViewText` (neu), `GridText.GridMenu_CopyToConnection*`, `ShellText.Changes_SourceCopy`/`Changes_RedoCopyTitle`/`Changes_MoreStatements`.

7. **Gleiche Daten, andere ID / gleiche ID, andere Daten** (Nachfrage des Nutzers, 2026-10-22, Variante A): Die Kopie erkennt und warnt, sie ordnet nicht um. Vorhandene Zeilen (über den Konfliktschlüssel) werden mit dem Ziel verglichen – andere Werte in den kopierten Spalten (ohne Schlüssel und LOBs) sind eine Warnung, weil Kinder beim Überspringen auf einen womöglich anderen Datensatz zeigen (`TableCopy.Differing`, Spalte „Vorhanden“ mit „(n abweichend)“). Neue Zeilen werden gegen **alle** übrigen Schlüssel des Ziels geprüft (PK und UKs, deren Spalten Werte bekommen): ein Treffer ist ein Fehler in der Vorschau statt ORA-00001 beim Schreiben. Zuordnen über einen fachlichen Schlüssel mit Umschreiben der IDs (Variante B) bleibt im Backlog.

8. **Stränge vorab wählen** (Nachfrage des Nutzers, 2026-10-22): Auch die Eltern sind vor dem Lesen als Baum wählbar – Wurzeln sind die Starttabelle und die Tabellen der gewählten Kind-Pfade, darunter je Eltern-Beziehung ein Häkchen (an: aufklappbar zu den Eltern der Elterntabelle, nicht im Kreis). Nach dem Lesen steht an jedem Strang die Zahl der Zeilen (`RowGraph.ReachedAlong` je Kind-Pfad, `ParentsAlong` je Eltern-Beziehung). Abgewählte Eltern prüft der Planer im Ziel (`PlanAsync(graph, skippedParents)`): Zeilen, auf die kopierte Zeilen verweisen und die weder mitkommen noch im Ziel stehen, sind ein Fehler, wenn das Ziel den Constraint hat, sonst eine Warnung.

## Entscheidungen in der Umsetzung
- **Eltern-Häkchen gelten je Beziehung**, nicht je Stelle im Baum: Dieselbe Beziehung kann unter mehreren Wurzeln stehen und zeigt überall denselben Zustand – die Traversierung kennt nur eine Menge abgewählter Beziehungen.
- **Ein Kind-Pfad geht von allen erreichten Zeilen weiter**, auch von solchen, die schon ein anderer Pfad gebracht hat (vorher nur von neuen – die Enkel solcher Zeilen fehlten).
- **Pfade statt Kanten mit Tiefe:** Die Auswahl der Kinder ist eine Menge von Pfaden ab der Starttabelle. Dieselbe Beziehung kann auf verschiedenen Ebenen vorkommen; ein Pfad ist eindeutig.
- **Reihenfolge der Tabellen nur über Beziehungen, die Zeilen tatsächlich verbinden** (`RowGraph.Links`), nicht über alle Beziehungen der Tabellen. Sonst blockierte ein Kreis über eine C#-Modell-Beziehung ohne Constraint, die in den kopierten Daten gar nicht vorkommt.
- **„Kein Schlüssel“ nur, wenn das Ziel weder PK noch UK hat.** Fehlt dem Schlüssel nur der Wert (Identity ALWAYS), meldet das die Spalte selbst.
- **NULL wird kopiert**, nicht durch den Default des Ziels ersetzt: Das INSERT nennt alle abgebildeten Spalten.
- **Ziel-Workspace ohne Tabs in der Shell:** `WorkspaceTabs` wird für den Aufruf neu erzeugt (nur die Id zählt), Tabs gibt es dann keine neu zu laden.
- **Fehler beim Lesen:** Datenbankfehler gehen über `RunDbAsync` (Verbindungsverlust an die Quellverbindung), die Zeilengrenze bleibt als Meldung im Dialog.

## Tests
- Core: `RelationGraphTests`, `RowTraversalTests`, `SubgraphCopyPlannerTests` (mit `MemoryTables`: Tabellen im Speicher hinter `ReadByKeysAsync` und ein geladener `SchemaCache`), `QueryBuilderTests.Keys`, `DmlBuilderTests` (Batch).
- Integration (`SubgraphCopyTests`, `CopySchema`: CP_-Tabellen im App-Schema, dieselben in FERRET_COPY als SYSTEM angelegt, mit vorhandenem Kunden, Audit-Trigger und Identity-Tabellen): Kunde mit Aufträgen, Positionen, Lieferungen (Tupel-IN) und Vorgesetztenkette als eine Aktion, mit CLOB (8000 Zeichen), BLOB, NUMBER(12,2), CHAR, TIMESTAMP(6) und NVARCHAR2 im Array Binding; Undo und Redo; gescheiterte Kopie (FK) lässt nichts zurück und nennt Tabelle und Constraint; Update einer verschwundenen Zeile; gesperrter Workspace; Identity-Art und Trigger aus dem Dictionary; Lesen nach Tupel und ROWID.
- UI: `SubgraphCopyTests` (Pfade, Neu-Lesen nach Änderung, nicht beschreibbares Ziel, Ziele aus dem Hub, Kopie als eine Aktion, gesperrtes Prod), `ChangeOverviewTests` (Kürzung).

## Offen
- **E2E, erster Teil (2026-10-22, abgebrochen):** Zellmenü, Dialog, beide Bäume, Lesen mit Zählern, Vorschau (abweichend, fehlende Tabelle/Spalten, Trigger), „Auswahl geändert“ → „Neu lesen“, Kopie mit „Aktualisieren“ (6 Zeilen, „1 Aktion offen“ im Ziel) liefen. Befunde, noch zu beheben:
  1. Der Menüeintrag steht ohne eigene Überschrift unter „Alle Zeilen des Filters“ – eigene Gruppe.
  2. Der Eltern-Baum zeigt die Beziehung eines Kind-Pfads rückwärts noch einmal (AUFTRAG → KUNDEN über FK_AUFTRAG_KUNDE) und Tabellen, die selbst Wurzel sind – ausblenden.
  3. „Abweichend“ zählt Spalten wie `ERSTELLT_AM DEFAULT SYSDATE` mit, die zwischen Datenbanken immer abweichen – die Meldung soll die abweichenden Spalten nennen.
  4. „Zeilen lesen“ steht unter den Bäumen und muss gescrollt werden – in die Fußzeile neben „Kopieren“.
- **E2E, zweiter Teil (2026-10-23):** Im Ziel lädt der offene AUFTRAG-Tab nach der Kopie neu, die Statusleiste zeigt „1 Aktion nicht committet“, die Änderungsübersicht eine Aktion „Kopie aus E2E DEV: 6 Zeilen“ mit Quelle „Kopie“ und den 6 Statements (UPDATE KUNDEN, dann die INSERTs in FK-Reihenfolge). ↶ nimmt sie ganz zurück (Tab 0 Zeilen, „Zurückgenommen – wiederholbar“), Redo fragt und kopiert erneut (Tab 1 Zeile). Gesperrtes Prod erscheint als Ziel „(gesperrt – zum Kopieren freischalten)“, Lesen geht, Kopieren bleibt aus. Dialog im Dunkelmodus lesbar. Log ohne `[ERR]`/`[WRN]`. Weitere Befunde:
  5. Die Redo-Bestätigung heißt bei einer Kopie „Statement erneut ausführen“ – eigener Titel und Text für Kopien.
  6. Bei gesperrtem Ziel sagt der Dialog nicht, warum „Kopieren“ aus ist (nur die Ziel-Auswahl nennt es) – Hinweis am Knopf bzw. in der Prüfung.
- **Befunde 1–6 behoben (2026-10-23)** und in der App nachgeprüft: eigene Menügruppe „Andere Verbindung · n Zeilen“; der Eltern-Baum lässt die Beziehungen gewählter Kind-Pfade weg und klappt nicht in Tabellen auf, die selbst Wurzel sind; „andere Werte in ERSTELLT_AM, …“ nennt die abweichenden Spalten (höchstens fünf); „Zeilen lesen“, Fortschritt und Hinweise stehen in der Fußzeile; die Redo-Bestätigung einer Kopie heißt „Erneut kopieren“ und sagt, dass eine inzwischen vorhandene Zeile die Kopie scheitern lässt; bei gesperrtem Ziel nennen Fußzeile und Tooltip von „Kopieren“ den Grund.
- **Nicht geprüft:** Fortschritt und Abbrechen beim Lesen (lokal unter einer Sekunde, nichts zu sehen); alles per CDP, nicht mit echter Windows-Eingabe (neu sind nur einfache Klicks auf Häkchen, Knöpfe und Auswahllisten, das Zellmenü per Rechtsklick ist der bestehende Weg).
- Fragen an den Nutzer: Soll das Ziel auch ein anderer Workspace derselben Verbindung sein dürfen (heute ausgeschlossen)? Reicht die Prüfung „gesperrt → erst freischalten“ für Prod als Ziel, oder zusätzlich eine Bestätigung mit Verbindungsnamen (wie beim Redo)?
