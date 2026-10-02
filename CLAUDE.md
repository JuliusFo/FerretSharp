# FerretSharp – Oracle explorer for .NET developers

> Projektanweisungen für Claude Code. Bitte vollständig lesen, bevor ein Arbeitspaket umgesetzt wird.
> Arbeitssprache mit dem Nutzer: **Deutsch**. Code, Kommentare und Commit-Messages: **Englisch**.
> Stand: 2026-10-02 (v1 komplett, WP-07 umgesetzt; Release v1.0.0 vorbereitet)

## 1. Ziel

Ein eigener Datenbank-Editor für **Oracle**, der stärker auf den eigenen Arbeitsablauf zugeschnitten ist als DBeaver.
Kernideen, die das Tool von DBeaver abheben:

- **Workspaces** (Unter-Sessions) pro Verbindung, jeder mit eigener Oracle-Connection (und ab v2 eigener Transaktion) → zwei Workspaces können dieselbe Tabelle unabhängig voneinander ansehen und später bearbeiten.
- **FK-Navigation** im Grid: von einer Zeile zu referenzierten/referenzierenden Zeilen springen (v1: deklarierte FKs; virtuelle FKs für Schemas ohne deklarierte Constraints stehen im Backlog).
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
- Abgesichert durch: `OracleSession.ExecuteReaderAsync` lehnt alles außer reinen Abfragen ab (`IsReadOnlyStatement`: beginnt nach Leerraum/Kommentaren mit `SELECT`/`WITH`, kein `FOR UPDATE`, nur ein Statement) – eine Stolperfalle gegen Programmierfehler, kein SQL-Parser. `ReadOnlyTests` prüfen per Reflection, dass die DB-Typen keine schreibenden Methoden anbieten, und dass alle Statements von `QueryBuilder` und `OracleSchemaReader` die Sperre passieren; ein Integrationstest zeigt, dass `DELETE` abgewiesen wird. In v2 muss die Sperre für die Daten-Session des Transaktions-APIs bewusst umgangen werden (eigene Methode, nicht die Sperre aufweichen).
- Keine Garantie auf Datenbankseite: Hat der DB-User Schreibrechte, könnte ein Fehler in FerretSharp schreiben (ohne Transaktion committet ODP.NET sofort). `SET TRANSACTION READ ONLY` schützt nicht vor DDL (implizites Commit). Die einzige echte Garantie bleibt ein User mit reinen SELECT-Rechten.
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
| Theme | eigenes CSS mit Design-Tokens (`ferretsharp.css`), hell/dunkel über `prefers-color-scheme` | Auswahl System/Hell/Dunkel auf der Einstellungen-Seite (`AppSettings` in `settings.json`, `SettingsStore`). `WindowTheme` setzt Titelleiste (DWM), Fensterhintergrund und `CoreWebView2.Profile.PreferredColorScheme` – zur Laufzeit umschaltbar, CSS und Grid folgen über `prefers-color-scheme`. Die Einstellung wird vor dem Erzeugen des Fensters gelesen (kein weißes Aufblitzen). `--theme=dark\|light` übersteuert für die Sitzung (Tests). In WPF-Dateien `ThemeMode` per Alias auf `FerretSharp.Core.Settings` festlegen (`System.Windows.ThemeMode` kollidiert). **Nicht verwenden:** WPF-UI, AvalonDock, CommunityToolkit.Mvvm, AvalonEdit, CSS-Frameworks. |
| UI-Zustand | Komponenten + schlanke State-/Service-Klassen in `FerretSharp.UI` | Kein MVVM-Framework. Lange Operationen async mit `CancellationToken`. |
| JS-Interop | ein ES-Modul pro Thema in `FerretSharp.UI/wwwroot/js`, Aufruf über `IJSObjectReference` | JS bleibt dünn (Grid-Brücke, Zwischenablage, Scrollen, Fokus). **Keine Geschäftslogik in JS.** |
| Hosting/DI | `Microsoft.Extensions.Hosting` | DI, Konfiguration, Logging ab WP-01. Die BlazorWebView nutzt den Service Provider des Hosts. |
| Logging | `Microsoft.Extensions.Logging` + **Serilog** (`Serilog.Extensions.Hosting`, `Serilog.Sinks.File`) | Datei unter `%APPDATA%\FerretSharp\logs`. Keine Bind-Werte von Prod-Verbindungen loggen (maskieren). |
| Grid | **AG Grid Community 34.3.1** (MIT) über JS-Interop, **Infinite Row Model** | Datenblöcke à 500 und Sortierung kommen aus .NET (`IDataAccess`), gekapselt in `FerretGrid` + `wwwroot/js/grid.js`. Zellen gehen als fertig formatierte Strings über die Grenze (null = NULL), Spalten-IDs `c0`, `c1` … (Oracle-Namen dürfen Punkte enthalten). **Lokal im Repo** unter `FerretSharp.UI/wwwroot/lib/ag-grid/` (Herkunft/Hash in der README dort, kein CDN). Enterprise-Features (Kontextmenü, Zellbereich) nicht verwenden – eigene Lösungen in Blazor. |
| Layout | Tabs + Seitenleiste in Blazor | Kein Docking-Framework. |
| SQL-Anzeige | eigener Highlighter in Razor (siehe Prototyp `TableView.razor`) | Vollwertiger Editor (Monaco) erst mit dem freien SQL-Editor (Backlog). |
| Oracle | `Oracle.ManagedDataAccess.Core` (23.x) | rein managed, kein Instant Client; **durchgängig async** mit `CancellationToken`. |
| Oracle-Version | Ziel **19c+**; 12.2 sollte funktionieren | `OFFSET/FETCH`, `ALL_TAB_IDENTITY_COLS` erst ab 12c. Kein ROWNUM-Fallback. |
| Tests | **xUnit v3** auf **Microsoft Testing Platform** + NSubstitute; Integration: **Testcontainers.Oracle** | Kein VSTest (`Microsoft.NET.Test.Sdk`/`xunit.runner.visualstudio` nicht verwenden). Image `gvenzl/oracle-free:23-slim-faststart`. Benötigt Docker. |
| Persistenz | JSON-Dateien (`System.Text.Json`) | Polymorphie über `[JsonPolymorphic]`/`[JsonDerivedType]`; keine `object`-Properties (werden zu `JsonElement`). |
| Secrets | Windows Credential Manager via **`Meziantou.Framework.Win32.CredentialManager`** | Implementierung liegt im **App**-Projekt (Windows-only), Core kennt nur `ISecretStore`. Passwörter nie im JSON. |
| Paketquellen | repo-lokales `nuget.config` (nur nuget.org) | Auf dem Entwicklungsrechner ist global zusätzlich eine DevExpress-Quelle eingerichtet; CPM verlangt dann Source Mapping. |
| CI | vorerst keine (nur lokal) | Sobald das Hosting feststeht: Linux-Job (Core + Unit- + Integrationstests), Windows-Job (ganze Solution). |
| Distribution | `dotnet publish src/FerretSharp.App -c Release -r win-x64 --self-contained -o <ziel>` | Ordner-Deployment (kein Single-File). Installer/Auto-Update (Velopack) = Backlog. |

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
│     ├─ Services/                     # WindowTheme, ThemeService, DialogService, FileSaveService, CredentialManagerSecretStore
│     ├─ Assets/ferretsharp.ico        # App-Icon (generiert, nicht von Hand bearbeiten)
│     ├─ wwwroot/index.html            # Host-Page, bindet _content/FerretSharp.UI/… ein
│     └─ App.xaml                      # Generic Host, Serilog, Exception-Handler
├─ tests/
│  ├─ FerretSharp.Core.Tests/          # schnell, ohne DB
│  └─ FerretSharp.Integration.Tests/   # Testcontainers, überspringt sauber, wenn kein Docker verfügbar
├─ tools/icon/New-AppIcon.ps1          # erzeugt das App-Icon: „FS“, F dunkel/S blau, kantige Buchstaben (eigene Formen, keine Schrift) auf runder heller Kachel; .ico mit 16–256 px
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
    bool ReadOnly,                // bei Prod default true; wirkt ab v2 (in v1 ist alles read-only)
    string? Group = null);        // optional, z. B. Projekt/Kunde ("ERP", "Kasse") – Gruppierung in Umschalter und Übersicht
