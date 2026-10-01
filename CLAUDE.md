# FerretSharp – Oracle explorer for .NET developers

> Projektanweisungen für Claude Code. Bitte vollständig lesen, bevor ein Arbeitspaket umgesetzt wird.
> Arbeitssprache mit dem Nutzer: **Deutsch**. Code, Kommentare und Commit-Messages: **Englisch**.
> Stand: 2026-10-01

## 1. Ziel

Ein eigener Datenbank-Editor für **Oracle**, der stärker auf den eigenen Arbeitsablauf zugeschnitten ist als DBeaver.
Kernideen, die das Tool von DBeaver abheben:

- **Workspaces** (Unter-Sessions) pro Verbindung, jeder mit eigener Oracle-Connection (und ab v2 eigener Transaktion) → zwei Workspaces können dieselbe Tabelle unabhängig voneinander ansehen und später bearbeiten.
- **FK-Navigation** im Grid: von einer Zeile zu referenzierten/referenzierenden Zeilen springen, inkl. virtueller FKs für Schemas ohne deklarierte Constraints.
- **Zusammenbaubare Filter** (TablePlus-Stil): Liste aus (Spalte, Operator, Wert), daraus wird das WHERE generiert.
- **Sandbox-Editing** (v2): alle Änderungen laufen in einer Transaktion, Commit/Rollback explizit.
- Prod-Verbindungen sind visuell markiert und standardmäßig read-only.

**Langfristiges Ziel (v3): Brücke zur C#-/EF-Core-Welt.** FerretSharp kennt das Datenmodell des eigenen .NET-Projekts (DbContext): Entity- und Property-Namen statt nur Tabellen/Spalten, Enums mit C#-Namen, Navigation Properties als Beziehungen, ausführbare LINQ-Queries mit Anzeige des generierten SQL, Code-Generierung aus Daten. v1 und v2 sind der Unterbau dafür – Architekturentscheidungen dort müssen v3 mitdenken (siehe Abschnitt 2).

Der Nutzer ist erfahrener .NET-Entwickler (Rider, Blazor, SignalR). Erklärungen auf Senior-Niveau, keine Grundlagen.

## 2. Versionen

Es wird in Versionen ausgeliefert. Jede Version ist für sich benutzbar.

| Version | Inhalt | Arbeitspakete |
|---|---|---|
| **v1 – Read-only Browser** | Connections, Schema, Grid, Filter, Workspaces, FK-Navigation, Export | WP-01 … WP-07 |
| **v2 – Sandbox-Editing** | Transaktionsmodell, Editieren, Commit/Rollback, Lock-Handling, Prod-Freischaltung | WP-08 … WP-10 |
| **v3 – .NET-Integration** | DbContext-Modell laden, Schema-Anreicherung (Entities, Enums, Navigations), LINQ-Konsole, Code-Generierung | WP-11 … WP-14 |
| **v4+** | Backlog (Abschnitt 10) | – |

Regeln für **v1**:
- Es gibt **keinen** Codepfad, der DML/DDL erzeugt oder ausführt. `IDataAccess` und `OracleSession` bieten in v1 nur lesende Methoden (kein öffentliches `ExecuteNonQuery`). Einzige Ausnahme: INSERT-Statements als **Text-Export** (werden nie ausgeführt).
- v1-Sessions laufen ohne explizite Transaktion → jede Abfrage sieht den aktuellen Commit-Stand (Statement-Level-Konsistenz).
- Trotzdem für v2 vorbauen: Row-Key immer mitselektieren (Abschnitt 5.5), eine Session pro Workspace, `TabState` erweiterbar.
- Empfehlung an den Nutzer (nicht im Code erzwingbar): für Prod einen DB-User mit reinen SELECT-Grants verwenden.

Vorbereitung auf **v3** (gilt ab WP-01):
- Schema-Records bleiben reine DB-Sicht. Zusätzliche Metadaten (Entity-/Property-Name, CLR-Typ, Enum-Mapping) kommen später über eine separate, nach (Owner, Tabelle, Spalte) adressierte **Annotation-Schicht** hinzu – nicht durch Aufbohren von `TableDetails`/`ColumnInfo`.
- Anzeige und Eingabe von Zellwerten sowie Spaltenbeschriftungen laufen über einen austauschbaren Präsentations-Service (z. B. `IColumnPresentation`), damit v3 Enum-Namen und Property-Namen einhängen kann, ohne Grid/FilterBar umzubauen.
- FK-Quellen sind ein Enum (`Declared`, `Manual`, `Convention`, später `ClrModel`), kein `bool IsVirtual`.
- Das Filtermodell (Spalte, Operator, Werte) bleibt so einfach, dass es sich 1:1 in einen LINQ-`Where`-Ausdruck übersetzen lässt.

Versionierung: SemVer, Git-Tag `vX.Y.Z` pro Release, `CHANGELOG.md` pflegen. Features der nächsten Version werden **nicht** vorgezogen, sondern in `docs/backlog.md` notiert.

## 3. Stack (entschieden – Änderungen nur per ADR in `docs/decisions/`)

