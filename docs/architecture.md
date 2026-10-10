# Architektur – Domänenmodell und UI-Konzept

Aus der `CLAUDE.md` ausgelagert (Stand 3.14.0), damit sie kurz bleibt. Vor Änderungen am jeweiligen Bereich den passenden Abschnitt lesen. Die Kurzfassung der Invarianten steht in der `CLAUDE.md`, die Fallen in `src/FerretSharp.Core/CLAUDE.md` und `src/FerretSharp.UI/CLAUDE.md`.

## 1. Domänenmodell

### 1.1 ConnectionProfile
```csharp
record ConnectionProfile(
    Guid Id,
    string Name,
    ConnectionKind Kind,          // Prod | Test | Dev | Other → bestimmt Farbe
    OracleAddress Address,        // HostPort(host, port, serviceName?, sid?) | TnsAlias(alias, tnsAdminPath?)
    string User,
    string? DefaultSchema,        // falls technischer User, aber anderes Schema angeschaut wird
    bool ReadOnly,                // bei Prod default true; wirkt ab v2 (in v1 ist alles read-only)
    string? Group = null);        // optional, z. B. Projekt/Kunde ("ERP", "Kasse") – Gruppierung in Umschalter und Übersicht
```
- Suche und Gruppierung: `ConnectionSearch` (alle Suchbegriffe müssen in Name, Gruppe, Adresse, Benutzer oder Art vorkommen; Gruppen alphabetisch, „Ohne Gruppe“ zuletzt; innerhalb einer Gruppe Dev → Test → Prod → Sonstige).
- Passwort separat über `ISecretStore.Get(profileId)` / `Set(...)`.
- `TnsAlias` braucht den Ort der `tnsnames.ora`: Der Managed Driver liest **nicht** die Registry. Reihenfolge: `tnsAdminPath` im Profil → Umgebungsvariable `TNS_ADMIN` → Fehlermeldung mit Hinweis.
- Connection-Datei: `%APPDATA%\FerretSharp\connections.json`, optional zusätzlich pro Repo (`.ferretsharp/connections.json`, ohne Secrets).

### 1.2 Workspace
```csharp
record Workspace(Guid Id, Guid ConnectionId, string Name) {   // z. B. "Bug 3711", max. 40 Zeichen
    string Notes;                                    // Freitext (noch ohne UI)
    IReadOnlyList<TabState> Tabs; int ActiveTabIndex;
    bool IsOpen; int Order; DateTimeOffset LastActive;
}
record TabState(TableRef Table, TabMode Mode, FilterRows, AppliedFilters, Sorts, int? FirstVisibleRow) { PinnedColumns, OriginTab, Form }
```
- `PinnedColumns`: vom Nutzer angeheftete Spalten in Anheft-Reihenfolge, **pro Tab** (Entscheidung des Nutzers; ein FK-Sprung öffnet den neuen Tab ohne Pins). Der PK ist immer angeheftet und steht nicht in der Liste. Logik in `ColumnPinning` (Workspaces/): PK in Schema-Reihenfolge → angeheftete Spalten → Rest; gelöschte Spalten fallen beim Öffnen weg.
- `Form` (WP-21): Formular neben dem Grid offen, Breite, „Leere ausblenden“ – je Tab (Entscheidung des Nutzers); null, solange das Formular im Tab nie offen war.
- `OriginTab` (v1.6): Index des Tabs, aus dem ein FK-Sprung kam, für „Zurück“ (Alt+←). Zur Laufzeit hält `TableTab.Origin` die Referenz; ist der Ursprung geschlossen, führt „Zurück“ zum nächsten offenen Tab weiter hinten in der Sprungkette (`BackTarget`). „Vor“ (Alt+→, `TableTab.Forward`) wird nicht gespeichert; ein neuer Sprung aus einem Tab löscht dessen „Vor“ (wie im Browser). Mehrere Tabs derselben Tabelle zeigen im Titel die Kurzform ihrer Filter (`FilterSummary`).
- `SavedQuery` entfällt: Seit 3.2 werden SQL-Tabs (Text und Variablen) mit dem Workspace gespeichert.
- PL/SQL-Tabs (WP-28) stehen als `TabState.PlSql` (`PlSqlTabState`: Owner, Name, Art, Ansicht) in der Datei, `Table` ist wie bei SQL/LINQ leer; nicht mehr vorhandene Objekte werden beim Wiederherstellen still verworfen.
- Zur Laufzeit hält jeder offene Workspace eine **eigene** Session (eigene Connection, ab v2 eigene Transaktion), geöffnet beim ersten Datenzugriff (`WorkspaceManager.GetDataAsync`). Das Schema lädt eine separate Explorer-Session (ADR 0005).
- Connection-String mit `Pooling=false`: Die Sessions leben lange, und eine Connection mit offener Transaktion darf nie in einen Pool zurückgehen.
- Beim Öffnen `ModuleName = "FerretSharp"`, `ActionName = <Workspace-Name>`, `ClientInfo` setzen → in `V$SESSION` ist erkennbar, welcher Workspace eine Sperre hält. Umbenennen setzt ACTION neu. Werte werden nach ASCII transliteriert (`src/FerretSharp.Core/CLAUDE.md`).
- Persistenz unter `%APPDATA%\FerretSharp\workspaces\{id}.json` (`WorkspaceStore`, eine Datei pro Workspace, versioniert, unlesbare Dateien werden gemeldet und nicht angefasst). Tab-Änderungen speichert der `WorkspaceManager` gesammelt nach 1 s, Strukturänderungen und Trennen/Beenden sofort.
- Beim Verbinden: offene Workspaces laden, der zuletzt aktive wird aktiv; ist keiner offen, wird der zuletzt benutzte wieder geöffnet, sonst „Workspace 1“ angelegt. Der letzte offene Workspace lässt sich nicht schließen. Gelöscht werden nur geschlossene Workspaces; das Löschen einer Verbindung löscht ihre Workspaces mit.
- Tabellen, die beim Wiederherstellen nicht mehr im Schema sind (gelöscht, Synonym weg), werden still verworfen.