```
- Suche und Gruppierung: `ConnectionSearch` (alle Suchbegriffe müssen in Name, Gruppe, Adresse, Benutzer oder Art vorkommen; Gruppen alphabetisch, „Ohne Gruppe“ zuletzt; innerhalb einer Gruppe Dev → Test → Prod → Sonstige).
- Passwort separat über `ISecretStore.Get(profileId)` / `Set(...)`.
- `TnsAlias` braucht den Ort der `tnsnames.ora`: Der Managed Driver liest **nicht** die Registry. Reihenfolge: `tnsAdminPath` im Profil → Umgebungsvariable `TNS_ADMIN` → Fehlermeldung mit Hinweis.
- Connection-Datei: `%APPDATA%\FerretSharp\connections.json`, optional zusätzlich pro Repo (`.ferretsharp/connections.json`, ohne Secrets).

### 5.2 Workspace
```csharp
record Workspace(Guid Id, Guid ConnectionId, string Name) {   // z. B. "Bug 3711", max. 40 Zeichen
    string Notes;                                    // Freitext (noch ohne UI)
    IReadOnlyList<TabState> Tabs; int ActiveTabIndex;
    bool IsOpen; int Order; DateTimeOffset LastActive;
}
record TabState(TableRef Table, TabMode Mode, FilterRows, AppliedFilters, Sorts, int? FirstVisibleRow);
```
- `SavedQuery` entfällt in v1 (kein SQL-Editor), siehe Backlog.
- Zur Laufzeit hält jeder offene Workspace eine **eigene** Session (eigene Connection, ab v2 eigene Transaktion), geöffnet beim ersten Datenzugriff (`WorkspaceManager.GetDataAsync`). Das Schema lädt eine separate Explorer-Session (ADR 0005).
- Connection-String mit `Pooling=false`: Die Sessions leben lange, und eine Connection mit offener Transaktion darf nie in einen Pool zurückgehen.
- Beim Öffnen `ModuleName = "FerretSharp"`, `ActionName = <Workspace-Name>`, `ClientInfo` setzen → in `V$SESSION` ist erkennbar, welcher Workspace eine Sperre hält. Umbenennen setzt ACTION neu. Werte werden nach ASCII transliteriert (Abschnitt 6).
- Persistenz unter `%APPDATA%\FerretSharp\workspaces\{id}.json` (`WorkspaceStore`, eine Datei pro Workspace, versioniert, unlesbare Dateien werden gemeldet und nicht angefasst). Tab-Änderungen speichert der `WorkspaceManager` gesammelt nach 1 s, Strukturänderungen und Trennen/Beenden sofort.
- Beim Verbinden: offene Workspaces laden, der zuletzt aktive wird aktiv; ist keiner offen, wird der zuletzt benutzte wieder geöffnet, sonst „Workspace 1“ angelegt. Der letzte offene Workspace lässt sich nicht schließen. Gelöscht werden nur geschlossene Workspaces; das Löschen einer Verbindung löscht ihre Workspaces mit.
- Tabellen, die beim Wiederherstellen nicht mehr im Schema sind (gelöscht, Synonym weg), werden still verworfen.

### 5.3 Schema
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
enum FkSource { Declared, Manual, Convention /* v3: ClrModel */ }
```
- `ForeignKeyInfo` trägt Owner (`TableRef`), damit FKs über Schemagrenzen nicht verloren gehen.
- **Synonyme** (WP-04b): `ISchemaReader.GetSynonymTargetsAsync` liefert Tabellen/Views/MViews anderer Schemas, die über private Synonyme des Schemas oder öffentliche Synonyme erreichbar sind – nur Ziele mit Zugriff (`ALL_OBJECTS`), keine Oracle-Schemas (`ALL_USERS.ORACLE_MAINTAINED`), keine DB-Links. `TableSummary` beschreibt immer das **echte Objekt**, `Synonym` und `DisplayName` den Namen, unter dem der Nutzer es kennt. `SchemaCache.Merge`: eigene Objekte vor Synonymen, privat vor öffentlich, ein Eintrag pro echtem Objekt. FKs werden für alle beteiligten Owner geladen. Synonymketten (Synonym auf Synonym) werden nicht aufgelöst (Backlog). Ist im Profil ein Schema eingetragen, zählen die privaten Synonyme dieses Schemas, nicht die des Login-Users – so löst auch Oracle mit `CURRENT_SCHEMA` auf (entschieden, bleibt so). Gemessen: 27.889 öffentliche Synonyme (davon 20.000 eigene, die Hälfte ohne Zugriff) → Verbinden 2,5 s kalt, 0,5–0,8 s warm (ohne: 0,1 s); kein Handlungsbedarf.
- View-/MView-Definition: `TableDetails.Definition` aus `ALL_VIEWS.TEXT` / `ALL_MVIEWS.QUERY` (LONG, max. 32.767 Zeichen, `DefinitionTruncated`).
- Quellen: `ALL_TABLES`, `ALL_VIEWS`, `ALL_MVIEWS`, `ALL_TAB_COLUMNS` (inkl. `IDENTITY_COLUMN`), `ALL_CONSTRAINTS` (P/U/R), `ALL_CONS_COLUMNS`.
- Tabellenliste ohne Recyclebin (`DROPPED`), Nested/Secondary Tables, IOT-Overflow-Segmente und MView-Containertabellen (die MView erscheint einmal als `MaterializedView`).
- `DATA_DEFAULT` ist `LONG` → `OracleSession` setzt `InitialLONGFetchSize` (4000).
- **Immer nach `OWNER` filtern.** `ALL_TAB_COLUMNS` ist auf großen Datenbanken langsam → Tabellenliste und alle FKs des Schemas beim Connect laden, Spalten/Keys lazy pro Tabelle (`SchemaCache`). Cachen, manuell refreshbar.
- Schema-Name aus dem Profil wird normalisiert (`OracleIdentifier.Normalize`): `erp` → `ERP`, `"Erp"` bleibt `Erp`.
- Verbindungsaufbau: `ActiveConnection` (genau eine aktive Verbindung) öffnet die Explorer-Session über `IDatabaseConnector`, lädt den `SchemaCache`, hängt die Workspaces an (`WorkspaceManager.AttachAsync`) und merkt die Nutzung in `recent.json` (`RecentConnections`). Trennen speichert die Workspaces und schließt alle Sessions. Oracle-Fehler kommen als `DatabaseException` mit ORA-Code an.