| Bereich | Entscheidung | Begründung / Hinweise |
|---|---|---|
| Runtime | **.NET 10 (LTS, Support bis Nov. 2028)**, C# 14 | .NET 8 endet am 10.11.2026. SDK per `global.json` pinnen (`rollForward: latestFeature`). |
| Solution | **`FerretSharp.slnx`** | Standardformat des .NET-10-SDK, Rider-kompatibel, weniger Merge-Konflikte. |
| Pakete | **Central Package Management** (`Directory.Packages.props`) | Versionen an einer Stelle. |
| UI | **Blazor Hybrid**: WPF-Host mit `BlazorWebView` (`Microsoft.AspNetCore.Components.WebView.Wpf`), Komponenten in einer Razor Class Library | ADR 0004. Windows-only reicht; WebView2-Runtime ist auf Windows 10/11 vorhanden. App-TFM **`net10.0-windows10.0.19041.0`** (mit `net10.0-windows` stürzt BlazorWebView beim Start ab). |
| Theme | eigenes CSS mit Design-Tokens (`ferretsharp.css`), hell/dunkel über `prefers-color-scheme` | Titelleiste per DWM passend gefärbt (`WindowTheme`). `--theme=dark\|light` übersteuert Windows. **Nicht verwenden:** WPF-UI, AvalonDock, CommunityToolkit.Mvvm, AvalonEdit, CSS-Frameworks. |
| UI-Zustand | Komponenten + schlanke State-/Service-Klassen in `FerretSharp.UI` | Kein MVVM-Framework. Lange Operationen async mit `CancellationToken`. |
| JS-Interop | ein ES-Modul pro Thema in `FerretSharp.UI/wwwroot/js`, Aufruf über `IJSObjectReference` | JS bleibt dünn (Grid-Brücke, Zwischenablage, Scrollen, Fokus). **Keine Geschäftslogik in JS.** |
| Hosting/DI | `Microsoft.Extensions.Hosting` | DI, Konfiguration, Logging ab WP-01. Die BlazorWebView nutzt den Service Provider des Hosts. |
| Logging | `Microsoft.Extensions.Logging` + **Serilog** (`Serilog.Extensions.Hosting`, `Serilog.Sinks.File`) | Datei unter `%APPDATA%\FerretSharp\logs`. Keine Bind-Werte von Prod-Verbindungen loggen (maskieren). |
| Grid | **AG Grid Community** (MIT) über JS-Interop, **Infinite Row Model** | Datenblöcke und Sortierung kommen aus .NET (`IDataAccess`), gekapselt in einer `FerretGrid`-Komponente. Version 34.x, **lokal im Repo** unter `FerretSharp.UI/wwwroot/lib/ag-grid/` (kein CDN). Vor dem Herunterladen den Nutzer fragen (WP-04). Enterprise-Features (Kontextmenü, Zellbereich) nicht verwenden – eigene Lösungen in Blazor. |
| Layout | Tabs + Seitenleiste in Blazor | Kein Docking-Framework. |
| SQL-Anzeige | eigener Highlighter in Razor (siehe Prototyp `TableView.razor`) | Vollwertiger Editor (Monaco) erst mit dem freien SQL-Editor (Backlog). |
| Oracle | `Oracle.ManagedDataAccess.Core` (23.x) | rein managed, kein Instant Client; **durchgängig async** mit `CancellationToken`. |
| Oracle-Version | Ziel **19c+**; 12.2 sollte funktionieren | `OFFSET/FETCH`, `ALL_TAB_IDENTITY_COLS` erst ab 12c. Kein ROWNUM-Fallback. |
| Tests | **xUnit v3** auf **Microsoft Testing Platform** + NSubstitute; Integration: **Testcontainers.Oracle** | Kein VSTest (`Microsoft.NET.Test.Sdk`/`xunit.runner.visualstudio` nicht verwenden). Image `gvenzl/oracle-free:23-slim-faststart`. Benötigt Docker. |
| Persistenz | JSON-Dateien (`System.Text.Json`) | Polymorphie über `[JsonPolymorphic]`/`[JsonDerivedType]`; keine `object`-Properties (werden zu `JsonElement`). |
| Secrets | Windows Credential Manager via **`Meziantou.Framework.Win32.CredentialManager`** | Implementierung liegt im **App**-Projekt (Windows-only), Core kennt nur `ISecretStore`. Passwörter nie im JSON. |
| Paketquellen | repo-lokales `nuget.config` (nur nuget.org) | Auf dem Entwicklungsrechner ist global zusätzlich eine DevExpress-Quelle eingerichtet; CPM verlangt dann Source Mapping. |
| CI | vorerst keine (nur lokal) | Sobald das Hosting feststeht: Linux-Job (Core + Unit- + Integrationstests), Windows-Job (ganze Solution). |
| Distribution | `dotnet publish` self-contained | Installer/Auto-Update (Velopack) = Backlog. |

**Nicht** verwenden: Entity Framework für den generischen Zugriff (kennt Schema nur über DbContext). EF-Integration ist ein späteres, optionales Feature (siehe Backlog).

## 4. Solution-Struktur