### 1.3 Schema
```csharp
enum TableKind { Table, View, MaterializedView }
record TableSummary(string Owner, string Name, TableKind Kind);                       // beim Connect geladen
record TableDetails(TableSummary Table, IReadOnlyList<ColumnInfo> Columns,
                    IReadOnlyList<string> PrimaryKey, IReadOnlyList<IReadOnlyList<string>> UniqueKeys,
                    bool IsIndexOrganized);                                           // lazy pro Tabelle
record ColumnInfo(string Name, string DataType, int? Length, bool CharSemantics, int? Precision, int? Scale,
                  bool Nullable, bool IsIdentity, string? Default, int Position);    // DisplayType: "VARCHAR2(50 CHAR)", "NUMBER(12,2)", "INTEGER" …
record TableRef(string Owner, string Name);                                           // exakter Dictionary-Name
record ForeignKeyInfo(string Name, TableRef From, IReadOnlyList<string> FromColumns, TableRef To, IReadOnlyList<string> ToColumns, FkSource Source);
enum FkSource { Declared, Manual, Convention, ClrModel }   // ClrModel: Navigations des C#-Modells ohne Constraint (WP-12)
```
- `ForeignKeyInfo` trägt Owner (`TableRef`), damit FKs über Schemagrenzen nicht verloren gehen.
- **Synonyme** (WP-04b): `ISchemaReader.GetSynonymTargetsAsync` liefert Tabellen/Views/MViews anderer Schemas, die über private Synonyme des Schemas oder öffentliche Synonyme erreichbar sind – nur Ziele mit Zugriff (`ALL_OBJECTS`), keine Oracle-Schemas (`ALL_USERS.ORACLE_MAINTAINED`), keine DB-Links. `TableSummary` beschreibt immer das **echte Objekt**, `Synonym` und `DisplayName` den Namen, unter dem der Nutzer es kennt. `SchemaCache.Merge`: eigene Objekte vor Synonymen, privat vor öffentlich, ein Eintrag pro echtem Objekt. FKs werden für alle beteiligten Owner geladen. Synonymketten (Synonym auf Synonym) werden nicht aufgelöst (Backlog). Ist im Profil ein Schema eingetragen, zählen die privaten Synonyme dieses Schemas, nicht die des Login-Users – so löst auch Oracle mit `CURRENT_SCHEMA` auf (entschieden, bleibt so). Gemessen: 27.889 öffentliche Synonyme (davon 20.000 eigene, die Hälfte ohne Zugriff) → Verbinden 2,5 s kalt, 0,5–0,8 s warm (ohne: 0,1 s); kein Handlungsbedarf.
- View-/MView-Definition: `TableDetails.Definition` aus `ALL_VIEWS.TEXT` / `ALL_MVIEWS.QUERY` (LONG, max. 32.767 Zeichen, `DefinitionTruncated`).
- Objekt-Details (v1.7, `Schema/ObjectDetails.cs`), je eine `ISchemaReader`-Methode, nicht im `SchemaCache` gecacht (die Ansicht im Tab hält ihre Daten bis F5): `GetObjectInfoAsync` (`ALL_OBJECTS` + `ALL_TABLES` + `ALL_TAB_COMMENTS`/`ALL_MVIEW_COMMENTS`), `GetConstraintsAsync` (inkl. Check-Bedingung als LONG; die systemgenerierten NOT-NULL-Checks erkennt `ConstraintInfo.IsColumnNotNull` – auch `DEFAULT ON NULL` erzeugt einen), `GetIndexesAsync` (ohne LOB-Indizes; DESC-Spalten stehen in `ALL_IND_EXPRESSIONS` als `"SPALTE"` und werden zu normalen Spalten), `GetDependenciesAsync` (`ALL_DEPENDENCIES` beidseitig, ohne `SYS.STANDARD`/`DBMS_STANDARD`), `GetDdlAsync` (`SELECT DBMS_METADATA.GET_DDL(…) FROM DUAL` – passiert die Lesesperre; fremde Schemas brauchen `SELECT_CATALOG_ROLE`, sonst ORA-31603). `IndexAdvice.UnindexedForeignKeys` findet FKs ohne Index mit ihren Spalten vorne. Spalten kommen jetzt aus `ALL_TAB_COLS` mit `HIDDEN_COLUMN = 'NO'` (= `ALL_TAB_COLUMNS` plus `VIRTUAL_COLUMN`), dazu Kommentar und `DEFAULT_ON_NULL`. `TableSummary.IsInvalid` für Views/MViews mit Status INVALID (eine ungültige MView lässt sich weiter abfragen, eine View wird beim Zugriff neu kompiliert – Texte in `InvalidStatusText`).
- **PL/SQL** (WP-28, nur ansehen, `Schema/PlSqlObjects.cs`): Packages, Prozeduren, Funktionen und Trigger als `PlSqlObjectSummary` (ein Eintrag je Package mit Status von Spezifikation und Body; `PlSqlRef` mit Art, weil Trigger einen eigenen Namensraum haben). Beim Verbinden mit der Tabellenliste geladen (`SchemaCache.PlSqlObjects`, `ALL_OBJECTS` ohne `BIN$…`, deaktivierte Trigger aus `ALL_TRIGGERS`); Synonym-Ziele kommen aus derselben Abfrage wie die der Tabellen (`SynonymTargets`). Je Tab, nicht gecacht: Quelltext aus `ALL_SOURCE` (eine Zeile je `LINE`, damit Zeilen aus `ALL_ERRORS` passen; `FetchManyRows`), Unterprogramme aus `ALL_PROCEDURES` + Parameter aus `ALL_ARGUMENTS` (`DATA_LEVEL = 0`, Überladungen über `OVERLOAD`, Position 0 = Rückgabe; der Default-Ausdruck steht nicht im Dictionary, nur `DEFAULTED`), Fehler aus `ALL_ERRORS`, Kopf aus `ALL_OBJECTS`/`ALL_PROCEDURES.AUTHID`/`ALL_TRIGGERS`, Abhängigkeiten aus `ALL_DEPENDENCIES` (Package: Spezifikation und Body zusammen, ohne den Bezug des Bodys auf die eigene Spezifikation). Schemaweite Suche in `ALL_SOURCE` (`SearchSourceAsync`, nur eigenes Schema, `LIKE … ESCAPE`, begrenzt). Den Body eines Packages in einem anderen Schema zeigt Oracle nur dem Eigentümer bzw. mit `DEBUG` – mit `EXECUTE` bleibt er leer.
- Quellen: `ALL_TABLES`, `ALL_VIEWS`, `ALL_MVIEWS`, `ALL_TAB_COLUMNS` (inkl. `IDENTITY_COLUMN`), `ALL_CONSTRAINTS` (P/U/R), `ALL_CONS_COLUMNS`.
- Tabellenliste ohne Recyclebin (`DROPPED`), Nested/Secondary Tables, IOT-Overflow-Segmente und MView-Containertabellen (die MView erscheint einmal als `MaterializedView`).
- `DATA_DEFAULT` ist `LONG` → `OracleSession` setzt `InitialLONGFetchSize` (4000).
- **Immer nach `OWNER` filtern.** `ALL_TAB_COLUMNS` ist auf großen Datenbanken langsam → Tabellenliste und alle FKs des Schemas beim Connect laden, Spalten/Keys lazy pro Tabelle (`SchemaCache`). Cachen, manuell refreshbar.
- Schema-Name aus dem Profil wird normalisiert (`OracleIdentifier.Normalize`): `erp` → `ERP`, `"Erp"` bleibt `Erp`.
- Verbindungsaufbau: Seit WP-24 können mehrere Verbindungen offen sein, eine davon sichtbar (`ConnectionHub` in UI/State: je offene Verbindung ein DI-Scope `ConnectionScope` mit eigener `ActiveConnection`, `WorkspaceManager`, `ClrModelManager`, `PresentationService`, `LinqConsoleService`; `Current`/`Previous`/`Shown`, `Idle` als Platzhalter ohne Verbindung). Je Verbindung öffnet `ActiveConnection` die Explorer-Session über `IDatabaseConnector`, lädt den `SchemaCache`, hängt die Workspaces an (`WorkspaceManager.AttachAsync`) und merkt die Nutzung in `recent.json` (`RecentConnections`). Trennen speichert die Workspaces und schließt alle Sessions. Oracle-Fehler kommen als `DatabaseException` mit ORA-Code an.