### 5.4 Filter & Query
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

**Client-Locale (bei deutschem Windows sofort relevant)**
- ODP.NET übernimmt NLS-Einstellungen aus dem Windows-Locale → `NLS_SORT=GERMAN` sortiert Ziffern hinter Buchstaben und kann keine Indizes nutzen. `OracleSession` setzt deshalb `Sort`/`Comparison` = `BINARY` (Integrationstest prüft das).
- `OracleDecimal.ToString()` formatiert mit der aktuellen Kultur („1,5“) → vor dem Parsen normalisieren (`OracleDataAccess.NormalizeDecimalSeparator`).
- SQL*Plus-Testskripte: `SET DEFINE OFF` (sonst wird `&` als Variable abgefragt) und `NLS_LANG=…AL32UTF8` für Umlaute.

**Sessions & Nebenläufigkeit**
- `OracleConnection` ist nicht thread-safe → pro Session ein `SemaphoreSlim(1,1)` (kein `lock`, wegen `await`); Queries pro Workspace laufen sequenziell.
- **ODP.NET (managed, 23.26) verliert die Session, wenn `ActionName`/`ClientInfo` Nicht-ASCII enthalten**: Der nächste Roundtrip endet mit ORA-12537, die Connection ist weg (schon ein einzelnes „Ä“ reicht; per Integrationstest gefunden). `OracleSession.ToSessionAttribute` transliteriert deshalb (ä → ae, ß → ss, é → e, – → -, sonst `?`) und kürzt auf 64 Zeichen. Gilt für jeden Wert, der in MODULE/ACTION/CLIENT_INFO landet.
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
- **Verbindungen** (es sind im Alltag 12+): oben links nur die aktive Verbindung als Umschalter. Klick oder `Ctrl+Shift+O` öffnet ein Popover mit Suche, „Zuletzt verwendet“ (ab WP-03), einklappbaren Gruppen und Pfeiltasten-/Enter-Bedienung; „⋯“ je Zeile → Bearbeiten, Duplizieren, Löschen (mit Bestätigung).
- **Startseite „Verbindungen“**: Übersicht aller Verbindungen nach Gruppen (Name, Umgebung, Adresse, Benutzer → Schema, „⋯“-Menü). Erscheint, solange keine Verbindung aktiv ist, und über „Alle Verbindungen verwalten …“ im Popover. Doppelklick verbindet (ab WP-03; bis dahin: bearbeiten).
- **Chips in der Topbar** sind die Workspaces der aktiven Verbindung (WP-05), nicht die Verbindungen.
- Tabellenliste alphabetisch (Tabellen und Views unterscheidbar), Buchstabenleiste links: Klick springt zur ersten Tabelle mit diesem Buchstaben; Buchstaben ohne Treffer ausgegraut. (Fuzzy-Suche = Backlog.)
- Prod: roter Rahmen; ab v2 Schreiben nur nach Freischalten über Toggle + Bestätigungsdialog.
- Grid-Spalten werden aus dem Schema erzeugt und als Column-Definitions an AG Grid übergeben (eigener Header-Renderer: Name, Oracle-Typ, NOT NULL, PK/FK-Badges). AG Grid fragt Blöcke à 500 Zeilen per `invokeMethodAsync` bei .NET an (Infinite Row Model); Zeilen gehen als Dictionaries mit Row-Index über die Grenze.
- Header-Klick sortiert serverseitig: AG Grid liefert das Sort-Model im Datasource-Request, .NET fragt neu ab.
- Kontextmenü (FK-Navigation, Kopieren) und Dialoge sind Blazor-Komponenten; AG Grid meldet nur das `cellContextMenu`-Event (Zeilenindex, Spalte, Mausposition), `grid.js` unterdrückt das WebView-Kontextmenü im Grid (`GridContextMenu`).
- Look & Feel und Interaktionen: siehe Prototyp (Branch `spike/blazor-hybrid`).