```
FerretSharp.slnx
├─ src/
│  ├─ FerretSharp.Core/                # net10.0 – reine Logik, KEIN WPF, KEINE Windows-only-APIs, baut unter Linux
│  │  ├─ Connections/                  # ConnectionProfile, OracleAddress, ConnectionStore, ISecretStore
│  │  ├─ Schema/                       # SchemaCache, TableSummary, TableDetails, ColumnInfo, ForeignKeyInfo, ISchemaReader
│  │  ├─ Query/                        # FilterCondition, FilterOperator, QueryBuilder, QuerySpec, QueryParameter, SortSpec, PageSpec
│  │  ├─ Data/                         # RowSet, RowKey, IDataAccess   (v2: RowChange, ChangeTracker)
│  │  ├─ Workspaces/                   # Workspace, WorkspaceStore, TabState
│  │  └─ Oracle/                       # OracleSession, OracleSchemaReader, OracleDataAccess, OracleTypeMapper, OracleIdentifier
│  ├─ FerretSharp.UI/                  # net10.0, Razor Class Library – plattformneutral, KEIN WPF/Windows
│  │  ├─ Shell.razor                   # Root-Komponente (Topbar, Explorer, Tabs, Statusleiste)
│  │  ├─ Components/                   # LetterIndexBar, FilterBar, FerretGrid, SqlPreview, ContextMenu, Dialoge …
│  │  ├─ State/                        # UI-Zustand (aktiver Workspace, Tabs), Services-Interfaces für den Host
│  │  └─ wwwroot/                      # css/ferretsharp.css, js/*.js (ES-Module), lib/ag-grid/
│  └─ FerretSharp.App/                 # net10.0-windows10.0.19041.0 – schlanker WPF-Host
│     ├─ Views/MainWindow.xaml         # nur die BlazorWebView
│     ├─ Services/                     # WindowTheme, DialogService (native Fehler), CredentialManagerSecretStore
│     ├─ wwwroot/index.html            # Host-Page, bindet _content/FerretSharp.UI/… ein
│     └─ App.xaml                      # Generic Host, Serilog, Exception-Handler
├─ tests/
│  ├─ FerretSharp.Core.Tests/          # schnell, ohne DB
│  └─ FerretSharp.Integration.Tests/   # Testcontainers, überspringt sauber, wenn kein Docker verfügbar
├─ docs/
│  ├─ decisions/                       # ADRs, eine Datei pro Entscheidung
│  └─ backlog.md
├─ CLAUDE.md
├─ CHANGELOG.md
├─ global.json
├─ Directory.Build.props               # Nullable enable, ImplicitUsings, TreatWarningsAsErrors
├─ Directory.Packages.props
└─ nuget.config
```

Regeln:
- `FerretSharp.Core` und `FerretSharp.UI` dürfen **kein** `System.Windows` und keine Windows-only-APIs referenzieren (CA1416). CI-Check: `dotnet build src/FerretSharp.Core` und `src/FerretSharp.UI` unter Linux.
- Was nur der Host kann (Credential Manager, native Datei-Dialoge, Fenster), definiert die UI/Core als Interface; die Implementierung liegt in `FerretSharp.App`.
- Alles, was Oracle-spezifisch ist, liegt hinter Interfaces (`ISchemaReader`, `IDataAccess`), damit Unit-Tests mit Mocks laufen.
- SQL-Strings entstehen ausschließlich in `Query/` (QueryBuilder) und `Oracle/` (Schema-Reader, Session-Setup, v2: DML). Komponenten in `FerretSharp.UI` enthalten kein SQL (Ausnahme: reine Anzeige eines vom QueryBuilder erzeugten Statements).
- Der Prototyp auf Branch `spike/blazor-hybrid` (`prototypes/FerretSharp.Prototype.Blazor`) ist Referenz für Look & Feel und die AG-Grid-Brücke – Code daraus übernehmen und sauber in die Architektur einpassen, nicht 1:1 kopieren (Fake-Daten, SQL im UI).
- `QueryBuilder` kennt den Oracle-Treiber nicht: Er liefert eigene `QueryParameter`, das Mapping auf `OracleParameter` passiert in `OracleSession`.

## 5. Domänenmodell

### 5.1 ConnectionProfile
```csharp
record ConnectionProfile(
    Guid Id,
    string Name,
    ConnectionKind Kind,          // Prod | Test | Dev | Other → bestimmt Farbe
    OracleAddress Address,        // HostPort(host, port, serviceName?, sid?) | TnsAlias(alias, tnsAdminPath?)
    string User,
    string? DefaultSchema,        // falls technischer User, aber anderes Schema angeschaut wird
    bool ReadOnly);               // bei Prod default true; wirkt ab v2 (in v1 ist alles read-only)
```
- Passwort separat über `ISecretStore.Get(profileId)` / `Set(...)`.
- `TnsAlias` braucht den Ort der `tnsnames.ora`: Der Managed Driver liest **nicht** die Registry. Reihenfolge: `tnsAdminPath` im Profil → Umgebungsvariable `TNS_ADMIN` → Fehlermeldung mit Hinweis.
- Connection-Datei: `%APPDATA%\FerretSharp\connections.json`, optional zusätzlich pro Repo (`.ferretsharp/connections.json`, ohne Secrets).

### 5.2 Workspace
```csharp
class Workspace {
    Guid Id; Guid ConnectionId; string Name;          // z. B. "Bug 3711"
    List<TabState> Tabs;                             // Tabelle + Filter + Sort (+ grobe Scrollposition)
    string Notes;                                    // Freitext
    List<SavedQuery> Queries;
    List<VirtualForeignKey> VirtualFks;              // manuell definierte FKs
}
```
- Zur Laufzeit hält jeder Workspace eine **eigene** `OracleSession` (eigene Connection, ab v2 eigene Transaktion).
- Connection-String mit `Pooling=false`: Die Sessions leben lange, und eine Connection mit offener Transaktion darf nie in einen Pool zurückgehen.
- Beim Öffnen `ModuleName = "FerretSharp"`, `ActionName = <Workspace-Name>`, `ClientInfo` setzen → in `V$SESSION` ist erkennbar, welcher Workspace eine Sperre hält.
- Persistenz unter `%APPDATA%\FerretSharp\workspaces\{id}.json`. Workspace schließen = Zustand speichern; öffnen = wiederherstellen.