### 1.4 Filter & Query
```csharp
enum FilterOperator { Equals, NotEquals, Contains, StartsWith, EndsWith, Gt, Gte, Lt, Lte, Between, In, IsNull, IsNotNull }
record FilterCondition(string Column, FilterOperator Op, IReadOnlyList<string> Values, bool Enabled = true);
    // Werte wie eingegeben (deutsch oder ISO); Anzahl je Operator: 0 / 1 / 2 (Between) / n (In, in der UI mit ";" getrennt)
record SortSpec(string Column, bool Descending);
record PageSpec(int Offset, int Limit);
record QueryParameter(string Name, object? Value, OracleTypeHint Type);
record SelectQuery(string Sql, IReadOnlyList<QueryParameter> Parameters, IReadOnlyList<ResultColumn> Columns, RowKeyKind RowKey, bool HasRowId);
```
`QueryBuilder.BuildSelect(tableDetails, filters, sorts, page)` / `BuildCount(…)` / `Validate(…)`. Regeln (alle mit Unit- und Integrationstests):
- Spaltenkategorien (`ColumnCategories.Of`): Text, Number, Date, Timestamp(+TZ), Boolean, Interval, Raw, Clob, Blob, Long, Unsupported. Operatoren je Kategorie (`FilterRules.OperatorsFor`); kein `Contains` auf NUMBER/DATE, LOB nur LIKE-artig/NULL, RAW `=`/`≠`/`in` mit Hex-Werten (optional `0x`, gebunden als `byte[]`), BLOB/LONG/Unsupported nur NULL-Prüfung.
- Werte: Zahlen deutsch („1.234,5“) oder invariant („1234.5“; ohne Komma ist der Punkt Dezimaltrenner); Datum `TT.MM.JJJJ [hh:mm[:ss]]` oder ISO. Ungültige Eingaben → Validierungsfehler pro Filterzeile, kein SQL.
- Projektion statt `t.*`: CLOB → `DBMS_LOB.SUBSTR(…, 200, 1)` + `GETLENGTH`, BLOB → `GETLENGTH`, LONG/XMLTYPE/Objekttypen → `CASE WHEN … IS NULL THEN 0 ELSE 1 END` (LONG im Select verträgt sich nicht mit `FETCH FIRST`, ORA-00997).
- `t.ROWID` wird bei Tabellen/MViews immer mitselektiert (Views: nein).
- **Immer deterministisch sortieren:** Nutzer-Sortierung + Tiebreaker PK → ROWID → (View ohne Key) alle sortierbaren Spalten. Ohne das liefert `OFFSET/FETCH` doppelte oder fehlende Zeilen zwischen den Seiten.
- Paging: `OFFSET :p_offset ROWS FETCH NEXT :p_limit ROWS ONLY`.
- Spaltennamen in Filter/Sort gegen das Schema validieren, dann per `OracleIdentifier.Quote()` quoten. Werte nur als Bind-Variablen; **jede gebundene Variable muss im SQL vorkommen** (sonst ORA-01036) – Unit-Tests prüfen das für jedes Statement.
- `Contains`/`StartsWith`/`EndsWith`: `%`, `_`, `\` escapen, `UPPER(c) LIKE UPPER(:p) ESCAPE '\'` (immer ohne Groß-/Kleinschreibung).
- `≠` schließt NULL ein, wenn die Spalte nullable ist (wie C#/EF): `(c <> :p OR c IS NULL)`.
- `CHAR`/`NCHAR`-Spalten werden als `OracleDbType.Char` gebunden (Blank-Padding: „AB“ findet „AB␣“).
- `Equals` mit leerem Text: in Oracle nie wahr (`'' = NULL`) → Validierungsfehler mit Hinweis auf „ist NULL“.
- `In`: max. 1000 Elemente pro Liste (ORA-01795) → mehrere Listen mit `OR`.
- Datum ohne Uhrzeit meint den ganzen Tag: `=` → `[d, d+1)`, `>` → `>= d+1`, `≤` → `< d+1`, `zwischen` schließt den letzten Tag ein.
- Daten: `IDataAccess.ReadPageAsync/CountAsync` (`OracleDataAccess`), Zellanzeige über `CellFormatter` (deutsch, NULL bleibt null). NUMBER > 28 Stellen → `BigNumber` (exakter Text).

### 1.5 Row-Identität
```csharp
abstract record RowKey;   // PrimaryKey(IReadOnlyList<object?> Values) | RowId(string Value) | None
```
- PK vorhanden → PK-Werte. Sonst `ROWID` (bei IOTs ist das eine UROWID).
- Views ohne PK → `None` → dauerhaft read-only.
- ROWID ist nicht absolut stabil (`ROW MOVEMENT`, Partitionen, Shrink). Deshalb hat der PK Vorrang.
- Wird in v1 schon für FK-Navigation und als Paging-Tiebreaker genutzt.

### 1.6 Änderungen (v2)
Eine Änderung durchläuft drei Zustände, die in Modell und UI explizit sichtbar sind:

| Zustand | Bedeutung | Für andere Workspaces sichtbar? | Row-Locks? |
|---|---|---|---|
| **Pending** | nur lokal im `ChangeTracker` | nein | nein |
| **Flushed** | DML in der Session ausgeführt, nicht committed | nein | **ja** |
| **Committed** | committed | ja | nein |

```csharp
enum RowState { Unchanged, Modified, Added, Deleted }
class RowChange { RowKey Key; RowState State; Dictionary<string, object?> Original; Dictionary<string, object?> Current; }
```
- `ChangeTracker` sammelt pro Tab.
- `OracleDataAccess.FlushAsync(changes, session, ct)` (bewusst nicht „Apply“, um Verwechslung mit dem Filter-Apply zu vermeiden):
  - pro Flush einen `SAVEPOINT` → einzelne Flushes lassen sich zurücknehmen. Seit 3.6.0 sind Grid-Schreibvorgänge und SQL-/LINQ-Statements gemeinsam die `IDataEditor.Actions` der Transaktion; ein Savepoint verwirft alles danach, auch fremde Statements. Seit WP-30 (ADR 0018): Undo bis zu einer gewählten Aktion (sie und alle späteren), Redo der zurückgenommenen in Reihenfolge (Grid: dieselben Änderungen erneut schreiben; Statement: dasselbe SQL nach Bestätigung, abweichende Zeilenzahl wird gefragt), Buchführung in `WriteLog`; jede neue Schreibaktion, Commit und Rollback beenden Redo. `WriteAction.Statements` hält das ausgeführte SQL mit Bind-Werten (nur für die UI, nie im Log).
  - ausstehende Bearbeitungen haben je Tab einen Verlauf im `ChangeTracker` (Ctrl+Z/Ctrl+Y im Grid, Zelle für Zelle); ein Schreibvorgang beendet ihn.
  - die Übersicht „Offene Änderungen“ (Panel rechts, über den Zähler der Statusleiste) zeigt Ausstehendes je Tab, Geschriebenes mit SQL und Zurückgenommenes des aktiven Workspaces.
  - vor jedem Update/Delete `SELECT … FOR UPDATE WAIT n` (n konfigurierbar, Default 3 s) → `ORA-30006` (Oracle 23: `ORA-00054`) statt endlosem Warten
  - `UPDATE t SET c=:v WHERE <RowKey>` (+ optional Original-Werte im WHERE für Concurrency, konfigurierbar)
  - `INSERT INTO t (...) VALUES (...) RETURNING ROWID INTO :rid` (bzw. PK)
  - `DELETE FROM t WHERE <RowKey>`
  - alles mit `BindByName = true`
- Commit/Rollback nur auf expliziten Nutzerbefehl. Nach Rollback die betroffenen Tabs neu abfragen.
- **DDL (WP-22, ADR 0019)** läuft über einen eigenen Weg außerhalb jeder Transaktion: `OracleSession.ExecuteDdlAsync` (`internal`, ein DDL-Statement, nie bei offener Transaktion, nie gesperrt) ← `IDataEditor.ExecuteDdlAsync` ← `WorkspaceManager.ExecuteDdlAsync` (prüft `IsWritable`). Oracle committet das DDL sofort; es wird keine Aktion des Undo-Stapels. Hat der Workspace Arbeit, verwirft der SQL-Editor sie vorher – nur nach Bestätigung mit der Liste der offenen Änderungen. Skripte: keine DML vor einem DDL-Statement (sie würde still committet), DML danach bleibt in der Transaktion.
  - Nach DDL (und bei „Schema neu laden“) lädt `WorkspaceLifecycle.RefreshSchemaAsync` den Schema-Cache neu, schließt Tabs verschwundener Objekte, baut Tabellen-Tabs mit geänderter Struktur neu auf (`TableTab.StructureVersion` als `@key` der `TabView`; Tabs mit ausstehenden oder geschriebenen Änderungen behalten ihre Struktur, weil diese Spalten nach Position adressieren) und gleicht das C#-Modell ohne Hilfsprozess neu ab (`ClrModelManager.RemapAsync`).
  - Fremde offene Transaktionen: `DROP` scheitert mit ORA-00054, `ALTER TABLE … ADD` wartet unabbrechbar, bis sie enden – die Bestätigung nennt andere Workspaces der Verbindung mit offener Schreib-Transaktion und ihre Tabellen.

## 2. UI-Konzept

```
┌─────────────────────────────────────────────────────────────────┐
│ [● ERP Test ▾]   Workspaces: [Bug 3711] [Feature ABC] [+]        │  ← farbiger Rahmen je ConnectionKind
├───────────┬─────────────────────────────────────────────────────┤
│ A         │ Tab: KUNDEN  │ Tab: AUFTRAG                          │
│ B  KUNDEN │ ┌─ Filterzeilen (TablePlus-Stil) ──────[SQL][Apply]─┐│
│ C  …      │ │ [KUNDE_ID ▾] [= ▾] [4711        ] [−][+]          ││
│ …         │ └───────────────────────────────────────────────────┘│
│ (Letter-  │  Grid (virtualisiert, NULL kursiv; v2: Inline-Edit)   │
│  Index)   │  Rechtsklick → FK-Navigation                         │
│           ├──────────────────────────────────────────────────────┤
│           │ Statusleiste: 1.234 Zeilen geladen · v2: 3 Pending,  │
│           │ 2 Flushed · Tx seit 4 min · [Flush][Commit][Rollback]│
└───────────┴─────────────────────────────────────────────────────┘
```
- **Verbindungen** (es sind im Alltag 12+): oben links nur die sichtbare Verbindung als Umschalter. Seit WP-24 bleiben gewechselte Verbindungen im Hintergrund offen (Abschnitt „Offen“ im Umschalter mit „angezeigt“/„verbunden“/„N Aktionen offen“/„Verbindung verloren“ und „Trennen“); `Alt+O` springt zur vorigen. Nicht committete Arbeit im Hintergrund steht in der Statusleiste; eine im Hintergrund verlorene Verbindung wird beim Wechsel mit Banner gezeigt (neu verbunden erst über „Neu verbinden“). Klick oder `Ctrl+Shift+O` öffnet ein Popover mit Suche, „Zuletzt verwendet“ (ab WP-03), einklappbaren Gruppen und Pfeiltasten-/Enter-Bedienung; „⋯“ je Zeile → Bearbeiten, Duplizieren, Löschen (mit Bestätigung).
- **Startseite „Verbindungen“**: Übersicht aller Verbindungen nach Gruppen (Name, Umgebung, Adresse, Benutzer → Schema, „⋯“-Menü). Erscheint, solange keine Verbindung aktiv ist, und über „Alle Verbindungen verwalten …“ im Popover. Doppelklick verbindet (ab WP-03; bis dahin: bearbeiten).
- **Chips in der Topbar** sind die Workspaces der aktiven Verbindung (WP-05), nicht die Verbindungen.
- Tabellenliste alphabetisch (Tabellen und Views unterscheidbar), Buchstabenleiste links: Klick springt zur ersten Tabelle mit diesem Buchstaben; Buchstaben ohne Treffer ausgegraut. (Fuzzy-Suche = Backlog.)
- Prod: roter Rahmen; Profile mit „Schreibgeschützt“ (Prod voreingestellt) schreiben erst nach dem Freischalten eines Workspaces (Badge, Statusleiste oder „⋯“-Menü; auf Prod mit eingetipptem Verbindungsnamen, seit 2.0).
- Grid-Spalten werden aus dem Schema erzeugt und als Column-Definitions an AG Grid übergeben (eigener Header-Renderer: Name, Oracle-Typ, NOT NULL, PK/FK-Badges). AG Grid fragt Blöcke à 500 Zeilen per `invokeMethodAsync` bei .NET an (Infinite Row Model); Zeilen gehen als Dictionaries mit Row-Index über die Grenze.
- Header-Klick sortiert serverseitig: AG Grid liefert das Sort-Model im Datasource-Request, .NET fragt neu ab.
- Spalten anheften (v1.4): Rechtsklick auf den Header öffnet `ColumnMenu` (Blazor; `grid.js` liest die Spalte aus `col-id` der `.ag-header-cell`, weil AG Grids `columnHeaderContextMenu` keine Mausposition liefert), derselbe Eintrag steht im Zellen-Kontextmenü. Ziehen in den angehefteten Bereich meldet `grid.js` per `OnPinnedChanged` (nur UI-Quellen, nicht `api`). Der PK hat `lockPinned` + `lockPosition: 'left'`. Spalten-IDs bleiben `c<Schema-Index>`, nur die Reihenfolge der Column-Defs ändert sich.
- Spaltensuche (v1.5, Entscheidung des Nutzers: nur Springen, kein Ausblenden; Suche nur nach Namen): `ColumnSearch` (Core/Query) – alle Wörter müssen vorkommen, Unterstriche optional, Reihenfolge exakt → Präfix → Wortanfang → enthält, je Gruppe Schema-Reihenfolge. `ColumnList` zeigt die Treffer, die Eingabe gehört der Eltern-Komponente: `ColumnPicker` (Spaltenwahl der Filterleiste statt `<select>`; Klick in die Liste per `mousedown` mit `preventDefault`, sonst schließt das `blur` die Liste vor dem Klick) und `ColumnJump` (Ctrl+F). Pfeiltasten wie im Verbindungs-Umschalter ohne JS-Abfangen.
- Kontextmenü (FK-Navigation, Kopieren) und Dialoge sind Blazor-Komponenten; AG Grid meldet nur das `cellContextMenu`-Event (Zeilenindex, Spalte, Mausposition), `grid.js` unterdrückt das WebView-Kontextmenü im Grid (`GridContextMenu`).
- Ansichten eines Tabs (v1.7, flach nebeneinander, Entscheidung des Nutzers): Daten | Spalten | Constraints | Indizes | Abhängigkeiten | DDL (`TabMode`, gespeichert). Über allen Nicht-Daten-Ansichten steht `ObjectHeader` (Kommentar, Status, Daten, Statistik). Jede Ansicht mountet beim ersten Öffnen und bleibt dann (wie das Grid); die Detailansichten erben von `DetailViewBase<T>` (Laden auf der Explorer-Session, Abbruch, Fehler, Neuladen über `Version` = F5). Ab etwa zehn Einträgen die seltenen unter „Mehr ▾“ zusammenfassen.
- PL/SQL (WP-28, Entscheidungen des Nutzers): Der Explorer schaltet zwischen „Tabellen“ und „PL/SQL“ um (je Verbindung für die Sitzung); die Namenssuche filtert den aktiven Bereich und nennt Treffer im anderen; „Quelltext“ neben dem Suchfeld sucht schemaweit im PL/SQL (Enter, Treffer nach Objekt, Klick öffnet an der Zeile). Ein PL/SQL-Tab (`PlSqlTab`, `PlSqlTabView`) hat die Ansichten Spezifikation bzw. Quelltext | Body (Packages) | Parameter (nicht bei Triggern) | Fehler (Anzahl im Umschalter, Klick springt in den Quelltext) | Abhängigkeiten, darüber `PlSqlHeader`. Quelltext in Monaco nur lesend mit eigener PL/SQL-Färbung (`plsql` in `monaco.js`) und den Kompilierfehlern als Marker; verschlüsselter Code (wrapped) zeigt einen Hinweis. Die Ansichten erben von `PlSqlViewBase<T>` (gemeinsame Basis `LoadingViewBase<T>` mit den Detailansichten der Tabellen). In den Abhängigkeiten (`DependencyList`, auch bei Tabellen) sind bekannte PL/SQL-Objekte Links; `PACKAGE BODY` öffnet den Body.
- Formularansicht (WP-21, Entscheidungen des Nutzers): eine Zeile als **Seitenleiste rechts neben dem Grid** (`RowFormPanel`, Breite ziehbar), der Vergleich markierter Zeilen als **Dialog** (`RowCompareDialog`, nur lesen, 2–20 Zeilen). **Das Grid führt:** Das Formular zeigt die Zeile der fokussierten Zelle (`FerretGrid.Focused`/`FocusChanged`), ▲ ▼ bewegen nur den Fokus im Grid (`focusRow` in grid.js) – Blöcke lädt AG Grid wie gewohnt, nach Schreiben/Neuladen findet das Formular die Zeile über `RowBlocks.IndexOf(RowKey)` wieder. Felder kommen aus `RowForm` (Core/Forms), Editieren über `FerretGrid.EditFromForm` (derselbe Kern wie `OnCellEdit`, synchron). Getippte, noch nicht bestätigte Werte übernimmt `ShellState.BeforeWrite` vor Ctrl+S/Commit. FK-Sprünge aus dem Formular nehmen den aktuellen (auch ausstehenden) Wert; eingehende FKs zählt `FkCounts` (gemeinsam mit dem Kontextmenü) erst, wenn die Zeile 400 ms gewählt bleibt. Spaltenkommentar nur als Tooltip.

## 3. Shortcuts

Seit WP-25 (3.16.0) unter Einstellungen › Tastenkürzel änderbar; die Tabelle zeigt die Standardbelegung (`ShortcutMap.Definitions`, fest: `ShortcutMap.Fixed`).

**Neue Aktionen bekommen keine Standardtaste** (Entscheidung des Nutzers, nach 3.20; `Default` = null): Eine gespeicherte Taste einer anderen Aktion könnte schon darauf liegen, und bei doppelter Belegung gewinnt die erste Definition – die neue Vorgabe würde dem Nutzer seine Taste stillschweigend wegnehmen. Der Nutzer vergibt die Taste selbst. Die Standardtasten bis 3.20 bleiben, wie sie sind.

| Taste (Standard) | Aktion | Version |
|---|---|---|
| Ctrl+Shift+O | Verbindungs-Umschalter öffnen | v1 |
| Alt+O | Zur vorigen offenen Verbindung wechseln (ohne Trennen) | WP-24 |
| Ctrl+Enter | Filter anwenden | v1 |
| Ctrl+C | Im Grid: Wert der fokussierten Zelle; bei mehreren markierten Zeilen diese als Tabelle (Tab-getrennt, mit Kopfzeile). Mit der Maus markierter Text innerhalb einer Zelle wird normal kopiert. Kopiert wird immer der volle Wert (`DelimitedExport.CellText`), nicht der gekürzte Anzeigetext. | v1.3 |
| F5 | Refresh (v2 in Read-only-Tx: neue Transaktion) | v1 |
| Ctrl+F | Datenansicht: Spalte suchen und hinspringen (Scrollen, Hervorheben, Fokus auf die Zelle der ersten sichtbaren Zeile); Strukturansicht: Spalten filtern (v1.6) | v1.5 |
| Alt+← / Alt+→ | Zurück zum Tab, aus dem ein FK-Sprung kam / wieder vor (verhindert nebenbei die Zurück-Navigation der WebView) | v1.6 |
| – (frei belegbar) | Aktiven Tab schließen; fragt vorher (auch beim ✕) bei ausstehenden Änderungen, bei einem eingegebenen, nicht bestätigten Wert (Zelle im Grid oder Feld im Formular) und bei Text im SQL-/LINQ-Tab | 3.21 |
| Ctrl+P | Tabelle suchen (Backlog) | – |
| Ctrl+S | Pending-Änderungen flushen (kein Commit) | v1.9 |
| Ctrl+Shift+Enter | Commit (auf Prod immer mit Bestätigung) | v1.9 |
| Ctrl+Shift+L | Neue LINQ-Konsole (mit verknüpftem C#-Projekt); im LINQ-Tab führen Ctrl+Enter und F5 aus, Ctrl+F sucht im Editor, Ctrl+Leertaste schlägt Member/Typen vor (3.5) | 2.3 |
| Ctrl+Shift+Q | Neuer SQL-Editor; im SQL-Tab führen Ctrl+Enter und F5 das Statement am Cursor aus, Ctrl+F sucht, Ctrl+Leertaste schlägt Tabellen/Spalten vor | 3.2 |
| Alt+X | Im SQL-Tab: das ganze Skript (bzw. die markierten Statements) nacheinander ausführen | 3.3 |
| Alt+Enter | Im Grid: Formular der fokussierten Zeile öffnen/schließen; bei mehreren markierten Zeilen: vergleichen. Im Formular: schließen. Nur lokal (nicht in `shortcuts.js`), damit Monaco seine Tasten behält | WP-21 |
| Alt+↑ / Alt+↓ | Im Formular: vorige/nächste Zeile (bewegt den Fokus im Grid; im Grid selbst reichen ↑/↓). Nur lokal | WP-21 |
| – | Rollback nur über Button, mit Bestätigung | v2 |

`Esc` bleibt dem Grid vorbehalten (Zelleingabe abbrechen) bzw. schließt Menüs/Dialoge. `F12` öffnet im Debug-Build die DevTools.

Technik (WP-25):
- `KeyChord` (Core/Settings): Textform `ctrl+shift+alt+<KeyboardEvent.key klein>` (`space`, `plus`), wie `comboOf` in `shortcuts.js` sie bildet; `settings.json` speichert in `AppSettings.Shortcuts` (`ShortcutOverrides`) nur Abweichungen je Aktionsname, leer = nicht belegt, unbekannte Einträge bleiben erhalten.
- `ShortcutMap.Check`: abgelehnt werden von Windows/WebView belegte Kombinationen (Alt+F4, Alt+Leertaste, F12, Ctrl+Shift+I …), Ctrl+Alt (= AltGr) und Tasten ohne Ctrl/Alt außer F-Tasten; Konflikt mit einer anderen Aktion → „Übernehmen“ nimmt sie dort weg; Warnung bei Tasten der Editoren/des Grids (Ctrl+Z, Ctrl+Leertaste …) und bei Alt+Shift.
- Globale Kürzel registriert die `Shell` über `shortcuts.js` (Capture-Listener → `OnShortcut` → `ShortcutMap.ActionFor`), neu bei jeder Änderung (`ShortcutService.Changed`). Lokal: die Formular-Taste liest `grid.js` aus `shortcuts.js` (`setLocal`/`localCombo`), das Formular prüft seine Tasten über `ShortcutService.Is`.
- Anzeige nie als festen Text: `<Kbd Action="…" />`, `Shortcuts.Hint(…)` („ (Ctrl+S)“) und `Shortcuts.With(…)` („ mit Ctrl+Enter“) – leer, wenn die Aktion nicht belegt ist.
- Grenze: `e.key` ist das Zeichen der Tastaturbelegung (Ctrl+Shift+7 heißt auf deutscher Tastatur „Ctrl+/“); Aufnahme und Erkennung stimmen trotzdem überein.