**Shortcuts**

| Taste | Aktion | Version |
|---|---|---|
| Ctrl+Shift+O | Verbindungs-Umschalter öffnen | v1 |
| Ctrl+Enter | Filter anwenden | v1 |
| F5 | Refresh (v2 in Read-only-Tx: neue Transaktion) | v1 |
| Ctrl+P | Tabelle suchen (Backlog) | – |
| Ctrl+S | Pending-Änderungen flushen (kein Commit) | v2 |
| Ctrl+Shift+Enter | Commit (auf Prod immer mit Bestätigung) | v2 |
| – | Rollback nur über Button, mit Bestätigung | v2 |

`Esc` bleibt dem Grid vorbehalten (Zelleingabe abbrechen) bzw. schließt Menüs/Dialoge. Globale Shortcuts registriert die `Shell` über `wwwroot/js/shortcuts.js` (Capture-Listener → `OnShortcut` in .NET); `F12` öffnet im Debug-Build die DevTools.

UI-Muster: Dialoge und Bestätigungen fordern Komponenten über den kaskadierten `ShellState` an. Komponenten ohne Parameter rendern bei Parent-Updates **nicht** neu → sie abonnieren `ShellState.Changed` bzw. `ConnectionManager.Changed`/`WorkspaceManager.Changed` selbst. Tab-Zustand, der kein Neurendern braucht (Tippen im Filter, Scrollen, Sortieren), meldet `ShellState.MarkDirty()`; die Shell reicht dann die Tabs aller offenen Workspaces an `WorkspaceManager.UpdateTabs` weiter (speichert nur bei Änderung).