### 5.3 Schema
```csharp
enum TableKind { Table, View, MaterializedView }
record TableSummary(string Owner, string Name, TableKind Kind);                       // beim Connect geladen
record TableDetails(TableSummary Table, IReadOnlyList<ColumnInfo> Columns,
                    IReadOnlyList<string> PrimaryKey, IReadOnlyList<IReadOnlyList<string>> UniqueKeys,
                    bool IsIndexOrganized);                                           // lazy pro Tabelle
record ColumnInfo(string Name, string OracleType, int? Precision, int? Scale, bool Nullable, bool IsIdentity, string? Default, int Position);
record ForeignKeyInfo(string Name, string FromTable, IReadOnlyList<string> FromColumns, string ToTable, IReadOnlyList<string> ToColumns, FkSource Source);
enum FkSource { Declared, Manual, Convention /* v3: ClrModel */ }
```
- Quellen: `ALL_TABLES`, `ALL_VIEWS`, `ALL_MVIEWS`, `ALL_TAB_COLUMNS`, `ALL_CONSTRAINTS` (P/U/R), `ALL_CONS_COLUMNS`, `ALL_TAB_IDENTITY_COLS`.
- **Immer nach `OWNER` filtern.** `ALL_TAB_COLUMNS` ist auf großen Datenbanken langsam → Tabellenliste und alle FKs des Schemas beim Connect laden, Spalten/Keys lazy pro Tabelle. Cachen, manuell refreshbar.

### 5.4 Filter & Query
```csharp
enum FilterOperator { Equals, NotEquals, Contains, StartsWith, EndsWith, Gt, Gte, Lt, Lte, Between, In, IsNull, IsNotNull }
record FilterCondition(string Column, FilterOperator Op, IReadOnlyList<string> Values); // Werte invariant-culture; Anzahl je Operator: 0 / 1 / 2 (Between) / n (In)
record SortSpec(string Column, bool Descending);
record PageSpec(int Offset, int Limit);
record QueryParameter(string Name, object? Value, OracleTypeHint Type);
record QuerySpec(string Sql, IReadOnlyList<QueryParameter> Parameters);
```
`QueryBuilder.BuildSelect(tableDetails, filters, sorts, page)` → `QuerySpec`. Regeln:
- Werte werden anhand des Spaltentyps aus dem Schema interpretiert (`OracleTypeMapper`); ungültige Eingaben → Validierungsfehler, kein SQL.
- Row-Key immer mitselektieren (Abschnitt 5.5).
- **Immer deterministisch sortieren:** Nutzer-Sortierung + Row-Key als Tiebreaker. Ohne das liefert `OFFSET/FETCH` doppelte oder fehlende Zeilen zwischen den Seiten.
- Paging: `OFFSET :o ROWS FETCH NEXT :n ROWS ONLY`.
- Spaltennamen in Filter/Sort gegen das Schema validieren, dann per `OracleIdentifier.Quote()` quoten. Werte nur als Bind-Variablen.
- Operatoren je Typ einschränken (kein `Contains` auf NUMBER/DATE).
- `Contains`/`StartsWith`/`EndsWith`: `%`, `_` und das Escape-Zeichen escapen, `LIKE … ESCAPE '\'`. Groß-/Kleinschreibung ignorieren optional (`UPPER()` – nutzt dann keinen normalen Index).
- `Equals` mit leerem Wert: in Oracle nie wahr (`'' = NULL`) → UI schlägt `IsNull` vor.
- `In`: max. 1000 Elemente pro Liste (ORA-01795) → mehrere Listen mit `OR` verbinden.
- `Equals` auf `DATE` ohne Uhrzeit → Bereich `col >= :d AND col < :d + 1`.

### 5.5 Row-Identität
```csharp
abstract record RowKey;   // PrimaryKey(IReadOnlyList<object?> Values) | RowId(string Value) | None
```
- PK vorhanden → PK-Werte. Sonst `ROWID` (bei IOTs ist das eine UROWID).
- Views ohne PK → `None` → dauerhaft read-only.
- ROWID ist nicht absolut stabil (`ROW MOVEMENT`, Partitionen, Shrink). Deshalb hat der PK Vorrang.
- Wird in v1 schon für FK-Navigation und als Paging-Tiebreaker genutzt.

### 5.6 Änderungen (v2)
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
  - pro Flush einen `SAVEPOINT` → einzelne Flushes lassen sich zurücknehmen
  - vor jedem Update/Delete `SELECT … FOR UPDATE WAIT n` (n konfigurierbar, Default 3 s) → `ORA-30006` statt endlosem Warten
  - `UPDATE t SET c=:v WHERE <RowKey>` (+ optional Original-Werte im WHERE für Concurrency, konfigurierbar)
  - `INSERT INTO t (...) VALUES (...) RETURNING ROWID INTO :rid` (bzw. PK)
  - `DELETE FROM t WHERE <RowKey>`
  - alles mit `BindByName = true`
- Commit/Rollback nur auf expliziten Nutzerbefehl. Nach Rollback die betroffenen Tabs neu abfragen.

## 6. Oracle-Fallstricke (bei Implementierung beachten)

**Identifier & Werte**
- Identifier sind uppercase, es sei denn, sie wurden mit `"…"` angelegt → Namen immer aus den `ALL_*`-Views übernehmen und per `OracleIdentifier.Quote()` exakt so quoten.
- Leerer String == NULL. Die UI muss NULL explizit darstellen (z. B. kursives `NULL`), nicht als leeres Feld.