Blazor kennt **kein `auxclick`-Event**: `@onauxclick` wird kommentarlos als HTML-Attribut ausgegeben und tut nichts (so war Mittelklick-Schließen der Tabs seit WP-04 wirkungslos). Mittelklick über `@onmouseup` mit `e.Button == 1`.

**Kein `@ondblclick` verwenden.** Der WPF-Host (`WebView2CompositionControl`) reicht beim zweiten Klick eines echten Doppelklicks das Mouse-down doppelt an die WebView weiter: Chromium zählt `detail` 1 → 2 → 3, der zweite `click` kommt mit `detail=3`, und `dblclick` feuert nie. Doppelklick deshalb über `@onclick` mit `e.Detail >= 2` erkennen (Workspace-Chip, Verbindungszeile). Fiel lange nicht auf, weil die E2E-Tests Doppelklicks synthetisch bzw. per CDP direkt in die WebView schickten, also am Host vorbei. Gefunden durch den Nutzer in v1.1.0.

**Mausinteraktionen mit echter Windows-Eingabe prüfen** (`SendInput` über `realclick.ps1` im Scratchpad: `ClientToScreen` des Fensters + CSS-Position × `devicePixelRatio`), nicht nur per CDP. Nur so läuft die Eingabe durch den WPF-Host wie beim Nutzer. Dabei immer nur **eine** App-Instanz mit Debug-Port starten: Zwei Instanzen teilen sich das WebView2-Datenverzeichnis, und unterschiedliche Browser-Argumente (z. B. zwei Debug-Ports) lassen die zweite beim Start mit `0x8007139F` abstürzen.

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
- Explorer-Panel: Tabellenliste alphabetisch (Suchfeld mit einfachem Contains-Filter) + `LetterIndexBar`-Komponente (A–Z, `#` für Namen, die nicht mit A–Z beginnen).
- Zusätzlich umgesetzt: Strukturansicht der gewählten Tabelle (Spalten, PK/UK, Row-Key, ein-/ausgehende FKs mit Sprung), Verbinden/Trennen mit Fehleransicht (ORA-Code, „Erneut versuchen“), „Zuletzt verwendet“, roter Rahmen bei aktiver Prod-Verbindung.
- **Fertig wenn:** Nach dem Connect erscheinen alle Tabellen/Views, die Buchstabenleiste springt korrekt.