**Typen**
- `NUMBER` hat bis zu 38 Stellen, `decimal` nur ~28 → `GetDecimal` wirft. `OracleDataReader.SuppressGetDecimalInvalidCastException = true` setzen bzw. `OracleDecimal` nutzen; Anzeige als String ohne Präzisionsverlust.
- Heuristik für Editor-Typen: Scale 0 & Precision ≤ 9 → int, ≤ 18 → long, sonst decimal/`OracleDecimal`. `NUMBER` ohne Precision → `OracleDecimal`.
- `DATE` enthält eine Uhrzeit; `TIMESTAMP` reicht bis Nanosekunden (`OracleTimeStamp`, nicht `DateTime`); `TIMESTAMP WITH (LOCAL) TIME ZONE` gesondert.
- `CLOB`/`BLOB`/`RAW`/`LONG`: im Grid nur Länge und Vorschau (nicht vollständig laden; z. B. `InitialLOBFetchSize` oder `DBMS_LOB.SUBSTR`). Bearbeitung (v2) in eigenem Editor-Dialog.
- Unbekannte oder exotische Typen (`INTERVAL`, `XMLTYPE`, `JSON`, `BOOLEAN`/`VECTOR` ab 23ai, `BINARY_FLOAT/DOUBLE`, Objekttypen): als read-only Textdarstellung anzeigen, nie abstürzen.

**Sessions & Nebenläufigkeit**
- `OracleConnection` ist nicht thread-safe → pro Session ein `SemaphoreSlim(1,1)` (kein `lock`, wegen `await`); Queries pro Workspace laufen sequenziell.
- Alles async mit `CancellationToken`; Abbruch löst `OracleCommand.Cancel()` aus (Token-Registration). Lang laufende Abfragen (`COUNT(*)`, FK-Counts) sind in der UI abbrechbar.
- Verbindungsabbruch (Idle-Timeout, Firewall, `IDLE_TIME`-Profil) erkennen und laut melden. In v2 gehen dabei uncommittete Änderungen verloren → das muss der Nutzer klar sehen.

**Transaktionen (v2)**
- Uncommittetes Update in Workspace A blockiert ein Update derselben Zeile in B. Ein normales `UPDATE` wartet unbegrenzt; `ORA-00054` gibt es nur bei `NOWAIT`. Deshalb `FOR UPDATE WAIT n` (→ `ORA-30006`) und Dialog mit dem sperrenden Workspace bzw. der Session (`V$SESSION`, falls Rechte vorhanden).
- Lange offene Transaktionen halten Locks und Undo. Die Statusleiste zeigt „Tx offen seit X min · N Zeilen gesperrt“.
- Prod im gesperrten Zustand: `SET TRANSACTION READ ONLY` (Oracle erzwingt das selbst). Achtung, Snapshot-Semantik: Alle Abfragen sehen den Stand vom Transaktionsbeginn. **F5/Refresh beendet die Read-only-Transaktion und startet eine neue.** Muss nach jedem Commit/Rollback neu gesetzt werden. Bei `ORA-01555` automatisch neu starten.
- Concurrency-Check optional über `ORA_ROWSCN` (nur zuverlässig bei `ROWDEPENDENCIES`-Tabellen) oder Original-Werte im WHERE.

**Performance**
- `ALL_*`-Views immer nach `OWNER` filtern (siehe 5.3).
- FK-Spalten sind in Oracle oft nicht indiziert → eingehende FK-Counts lazy, mit Timeout und abbrechbar.
- Großes `OFFSET` wird langsam → Keyset-Paging ist Backlog-Option.

## 7. UI-Konzept