#### WP-04 Grid, Paging & Filter (read-only)
- Läuft zunächst auf einer Default-Session pro Verbindung (Workspaces folgen in WP-05).
- `QueryBuilder` mit vollständigen Tests (alle Operatoren, Typ-Einschränkungen, LIKE-Escaping, IN-Chunking, DATE-Bereich, deterministische Sortierung, Paging, Quoting, Bind-Variablen).
- `RowKey`-Ermittlung (PK → ROWID → None).
- AG Grid einführen: Nutzer fragen, dann Version 34.x lokal nach `FerretSharp.UI/wwwroot/lib/ag-grid/` legen (inkl. Lizenzdatei), in `index.html` einbinden.
- `FerretGrid`-Komponente (AG Grid, Infinite Row Model): Spalten aus dem Schema, serverseitiges Sortieren per Header-Klick, NULL-Darstellung, Typ-Formatierung (DATE, TIMESTAMP, NUMBER ohne Präzisionsverlust – große Zahlen als String übertragen), LOB-Vorschau, Fallback-Darstellung für unbekannte Typen.
- `FilterBar`-Komponente im TablePlus-Stil: Zeilen hinzufügen/entfernen, Operatoren typabhängig, Validierung je Typ, `Apply`, `Apply All`, `Clear`; der `SQL`-Button zeigt das vom `QueryBuilder` erzeugte Statement mit Bind-Variablen (Highlighter-Komponente).
- Paging („nächste 500 Zeilen“ / Endlos-Nachladen) und Zeilenzähler (`COUNT(*)` lazy, abbrechbar).
- Zusätzlich umgesetzt: Tabellen als Tabs (Klick im Explorer öffnet/aktiviert, Mittelklick schließt), Umschalter „Daten | Struktur“ je Tab, Tabs bleiben gemountet (Grid-Zustand bleibt beim Wechsel erhalten), F5 = Neu laden statt WebView-Reload, Filter-Zeilen einzeln aktivierbar.
- **Fertig wenn:** Eine Tabelle mit > 100k Zeilen bleibt flüssig; die Filter erzeugen korrektes SQL (Tests); Seiten überlappen nicht und haben keine Lücken (Integrationstest).

#### WP-04b Views & Synonyme (eingeschoben)
- View-/MView-Definition in der Strukturansicht (mit Syntax-Hervorhebung, `SqlCode`-Komponente).
- Synonyme im Explorer (Tag „SYN“, Tooltip mit Ziel), Daten/Struktur/FKs über das echte Objekt; Integrationstest legt als SYSTEM ein zweites Schema mit privaten und öffentlichen Synonymen an.
- **Fertig wenn:** Objekte, die der Nutzer nur über Synonyme erreicht, erscheinen im Explorer und lassen sich wie eigene Tabellen/Views öffnen.

#### WP-05 Workspaces
- `Workspace`, `WorkspaceStore` (JSON), `TabState` (Tabelle, Filter, Sort, grobe Scrollposition = erste sichtbare Zeile, nur wenn bereits geladen).
- UI: Workspace-Leiste pro Verbindung, Workspace anlegen/umbenennen/schließen; jeder Workspace hat eine eigene `OracleSession` (ActionName = Workspace-Name).
- Tabellen-Tabs im Dokument-Bereich gehören zum aktiven Workspace.
- Zustand wird beim Schließen gespeichert und beim Öffnen wiederhergestellt.
- Umgesetzt: Workspace-Chips in der Topbar (Klick aktiviert, Doppelklick benennt um, ✕/Mittelklick schließt; seit v1.2 zusätzlich „⋯“-Menü mit Umbenennen/Schließen/Neuer Workspace, Umbenennen markiert den ganzen Namen), Menü „+“ mit „Neuer Workspace“ und den geschlossenen Workspaces (wieder öffnen, löschen mit Bestätigung), Statusleiste mit aktivem Workspace und Speicher-/Ladefehlern. Gespeichert werden auch noch nicht angewendete Filterzeilen; die Scrollposition wird exakt wiederhergestellt (zweistufig im Infinite Row Model). Wiederhergestellte Tabs mounten erst beim ersten Anzeigen.
- Nebenbei behoben: Mittelklick auf Tabs (WP-04) war wirkungslos (`auxclick`, Abschnitt 7); Nicht-ASCII in ACTION/CLIENT_INFO zerstört die Session (Abschnitt 6).
- **Fertig wenn:** Zwei Workspaces auf derselben Verbindung öffnen dieselbe Tabelle mit unterschiedlichen Filtern; nach einem App-Neustart sind beide inkl. Filter wieder da. → erfüllt (Integrationstest + E2E-Prüfung, `V$SESSION` zeigt eine Session je Workspace plus Explorer).