```
┌─────────────────────────────────────────────────────────────────┐
│ [Prod ▾] [Test ▾]   Workspaces: [Bug 3711] [Feature ABC] [+]     │  ← farbiger Rahmen je ConnectionKind
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
- Tabellenliste alphabetisch (Tabellen und Views unterscheidbar), Buchstabenleiste links: Klick springt zur ersten Tabelle mit diesem Buchstaben; Buchstaben ohne Treffer ausgegraut. (Fuzzy-Suche = Backlog.)
- Prod: roter Rahmen; ab v2 Schreiben nur nach Freischalten über Toggle + Bestätigungsdialog.
- Grid-Spalten werden aus dem Schema erzeugt und als Column-Definitions an AG Grid übergeben (eigener Header-Renderer: Name, Oracle-Typ, NOT NULL, PK/FK-Badges). AG Grid fragt Blöcke à 500 Zeilen per `invokeMethodAsync` bei .NET an (Infinite Row Model); Zeilen gehen als Dictionaries mit Row-Index über die Grenze.
- Header-Klick sortiert serverseitig: AG Grid liefert das Sort-Model im Datasource-Request, .NET fragt neu ab.
- Kontextmenü (FK-Navigation, Kopieren) und Dialoge sind Blazor-Komponenten; AG Grid meldet nur das `cellContextMenu`-Event.
- Look & Feel und Interaktionen: siehe Prototyp (Branch `spike/blazor-hybrid`).

**Shortcuts**

| Taste | Aktion | Version |
|---|---|---|
| Ctrl+Enter | Filter anwenden | v1 |
| F5 | Refresh (v2 in Read-only-Tx: neue Transaktion) | v1 |
| Ctrl+P | Tabelle suchen (Backlog) | – |
| Ctrl+S | Pending-Änderungen flushen (kein Commit) | v2 |
| Ctrl+Shift+Enter | Commit (auf Prod immer mit Bestätigung) | v2 |
| – | Rollback nur über Button, mit Bestätigung | v2 |

`Esc` bleibt dem Grid vorbehalten (Zelleingabe abbrechen) bzw. schließt Menüs/Dialoge. Shortcuts werden in der WebView behandelt (JS-Keydown → Blazor); `F12` öffnet im Debug-Build die DevTools.

## 8. Arbeitspakete

Jedes Paket: eigener Branch `wp/NN-kurzname`, am Ende `dotnet build -warnaserror` + `dotnet test` grün, kurzer Eintrag in `docs/decisions/` bei nicht-trivialen Entscheidungen. Vor dem Start eines Pakets kurz den Plan nennen, dann umsetzen.

### v1 – Read-only Browser

#### WP-01 Solution-Gerüst
- `FerretSharp.slnx`, Projekte, `global.json`, `Directory.Build.props`, `Directory.Packages.props`, `.gitignore`, `.editorconfig`, `CHANGELOG.md`, `docs/backlog.md`.
- Generic Host in `App.xaml.cs` (DI, Konfiguration, Serilog-Datei-Logging).
- Globale Exception-Handler (`DispatcherUnhandledException`, `AppDomain.UnhandledException`, `TaskScheduler.UnobservedTaskException`) → loggen + Fehlerdialog.
- WPF-Host mit `BlazorWebView`, Root-Komponente `Shell` aus `FerretSharp.UI` (Topbar, Explorer links, Inhaltsbereich, Statusleiste), Design-Tokens hell/dunkel, dunkle Titelleiste, `--theme`-Override, `ErrorBoundary`.
- Testprojekte: Unit (Dummy-Test), Integration (Testcontainers-Smoke-Test `SELECT 1 FROM DUAL`, überspringt ohne Docker).
- CI-Workflow: zurückgestellt, bis das Hosting feststeht (siehe `docs/backlog.md`).
- ADRs: 0001 .NET 10, 0002 Versionierung (read-only first, .NET-Integration in v3), 0003 Theme WPF-UI (ersetzt), 0004 Blazor Hybrid.
- **Fertig wenn:** App startet und loggt; beide Testprojekte laufen; `dotnet build src/FerretSharp.Core` und `src/FerretSharp.UI` laufen unter Linux.

#### WP-02 Connections
- `ConnectionProfile`, `OracleAddress` (polymorph in JSON), `ConnectionStore` (JSON), `ISecretStore` mit Credential-Manager-Implementierung (App) + In-Memory-Fake (Tests).
- Connection-Dialog (Add/Edit): Name, Kind (Farbe), Host/Port + ServiceName **oder** SID, alternativ TNS-Alias (+ optionaler TNS_ADMIN-Pfad), User, Passwort, Default-Schema, Read-only-Checkbox (bei Prod vorbelegt), „Verbindung testen“ (`SELECT 1 FROM DUAL`, async, abbrechbar).
- `OracleSession`: öffnet die Connection (`Pooling=false`, Module/Action/ClientInfo), `SemaphoreSlim`, `QueryAsync`/`ReaderAsync` mit `CancellationToken`. **Kein** öffentliches `ExecuteNonQuery` in v1.
- **Fertig wenn:** Profil anlegen, testen, speichern, neu laden; Passwort steht nicht im JSON; Integrationstest verbindet sich mit dem Container.

#### WP-03 Schema-Cache & Tabellenliste
- `OracleSchemaReader : ISchemaReader` (Tabellen, Views, MViews, Spalten, PK/UK/FK, Identity, IOT-Flag), immer nach Owner gefiltert, Details lazy.
- `SchemaCache` mit Refresh; Unit-Tests gegen Mock-Reader, Integrationstests gegen ein Testschema im Container (inkl. Quoted Identifiers, composite FKs, View ohne PK, IOT).
- Explorer-Panel: Tabellenliste alphabetisch (Suchfeld mit einfachem Contains-Filter) + `LetterIndexBar`-Komponente.
- **Fertig wenn:** Nach dem Connect erscheinen alle Tabellen/Views, die Buchstabenleiste springt korrekt.

#### WP-04 Grid, Paging & Filter (read-only)
- Läuft zunächst auf einer Default-Session pro Verbindung (Workspaces folgen in WP-05).
- `QueryBuilder` mit vollständigen Tests (alle Operatoren, Typ-Einschränkungen, LIKE-Escaping, IN-Chunking, DATE-Bereich, deterministische Sortierung, Paging, Quoting, Bind-Variablen).
- `RowKey`-Ermittlung (PK → ROWID → None).
- AG Grid einführen: Nutzer fragen, dann Version 34.x lokal nach `FerretSharp.UI/wwwroot/lib/ag-grid/` legen (inkl. Lizenzdatei), in `index.html` einbinden.
- `FerretGrid`-Komponente (AG Grid, Infinite Row Model): Spalten aus dem Schema, serverseitiges Sortieren per Header-Klick, NULL-Darstellung, Typ-Formatierung (DATE, TIMESTAMP, NUMBER ohne Präzisionsverlust – große Zahlen als String übertragen), LOB-Vorschau, Fallback-Darstellung für unbekannte Typen.
- `FilterBar`-Komponente im TablePlus-Stil: Zeilen hinzufügen/entfernen, Operatoren typabhängig, Validierung je Typ, `Apply`, `Apply All`, `Clear`; der `SQL`-Button zeigt das vom `QueryBuilder` erzeugte Statement mit Bind-Variablen (Highlighter-Komponente).
- Paging („nächste 500 Zeilen“ / Endlos-Nachladen) und Zeilenzähler (`COUNT(*)` lazy, abbrechbar).
- **Fertig wenn:** Eine Tabelle mit > 100k Zeilen bleibt flüssig; die Filter erzeugen korrektes SQL (Tests); Seiten überlappen nicht und haben keine Lücken (Integrationstest).

#### WP-05 Workspaces
- `Workspace`, `WorkspaceStore` (JSON), `TabState` (Tabelle, Filter, Sort, grobe Scrollposition = erste sichtbare Zeile, nur wenn bereits geladen).
- UI: Workspace-Leiste pro Verbindung, Workspace anlegen/umbenennen/schließen; jeder Workspace hat eine eigene `OracleSession` (ActionName = Workspace-Name).
- Tabellen-Tabs im Dokument-Bereich gehören zum aktiven Workspace.
- Zustand wird beim Schließen gespeichert und beim Öffnen wiederhergestellt.
- **Fertig wenn:** Zwei Workspaces auf derselben Verbindung öffnen dieselbe Tabelle mit unterschiedlichen Filtern; nach einem App-Neustart sind beide inkl. Filter wieder da.

#### WP-06 FK-Navigation
- FK-Graph aus `SchemaCache` (deklarierte FKs) + `VirtualForeignKey` (manuell, im Workspace gespeichert) + Vorschlag per Namenskonvention (`KUNDE_ID` → `KUNDE.ID`, konfigurierbar).
- Kontextmenü auf Zelle/Zeile: **ausgehend** („verweist auf KUNDE #4711“ → Tab mit Filter öffnen) und **eingehend** („referenziert von AUFTRAG (12), RECHNUNG (3)“ → Counts lazy, mit Timeout, abbrechbar; Tab mit Filter öffnen). Composite FKs unterstützen.
- Dialog zum Anlegen/Bearbeiten virtueller FKs.
- **Fertig wenn:** Ein Sprung legt automatisch eine Filterzeile im Ziel-Tab an; virtuelle FKs überleben einen Neustart.

#### WP-07 Export & Politur v1 → Release v1.0.0
- Keyboard-Shortcuts v1 (siehe Abschnitt 7).
- Export für markierte Zeilen: CSV und INSERT-Statements (nur als Text, wird nie ausgeführt).
- Fehlerdialoge mit Oracle-Fehlercode + Statement (Bind-Werte bei Prod maskiert).
- Verbindungsabbruch-Erkennung mit Reconnect-Angebot.
- `CHANGELOG.md`, Tag `v1.0.0`, self-contained Publish.
- **Fertig wenn:** Der Nutzer kann DBeaver für das reine Lesen/Navigieren ersetzen.

### v2 – Sandbox-Editing

#### WP-08 Transaktionsmodell & Schreibschutz
- `OracleSession` bekommt ein Transaktions-API (Begin, Savepoint, Commit, Rollback, `ExecuteNonQueryAsync`).
- Prod gesperrt → `SET TRANSACTION READ ONLY` mit Snapshot-Semantik (Refresh = neue Tx, `ORA-01555` → Neustart).
- Statusleiste: Tx-Alter, Anzahl Pending/Flushed, Warnung bei Verbindungsverlust mit offener Transaktion.
- **Fertig wenn:** Integrationstest belegt, dass DML auf einer gesperrten Prod-Session von Oracle abgelehnt wird.

#### WP-09 Editieren
- `ChangeTracker`, `RowChange`, Dirty-Markierung im Grid (Zellfarbe je Zustand Pending/Flushed, `cellClassRules`), Inline-Editing über AG-Grid-Cell-Editoren (Validierung in .NET), Zeile hinzufügen/löschen. Kein Editieren bei `RowKey.None` oder exotischen Typen.
- `OracleTypeMapper` Text → Oracle-Typ inkl. Fehlermeldung bei ungültiger Eingabe.
- `OracleDataAccess.FlushAsync` gemäß 5.6 (Savepoint, `FOR UPDATE WAIT n`, RowKey-WHERE, `RETURNING`, `BindByName`).
- Lock-Konflikte (`ORA-30006`, `ORA-00054`) als Dialog, inkl. sperrender Session, falls ermittelbar.
- „Änderungen als SQL anzeigen“.
- **Fertig wenn:** Änderungen in Workspace A sind in B bis zum Commit unsichtbar; ein Lock-Konflikt zwischen A und B erscheint nach ≤ n s als Dialog (Integrationstest); Rollback stellt das Grid zurück; Tests für die Statement-Generierung.

#### WP-10 Prod-Freischaltung & Politur v2 → Release v2.0.0
- Freischalt-Toggle für Prod mit Bestätigungsdialog (Rollback der Read-only-Tx, normale Tx starten); beim Sperren werden offene Änderungen erzwungen committed oder verworfen.
- Shortcuts v2, Commit-Bestätigung auf Prod.
- CLOB/BLOB-Editor-Dialog.
- `CHANGELOG.md`, Tag `v2.0.0`.

### v3 – .NET-Integration

Die Arbeitspakete werden zu Beginn von v3 mit dem Nutzer verfeinert. Grober Zuschnitt:

#### WP-11 Projekt-Anbindung & Modell-Export
- Pro Connection (oder Workspace) ein .NET-Projekt bzw. Build-Output verknüpfen; DbContext-Typ auswählen.
- EF-Core-Modell laden (Entity ↔ Tabelle, Property ↔ Spalte, Value Converter/Enums, Navigations, Owned Types). DbContext-Erzeugung wie `dotnet ef` (`IDesignTimeDbContextFactory`, sonst Host-Builder/Default-Konstruktor).
- **ADR zu Beginn:** In-Process (`AssemblyLoadContext` + `AssemblyDependencyResolver`) vs. Out-of-Process (Hilfsprozess im Kontext des Zielprojekts wie `dotnet ef`, exportiert das Modell als JSON). Tendenz Out-of-Process wegen Versionskonflikten bei EF-Core-/Oracle-Provider-Assemblies; WP-13 (LINQ-Konsole) fließt in die Entscheidung ein.
- Modell landet als Annotation-Schicht im Core (siehe Abschnitt 2), nicht als EF-Abhängigkeit von `FerretSharp.Core`.

#### WP-12 Schema-Anreicherung
- Grid und Explorer zeigen optional Entity-/Property-Namen; Enums mit C#-Namen in Grid und Filter (Dropdown statt Zahl).
- Navigation Properties als zusätzliche FK-Quelle (`FkSource.ClrModel`) in der FK-Navigation.
- Sprung zur Entity-Klasse in Rider.

#### WP-13 LINQ-Konsole
- Roslyn-Scripting gegen den geladenen DbContext, Ergebnis im Grid, generiertes SQL (`ToQueryString()`) daneben.
- Idealerweise auf der Connection/Transaktion des Workspaces, damit eigene uncommittete Änderungen sichtbar sind (beeinflusst das ADR aus WP-11).
- Prod-Schutz gilt auch hier: Ausführung in `SET TRANSACTION READ ONLY`, solange nicht freigeschaltet; `SaveChanges` nur nach Freischaltung.

#### WP-14 Code-Generierung → Release v3.0.0
- Aktive Filter als LINQ kopieren (`.Where(x => x.KundeId == 4711)`).
- Markierte Zeilen als C#-Objektinitialisierer, `HasData()`-Seed oder Bogus-Fixture kopieren.

## 9. Offene UX-Fragen
- Shortcut-Belegung für Commit/Rollback (Abschnitt 7) – vorläufig, Nutzerfeedback einholen.

## 10. Backlog (nach v3)

- Fuzzy-Suche in der Tabellenliste (Ctrl+P-Stil), Gruppierung nach Präfix.
- Freier SQL-Editor mit Ergebnis-Grid (auf gesperrten Sessions nur in `SET TRANSACTION READ ONLY`, da SELECT-only nicht per Parsing garantierbar ist).
- Keyset-Paging für sehr große Tabellen; exakte Scroll-Wiederherstellung.
- Verbindungsoptionen: TCPS/Wallet, Proxy-User, Kerberos/OS-Auth.
- Installer/Auto-Update (Velopack).
- Migrations-Cockpit (Pending Migrations, Schema-Diff Modell ↔ DB).
- Plugins als C#-Scripts (Roslyn).
- Team-Workspace im Repo (`.ferretsharp/`), Verbindungen ohne Passwörter.
- AG Grid auf aktuelle Major-Version (36+) heben, sobald die API-Änderungen geprüft sind.
- Web-Host (`FerretSharp.DevHost`, Blazor Server) mit Fake-Daten, um die UI im Browser mit Hot Reload zu entwickeln und automatisiert zu prüfen.
- bUnit-Tests für UI-Komponenten.

## 11. Arbeitsweise für Claude Code

- Immer das aktuelle Arbeitspaket zu Ende bringen, bevor andere Themen angefasst werden. Ideen, die unterwegs auftauchen, und Features späterer Versionen in `docs/backlog.md` notieren statt umsetzen.
- In v1 keinen schreibenden Codepfad einbauen (Abschnitt 2).
- Core zuerst mit Tests, dann UI. Keine SQL-Strings außerhalb von `Query/` und `Oracle/`.
- Unit-Tests laufen ohne DB (Mocks). Alles, was eine echte DB braucht, gehört in `FerretSharp.Integration.Tests` (Testcontainers) und muss sich ohne Docker sauber überspringen.
- Commits klein und thematisch; Conventional Commits (`feat:`, `fix:`, `test:`, `docs:`).
- Vor dem Abschluss eines Pakets müssen `dotnet build -warnaserror` und `dotnet test` grün sein. Danach dem Nutzer eine kurze Zusammenfassung auf Deutsch geben (was gebaut, was offen, was zu testen).
- Bei Unsicherheit über UX-Details: einfachste Variante bauen und als Frage in der Zusammenfassung bzw. in Abschnitt 9 notieren, nicht blockieren.
- UI end-to-end prüfen, ohne die echten Nutzerdaten anzufassen: App mit `--data-dir=<scratch>` und `--theme=dark|light` starten, Umgebungsvariable `WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS=--remote-debugging-port=9333` setzen und die Seite per Chrome DevTools Protocol (`Runtime.evaluate`) bedienen; Screenshots per `PrintWindow` vom App-Fenster. Für eine Test-DB einen eigenen Container starten (`gvenzl/oracle-free:23-slim-faststart`, `APP_USER`/`APP_USER_PASSWORD`) und danach gezielt per Name entfernen. Im Credential Manager angelegte Test-Einträge über die App wieder löschen.