#### WP-06 FK-Navigation
- **Abweichung vom ursprünglichen Plan (Entscheidung des Nutzers):** nur deklarierte FKs aus dem `SchemaCache`. Virtuelle FKs (inkl. Dialog) und Vorschläge per Namenskonvention sind in den Backlog verschoben; `FkSource.Manual`/`Convention` bleiben im Enum, werden aber noch nicht erzeugt.
- Kontextmenü auf Zelle/Zeile: **ausgehend** („verweist auf KUNDEN · ID = 4711“) und **eingehend** („referenziert von AUFTRAG · KUNDE_ID = 4711 (12)“ → Counts lazy, nacheinander auf der Workspace-Session, Timeout 5 s, Abbruch beim Schließen des Menüs). Composite FKs unterstützt. Dazu „Wert kopieren“.
- **Ein Sprung öffnet immer einen neuen Tab** (rechts neben dem aktuellen, mit Filterzeilen und angewendet), auch wenn die Tabelle schon offen ist – der Ausgangs-Tab behält seine Filter (Entscheidung des Nutzers). Dieselbe Tabelle kann damit mehrfach offen sein; der Tab-Tooltip zeigt die Filter. Klick im Explorer aktiviert weiterhin den ersten Tab der Tabelle.
- Umsetzung: `FkNavigation` (Core/Query) baut aus den **Rohwerten** der Zeile Gleichheitsfilter, die exakt zurückgelesen werden (invariante Zahlen, ISO-Datum, Hex für RAW) – nie aus dem deutschen Anzeigetext („1.234“ wäre 1,234). NULL-Schlüssel und Typen ohne exakte Gleichheit (LOB, BINARY_FLOAT/DOUBLE, NUMBER > 28 Stellen, Intervalle, TIMESTAMP WITH TIME ZONE) machen den Sprung unmöglich und werden im Menü begründet. `FerretGrid` hält die Rohwerte der geladenen Blöcke (max. 40 wie AG Grid). `FkNavigation.CountAsync` liefert nach dem Timeout sofort `null` und bricht das Statement ab.
- Nebenbei: RAW-Spalten lassen sich jetzt per Hex-Wert filtern (`=`, `≠`, `in`), sonst wären FKs über `RAW(16)` (GUIDs, wie EF Core sie ablegt) nicht navigierbar.
- **Fertig wenn:** Ein Sprung legt automatisch eine Filterzeile im Ziel-Tab an. → erfüllt (Unit-/Integrationstests inkl. Composite- und RAW-FK, E2E mit echter Maus).

#### WP-07 Export & Politur v1 → Release v1.0.0
- Keyboard-Shortcuts v1 (siehe Abschnitt 7).
- Export für markierte Zeilen: CSV und INSERT-Statements (nur als Text, wird nie ausgeführt).
- Fehlerdialoge mit Oracle-Fehlercode + Statement (Bind-Werte bei Prod maskiert).
- Verbindungsabbruch-Erkennung mit Reconnect-Angebot.
- `CHANGELOG.md`, Tag `v1.0.0`, self-contained Publish.
- **Fertig wenn:** Der Nutzer kann DBeaver für das reine Lesen/Navigieren ersetzen.
- Umgesetzt:
  - Shortcuts v1 waren bereits vollständig (Ctrl+Shift+O, Ctrl+Enter, F5).
  - Export: Mehrfachauswahl im Grid (Ctrl/Shift, nur geladene Zeilen), im Kontextmenü „Als Tabelle kopieren“ (Tab-getrennt, für Excel), „Als INSERT kopieren“, „Als CSV speichern …“ (`;`, UTF-8 mit BOM), „Als INSERT-Skript speichern …“ (nativer Dialog über `IFileSaveService`, Implementierung im App-Projekt). `DelimitedExport` (Data/) und `InsertExport` (Query/, weil SQL-Text) mit exakten Oracle-Literalen; ein Integrationstest führt das Skript gegen eine Tabellenkopie aus und vergleicht alle Werte. LOBs, die nur als Vorschau geladen sind, werden als NULL mit Kommentar exportiert und gemeldet (vollständiger LOB-Export = Backlog).
  - Fehler: `DatabaseException` trägt das fehlgeschlagene Statement (`OracleSession` wirft intern `OracleStatementException`, `OracleErrors.Translate` übersetzt). `ErrorDialog` und Log (`QueryErrorLog`) zeigen Binds über `BindValues`, bei Prod maskiert.
  - Verbindungsabbruch: `DatabaseException.IsConnectionLost` (ORA-00028/00603/01012/01089/01092/02396/03113/03114/03135/12537/12547/12570/12571 sowie bereits geschlossene Connection). Jede DB-Fehlerstelle der UI ruft `ShellState.ReportFailure`; Banner und Statusleiste bieten „Neu verbinden“ (= normaler Connect, Workspaces/Tabs kommen aus dem gespeicherten Zustand). Erkennung reaktiv beim nächsten Statement, kein Keep-alive-Ping. Integrationstest killt die Session als SYSTEM (ergibt ORA-03135).
  - Release: Version 1.0.0 (`Directory.Build.props`), Publish: `dotnet publish src/FerretSharp.App -c Release -r win-x64 --self-contained -o <ziel>` (~180 MB, Ordner statt Single-File wegen WebView2/WPF-Nativbibliotheken).

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
- Workspaces: Standardname ist „Workspace N“ mit der kleinsten freien Nummer (nach Umbenennen von „Workspace 1“ heißt der nächste wieder „Workspace 1“). Shortcuts zum Wechseln (z. B. Ctrl+1…9) und eine Oberfläche für die Notizen fehlen noch.

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
- E2E: Rechtsklick und Mittelklick mit `Input.dispatchMouseEvent` (echte Maus), die Zwischenablage über `Get-Clipboard` prüfen. **Kein `navigator.clipboard.readText()`** im WebView aufrufen: Es öffnet eine Berechtigungsabfrage, die als zusätzliches CDP-Target (`edge://permission-request-dialog/`) vor der App-Seite in `/json` steht; das CDP-Skript wählt deshalb das Target mit der URL `https://0.0.0.1/`.
- **Native Dialoge (Speichern unter) nicht per UI Automation bedienen**: Sie öffnen sich in den echten Ordnern des Nutzers (Dokumente, OneDrive). Ein Fehlgriff traf dort einen Ordner-Eintrag statt des Dateinamenfelds, und der Dialog speicherte die Testdatei im Dokumente-Ordner (in WP-07 passiert, Datei wurde ins Scratchpad verschoben). Export-Inhalte über die Zwischenablage prüfen; den Speichern-Pfad höchstens bis zum Öffnen des Dialogs testen.
- Statements per PowerShell-Pipe an `docker exec … sqlplus` bekommen ein BOM vorangestellt (SP2-0734) → `docker exec <name> bash -c "echo '…' | sqlplus …"` oder Skriptdatei per `docker cp`.
- Farbige Button-Varianten (`.btn.primary`, `.btn.danger-solid`) müssen im `:hover` ihren Hintergrund selbst setzen: `.btn:hover:not(:disabled)` setzt `--hover` und gewinnt sonst (Fehler in v1.0.0: Text beim Hover unlesbar). Text auf farbigen Flächen immer über Tokens (`--accent-text`, `--danger-text`), die im Dark Mode dunkel sind. Prüfen mit echtem Hover (`Input.dispatchMouseEvent` mouseMoved) und berechnetem Kontrast, nicht nur per Screenshot.
- CSS-Klassennamen in Komponenten nicht mit globalen Klassen kollidieren lassen (`.empty` ist der Leerzustand mit `position: absolute; inset: 0` – so überdeckte in WP-06 ein Menüeintrag das ganze Kontextmenü).
- E2E-Fallen: Nach einem Klick auf einen Tab ist `.page.active` noch kurz die alte Seite → auf etwas Spezifisches des Ziels warten. Synthetische Events erreichen Blazor, ersetzen aber keinen Test mit echter Eingabe (`Input.dispatchMouseEvent`/`dispatchKeyEvent`), sonst bleiben Fehler wie das fehlende `auxclick` unentdeckt. Im Infinite Row Model kennt das Grid anfangs nur ~501 Zeilen; `scrollTop` weiter unten wird abgeschnitten.
