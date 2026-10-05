# FerretSharp – Oracle explorer for .NET developers

> Projektanweisungen für Claude Code. Bitte vollständig lesen, bevor ein Arbeitspaket umgesetzt wird.
> Arbeitssprache mit dem Nutzer: **Deutsch**. Code, Kommentare und Commit-Messages: **Englisch**.
> Stand: 2026-10-05 (v1 bis 1.7; v2: 1.8–2.0; v3: WP-11 → 2.1.0, Fixes 2.1.1/2.1.2; WP-12 → 2.2.0, Enum-Anzeigenamen 2.2.1; WP-13 → 2.3.0; WP-14 → 2.4.0)

## 1. Ziel

Ein eigener Datenbank-Editor für **Oracle**, der stärker auf den eigenen Arbeitsablauf zugeschnitten ist als DBeaver.
Kernideen, die das Tool von DBeaver abheben:

- **Workspaces** (Unter-Sessions) pro Verbindung, jeder mit eigener Oracle-Connection (und ab v2 eigener Transaktion) → zwei Workspaces können dieselbe Tabelle unabhängig voneinander ansehen und später bearbeiten.
- **FK-Navigation** im Grid: von einer Zeile zu referenzierten/referenzierenden Zeilen springen (v1: deklarierte FKs; virtuelle FKs für Schemas ohne deklarierte Constraints stehen im Backlog).
- **Zusammenbaubare Filter** (TablePlus-Stil): Liste aus (Spalte, Operator, Wert), daraus wird das WHERE generiert.
- **Sandbox-Editing** (v2): alle Änderungen laufen in einer Transaktion, Commit/Rollback explizit.
- Prod-Verbindungen sind visuell markiert und standardmäßig read-only.

**Langfristiges Ziel (v3): Brücke zur C#-/EF-Core-Welt.** FerretSharp kennt das Datenmodell des eigenen .NET-Projekts (DbContext): Entity- und Property-Namen statt nur Tabellen/Spalten, Enums mit C#-Namen, Navigation Properties als Beziehungen, ausführbare LINQ-Queries mit Anzeige des generierten SQL, Code-Generierung aus Daten. v1 und v2 sind der Unterbau dafür – Architekturentscheidungen dort müssen v3 mitdenken (siehe Abschnitt 2).

Der Nutzer ist erfahrener .NET-Entwickler (Visual Studio, Blazor, SignalR). Erklärungen auf Senior-Niveau, keine Grundlagen.

## 2. Versionen

Es wird in Versionen ausgeliefert. Jede Version ist für sich benutzbar.

| Version | Inhalt | Arbeitspakete |
|---|---|---|
| **v1 – Read-only Browser** | Connections, Schema, Grid, Filter, Workspaces, FK-Navigation, Export | WP-01 … WP-07 |
| **v2 – Sandbox-Editing** | Transaktionsmodell, Editieren, Commit/Rollback, Lock-Handling, Prod-Freischaltung | WP-08 … WP-10 |
| **v3 – .NET-Integration** | DbContext-Modell laden, Schema-Anreicherung (Entities, Enums, Navigations), LINQ-Konsole, Explain-Plan (Grid und LINQ), Code-Generierung | WP-11 … WP-15 |
| **v4+** | Backlog (Abschnitt 10) | – |

Regeln für **v1**:
- Es gibt **keinen** Codepfad, der DML/DDL erzeugt oder ausführt. `IDataAccess` und `OracleSession` bieten in v1 nur lesende Methoden (kein öffentliches `ExecuteNonQuery`). Einzige Ausnahme: INSERT-Statements als **Text-Export** (werden nie ausgeführt).
- Abgesichert durch: `OracleSession.ExecuteReaderAsync` lehnt alles außer reinen Abfragen ab (`IsReadOnlyStatement`: beginnt nach Leerraum/Kommentaren mit `SELECT`/`WITH`, kein `FOR UPDATE`, nur ein Statement) – eine Stolperfalle gegen Programmierfehler, kein SQL-Parser. `ReadOnlyTests` prüfen per Reflection, dass die DB-Typen keine schreibenden Methoden anbieten, und dass alle Statements von `QueryBuilder` und `OracleSchemaReader` die Sperre passieren; ein Integrationstest zeigt, dass `DELETE` abgewiesen wird. In v2 muss die Sperre für die Daten-Session des Transaktions-APIs bewusst umgangen werden (eigene Methode, nicht die Sperre aufweichen).
- **Seit WP-08 (ADR 0006)** gibt es diesen zweiten Weg: `OracleSession.ExecuteNonQueryAsync` ist `internal`, nimmt nur ein einzelnes INSERT/UPDATE/DELETE (`IsWriteStatement`, nie DDL) und nur innerhalb einer offenen Transaktion. Bis WP-09 ruft ihn kein Produktivcode auf. Transaktionssteuerung (Begin, Savepoint, Commit, Rollback) nur an `OracleSession`; `ReadOnlyTests` prüfen, dass `IDataAccess`/`ISchemaReader`/`IDatabaseConnection` weder schreibende noch transaktionssteuernde Methoden anbieten.
- Keine Garantie auf Datenbankseite: Hat der DB-User Schreibrechte, könnte ein Fehler in FerretSharp schreiben (ohne Transaktion committet ODP.NET sofort). `SET TRANSACTION READ ONLY` schützt nicht vor DDL (implizites Commit). Die einzige echte Garantie bleibt ein User mit reinen SELECT-Rechten.
- v1-Sessions laufen ohne explizite Transaktion → jede Abfrage sieht den aktuellen Commit-Stand (Statement-Level-Konsistenz).
- Trotzdem für v2 vorbauen: Row-Key immer mitselektieren (Abschnitt 5.5), eine Session pro Workspace, `TabState` erweiterbar.
- Empfehlung an den Nutzer (nicht im Code erzwingbar): für Prod einen DB-User mit reinen SELECT-Grants verwenden.

Vorbereitung auf **v3** (gilt ab WP-01):
- Schema-Records bleiben reine DB-Sicht. Zusätzliche Metadaten (Entity-/Property-Name, CLR-Typ, Enum-Mapping) kommen später über eine separate, nach (Owner, Tabelle, Spalte) adressierte **Annotation-Schicht** hinzu – nicht durch Aufbohren von `TableDetails`/`ColumnInfo`.
- Anzeige und Eingabe von Zellwerten sowie Spaltenbeschriftungen laufen über eine austauschbare Präsentationsschicht (seit WP-12 `TablePresentation`, in der UI über den `PresentationService`), damit Enum-Namen und Property-Namen eingehängt werden, ohne Grid/FilterBar umzubauen.
- FK-Quellen sind ein Enum (`Declared`, `Manual`, `Convention`, später `ClrModel`), kein `bool IsVirtual`.
- Das Filtermodell (Spalte, Operator, Werte) bleibt so einfach, dass es sich 1:1 in einen LINQ-`Where`-Ausdruck übersetzen lässt.

Versionierung: SemVer, Git-Tag `vX.Y.Z` pro Release, `CHANGELOG.md` pflegen. Features der nächsten Version werden **nicht** vorgezogen, sondern in `docs/backlog.md` notiert.

## 3. Stack (entschieden – Änderungen nur per ADR in `docs/decisions/`)

| Bereich | Entscheidung | Begründung / Hinweise |
|---|---|---|
| Runtime | **.NET 10 (LTS, Support bis Nov. 2028)**, C# 14 | .NET 8 endet am 10.11.2026. SDK per `global.json` pinnen (`rollForward: latestFeature`). |
| Solution | **`FerretSharp.slnx`** | Standardformat des .NET-10-SDK, von Visual Studio (und Rider) unterstützt, weniger Merge-Konflikte. |
| Pakete | **Central Package Management** (`Directory.Packages.props`) | Versionen an einer Stelle. |
| UI | **Blazor Hybrid**: WPF-Host mit `BlazorWebView` (`Microsoft.AspNetCore.Components.WebView.Wpf`), Komponenten in einer Razor Class Library | ADR 0004. Windows-only reicht; WebView2-Runtime ist auf Windows 10/11 vorhanden. App-TFM **`net10.0-windows10.0.19041.0`** (mit `net10.0-windows` stürzt BlazorWebView beim Start ab). |
| Theme | eigenes CSS mit Design-Tokens (`ferretsharp.css`), hell/dunkel über `prefers-color-scheme` | Auswahl System/Hell/Dunkel auf der Einstellungen-Seite (`AppSettings` in `settings.json`, `SettingsStore`). `WindowTheme` setzt Titelleiste (DWM), Fensterhintergrund und `CoreWebView2.Profile.PreferredColorScheme` – zur Laufzeit umschaltbar, CSS und Grid folgen über `prefers-color-scheme`. Die Einstellung wird vor dem Erzeugen des Fensters gelesen (kein weißes Aufblitzen). `--theme=dark\|light` übersteuert für die Sitzung (Tests). In WPF-Dateien `ThemeMode` per Alias auf `FerretSharp.Core.Settings` festlegen (`System.Windows.ThemeMode` kollidiert). **Nicht verwenden:** WPF-UI, AvalonDock, CommunityToolkit.Mvvm, AvalonEdit, CSS-Frameworks. |
| UI-Zustand | Komponenten + schlanke State-/Service-Klassen in `FerretSharp.UI` | Kein MVVM-Framework. Lange Operationen async mit `CancellationToken`. |
| JS-Interop | ein ES-Modul pro Thema in `FerretSharp.UI/wwwroot/js`, Aufruf über `IJSObjectReference` | JS bleibt dünn (Grid-Brücke, Zwischenablage, Scrollen, Fokus). **Keine Geschäftslogik in JS.** |
| Hosting/DI | `Microsoft.Extensions.Hosting` | DI, Konfiguration, Logging ab WP-01. Die BlazorWebView nutzt den Service Provider des Hosts. |
| Logging | `Microsoft.Extensions.Logging` + **Serilog** (`Serilog.Extensions.Hosting`, `Serilog.Sinks.File`) | Datei unter `%APPDATA%\FerretSharp\logs`. Keine Bind-Werte von Prod-Verbindungen loggen (maskieren). |
| Grid | **AG Grid Community 34.3.1** (MIT) über JS-Interop, **Infinite Row Model** | Datenblöcke à 500 und Sortierung kommen aus .NET (`IDataAccess`), gekapselt in `FerretGrid` + `wwwroot/js/grid.js`. Zellen gehen als fertig formatierte Strings über die Grenze (null = NULL), Spalten-IDs `c0`, `c1` … (Oracle-Namen dürfen Punkte enthalten). **Lokal im Repo** unter `FerretSharp.UI/wwwroot/lib/ag-grid/` (Herkunft/Hash in der README dort, kein CDN). Enterprise-Features (Kontextmenü, Zellbereich) nicht verwenden – eigene Lösungen in Blazor. |
| Layout | Tabs + Seitenleiste in Blazor | Kein Docking-Framework. |
| SQL-Anzeige | eigener Highlighter in Razor (siehe Prototyp `TableView.razor`) | Editor für C# in der LINQ-Konsole: **Monaco 0.57** (ADR 0011, lokal unter `wwwroot/lib/monaco/`); für den freien SQL-Editor (Backlog) wiederverwendbar. |
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
record TabState(TableRef Table, TabMode Mode, FilterRows, AppliedFilters, Sorts, int? FirstVisibleRow) { PinnedColumns, OriginTab }
```
- `PinnedColumns`: vom Nutzer angeheftete Spalten in Anheft-Reihenfolge, **pro Tab** (Entscheidung des Nutzers; ein FK-Sprung öffnet den neuen Tab ohne Pins). Der PK ist immer angeheftet und steht nicht in der Liste. Logik in `ColumnPinning` (Workspaces/): PK in Schema-Reihenfolge → angeheftete Spalten → Rest; gelöschte Spalten fallen beim Öffnen weg.
- `OriginTab` (v1.6): Index des Tabs, aus dem ein FK-Sprung kam, für „Zurück“ (Alt+←). Zur Laufzeit hält `TableTab.Origin` die Referenz; ist der Ursprung geschlossen, führt „Zurück“ zum nächsten offenen Tab weiter hinten in der Sprungkette (`BackTarget`). „Vor“ (Alt+→, `TableTab.Forward`) wird nicht gespeichert; ein neuer Sprung aus einem Tab löscht dessen „Vor“ (wie im Browser). Mehrere Tabs derselben Tabelle zeigen im Titel die Kurzform ihrer Filter (`FilterSummary`).
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
enum FkSource { Declared, Manual, Convention, ClrModel }   // ClrModel: Navigations des C#-Modells ohne Constraint (WP-12)
```
- `ForeignKeyInfo` trägt Owner (`TableRef`), damit FKs über Schemagrenzen nicht verloren gehen.
- **Synonyme** (WP-04b): `ISchemaReader.GetSynonymTargetsAsync` liefert Tabellen/Views/MViews anderer Schemas, die über private Synonyme des Schemas oder öffentliche Synonyme erreichbar sind – nur Ziele mit Zugriff (`ALL_OBJECTS`), keine Oracle-Schemas (`ALL_USERS.ORACLE_MAINTAINED`), keine DB-Links. `TableSummary` beschreibt immer das **echte Objekt**, `Synonym` und `DisplayName` den Namen, unter dem der Nutzer es kennt. `SchemaCache.Merge`: eigene Objekte vor Synonymen, privat vor öffentlich, ein Eintrag pro echtem Objekt. FKs werden für alle beteiligten Owner geladen. Synonymketten (Synonym auf Synonym) werden nicht aufgelöst (Backlog). Ist im Profil ein Schema eingetragen, zählen die privaten Synonyme dieses Schemas, nicht die des Login-Users – so löst auch Oracle mit `CURRENT_SCHEMA` auf (entschieden, bleibt so). Gemessen: 27.889 öffentliche Synonyme (davon 20.000 eigene, die Hälfte ohne Zugriff) → Verbinden 2,5 s kalt, 0,5–0,8 s warm (ohne: 0,1 s); kein Handlungsbedarf.
- View-/MView-Definition: `TableDetails.Definition` aus `ALL_VIEWS.TEXT` / `ALL_MVIEWS.QUERY` (LONG, max. 32.767 Zeichen, `DefinitionTruncated`).
- Objekt-Details (v1.7, `Schema/ObjectDetails.cs`), je eine `ISchemaReader`-Methode, nicht im `SchemaCache` gecacht (die Ansicht im Tab hält ihre Daten bis F5): `GetObjectInfoAsync` (`ALL_OBJECTS` + `ALL_TABLES` + `ALL_TAB_COMMENTS`/`ALL_MVIEW_COMMENTS`), `GetConstraintsAsync` (inkl. Check-Bedingung als LONG; die systemgenerierten NOT-NULL-Checks erkennt `ConstraintInfo.IsColumnNotNull` – auch `DEFAULT ON NULL` erzeugt einen), `GetIndexesAsync` (ohne LOB-Indizes; DESC-Spalten stehen in `ALL_IND_EXPRESSIONS` als `"SPALTE"` und werden zu normalen Spalten), `GetDependenciesAsync` (`ALL_DEPENDENCIES` beidseitig, ohne `SYS.STANDARD`/`DBMS_STANDARD`), `GetDdlAsync` (`SELECT DBMS_METADATA.GET_DDL(…) FROM DUAL` – passiert die Lesesperre; fremde Schemas brauchen `SELECT_CATALOG_ROLE`, sonst ORA-31603). `IndexAdvice.UnindexedForeignKeys` findet FKs ohne Index mit ihren Spalten vorne. Spalten kommen jetzt aus `ALL_TAB_COLS` mit `HIDDEN_COLUMN = 'NO'` (= `ALL_TAB_COLUMNS` plus `VIRTUAL_COLUMN`), dazu Kommentar und `DEFAULT_ON_NULL`. `TableSummary.IsInvalid` für Views/MViews mit Status INVALID (eine ungültige MView lässt sich weiter abfragen, eine View wird beim Zugriff neu kompiliert – Texte in `InvalidStatusText`).
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
  - vor jedem Update/Delete `SELECT … FOR UPDATE WAIT n` (n konfigurierbar, Default 3 s) → `ORA-30006` (Oracle 23: `ORA-00054`) statt endlosem Warten
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
- Verbindungsabbruch (Idle-Timeout, Firewall, `IDLE_TIME`-Profil) erkennen und laut melden. Seit v1.6 pingt `ConnectionKeepAlive` alle 2 min die Sessions, die mindestens 1 min ruhten (laufende werden übersprungen, Ping-Timeout 30 s), und meldet einen Abbruch sofort über das Banner. Abschaltbar in den Einstellungen (`AppSettings.KeepAlive`; alle Einstellungen laufen über `AppSettingsService`, damit sich Änderungen nicht gegenseitig überschreiben). Nebenwirkung, vom Nutzer so entschieden: Die Sessions bleiben auch gegen ein `IDLE_TIME`-Limit offen – in v2 bei offenen Transaktionen neu bewerten. In v2 gehen dabei uncommittete Änderungen verloren → das muss der Nutzer klar sehen.

**Transaktionen (v2)**
- Uncommittetes Update in Workspace A blockiert ein Update derselben Zeile in B. Ein normales `UPDATE` wartet unbegrenzt; `ORA-00054` gibt es nur bei `NOWAIT`. Deshalb `FOR UPDATE WAIT n` (→ `ORA-30006`, in Oracle 23 `ORA-00054`) und Dialog mit dem sperrenden Workspace bzw. der Session (`V$SESSION`, falls Rechte vorhanden).
- Lange offene Transaktionen halten Locks und Undo. Die Statusleiste zeigt „Tx offen seit X min · N Zeilen gesperrt“.
- Prod im gesperrten Zustand: `SET TRANSACTION READ ONLY` (Oracle erzwingt das selbst). Achtung, Snapshot-Semantik: Alle Abfragen sehen den Stand vom Transaktionsbeginn. **Umgesetzt (WP-08, ADR 0006, Entscheidung des Nutzers):** Jede neue Abfrage (erste Seite: Tab öffnen, Filter, Sortierung, F5) startet einen neuen Snapshot, weitere Seiten bleiben darin. Nach Rollback wird neu gesetzt. Bei ORA-01555, ORA-01466 (DDL nach Snapshot-Beginn, auch noch ~1 s danach) und ORA-08176 (erstes Segment einer Tabelle mit verzögerter Segment-Erzeugung) automatisch neu starten und wiederholen.
- `SET TRANSACTION READ ONLY` nur innerhalb einer ODP.NET-Transaktion (`BeginTransaction`) – ohne sie committet ODP.NET sofort und der Schutz ist weg. DDL läuft auch in einer Read-only-Transaktion (implizites Commit). Belegt in `TransactionBehaviorTests`.
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
- Prod: roter Rahmen; Profile mit „Schreibgeschützt“ (Prod voreingestellt) schreiben erst nach dem Freischalten eines Workspaces (Badge, Statusleiste oder „⋯“-Menü; auf Prod mit eingetipptem Verbindungsnamen, seit 2.0).
- Grid-Spalten werden aus dem Schema erzeugt und als Column-Definitions an AG Grid übergeben (eigener Header-Renderer: Name, Oracle-Typ, NOT NULL, PK/FK-Badges). AG Grid fragt Blöcke à 500 Zeilen per `invokeMethodAsync` bei .NET an (Infinite Row Model); Zeilen gehen als Dictionaries mit Row-Index über die Grenze.
- Header-Klick sortiert serverseitig: AG Grid liefert das Sort-Model im Datasource-Request, .NET fragt neu ab.
- Spalten anheften (v1.4): Rechtsklick auf den Header öffnet `ColumnMenu` (Blazor; `grid.js` liest die Spalte aus `col-id` der `.ag-header-cell`, weil AG Grids `columnHeaderContextMenu` keine Mausposition liefert), derselbe Eintrag steht im Zellen-Kontextmenü. Ziehen in den angehefteten Bereich meldet `grid.js` per `OnPinnedChanged` (nur UI-Quellen, nicht `api`). Der PK hat `lockPinned` + `lockPosition: 'left'`. Spalten-IDs bleiben `c<Schema-Index>`, nur die Reihenfolge der Column-Defs ändert sich.
- Spaltensuche (v1.5, Entscheidung des Nutzers: nur Springen, kein Ausblenden; Suche nur nach Namen): `ColumnSearch` (Core/Query) – alle Wörter müssen vorkommen, Unterstriche optional, Reihenfolge exakt → Präfix → Wortanfang → enthält, je Gruppe Schema-Reihenfolge. `ColumnList` zeigt die Treffer, die Eingabe gehört der Eltern-Komponente: `ColumnPicker` (Spaltenwahl der Filterleiste statt `<select>`; Klick in die Liste per `mousedown` mit `preventDefault`, sonst schließt das `blur` die Liste vor dem Klick) und `ColumnJump` (Ctrl+F). Pfeiltasten wie im Verbindungs-Umschalter ohne JS-Abfangen.
- Native `<select>`, deren Optionen sich ändern (Operator je Spaltentyp), brauchen `@key` auf die Optionsmenge: Bleibt der Wert gleich, setzt Blazor ihn nicht neu, und der Browser zeigt die erste Option (in v1.5 gefunden).
- Kontextmenü (FK-Navigation, Kopieren) und Dialoge sind Blazor-Komponenten; AG Grid meldet nur das `cellContextMenu`-Event (Zeilenindex, Spalte, Mausposition), `grid.js` unterdrückt das WebView-Kontextmenü im Grid (`GridContextMenu`).
- Ansichten eines Tabs (v1.7, flach nebeneinander, Entscheidung des Nutzers): Daten | Spalten | Constraints | Indizes | Abhängigkeiten | DDL (`TabMode`, gespeichert). Über allen Nicht-Daten-Ansichten steht `ObjectHeader` (Kommentar, Status, Daten, Statistik). Jede Ansicht mountet beim ersten Öffnen und bleibt dann (wie das Grid); die Detailansichten erben von `DetailViewBase<T>` (Laden auf der Explorer-Session, Abbruch, Fehler, Neuladen über `Version` = F5). Ab etwa zehn Einträgen die seltenen unter „Mehr ▾“ zusammenfassen.
- Look & Feel und Interaktionen: siehe Prototyp (Branch `spike/blazor-hybrid`).

**Shortcuts**

| Taste | Aktion | Version |
|---|---|---|
| Ctrl+Shift+O | Verbindungs-Umschalter öffnen | v1 |
| Ctrl+Enter | Filter anwenden | v1 |
| Ctrl+C | Im Grid: Wert der fokussierten Zelle; bei mehreren markierten Zeilen diese als Tabelle (Tab-getrennt, mit Kopfzeile). Mit der Maus markierter Text innerhalb einer Zelle wird normal kopiert. Kopiert wird immer der volle Wert (`DelimitedExport.CellText`), nicht der gekürzte Anzeigetext. | v1.3 |
| F5 | Refresh (v2 in Read-only-Tx: neue Transaktion) | v1 |
| Ctrl+F | Datenansicht: Spalte suchen und hinspringen (Scrollen, Hervorheben, Fokus auf die Zelle der ersten sichtbaren Zeile); Strukturansicht: Spalten filtern (v1.6) | v1.5 |
| Alt+← / Alt+→ | Zurück zum Tab, aus dem ein FK-Sprung kam / wieder vor (verhindert nebenbei die Zurück-Navigation der WebView) | v1.6 |
| Ctrl+P | Tabelle suchen (Backlog) | – |
| Ctrl+S | Pending-Änderungen flushen (kein Commit) | v1.9 |
| Ctrl+Shift+Enter | Commit (auf Prod immer mit Bestätigung) | v1.9 |
| Ctrl+Shift+L | Neue LINQ-Konsole (mit verknüpftem C#-Projekt); im LINQ-Tab führen Ctrl+Enter und F5 aus, Ctrl+F sucht im Editor | 2.3 |
| – | Rollback nur über Button, mit Bestätigung | v2 |

`Esc` bleibt dem Grid vorbehalten (Zelleingabe abbrechen) bzw. schließt Menüs/Dialoge. Globale Shortcuts registriert die `Shell` über `wwwroot/js/shortcuts.js` (Capture-Listener → `OnShortcut` in .NET); `F12` öffnet im Debug-Build die DevTools.

UI-Muster: Dialoge und Bestätigungen fordern Komponenten über den kaskadierten `ShellState` an. Komponenten ohne Parameter rendern bei Parent-Updates **nicht** neu → sie abonnieren `ShellState.Changed` bzw. `ConnectionManager.Changed`/`WorkspaceManager.Changed` selbst. Tab-Zustand, der kein Neurendern braucht (Tippen im Filter, Scrollen, Sortieren), meldet `ShellState.MarkDirty()`; die Shell reicht dann die Tabs aller offenen Workspaces an `WorkspaceManager.UpdateTabs` weiter (speichert nur bei Änderung).

Blazor kennt **kein `auxclick`-Event**: `@onauxclick` wird kommentarlos als HTML-Attribut ausgegeben und tut nichts (so war Mittelklick-Schließen der Tabs seit WP-04 wirkungslos). Mittelklick über `@onmouseup` mit `e.Button == 1`.

**Kein `@ondblclick` verwenden.** Der WPF-Host (`WebView2CompositionControl`) reicht beim zweiten Klick eines echten Doppelklicks das Mouse-down doppelt an die WebView weiter: Chromium zählt `detail` 1 → 2 → 3, der zweite `click` kommt mit `detail=3`, und `dblclick` feuert nie. Doppelklick deshalb über `@onclick` mit `e.Detail >= 2` erkennen (Workspace-Chip, Verbindungszeile). Fiel lange nicht auf, weil die E2E-Tests Doppelklicks synthetisch bzw. per CDP direkt in die WebView schickten, also am Host vorbei. Gefunden durch den Nutzer in v1.1.0.

**Mausinteraktionen mit echter Windows-Eingabe prüfen** (`SendInput` über `realclick.ps1` im Scratchpad: `ClientToScreen` des Fensters + CSS-Position × `devicePixelRatio`), nicht nur per CDP. Nur so läuft die Eingabe durch den WPF-Host wie beim Nutzer. Dabei immer nur **eine** App-Instanz mit Debug-Port starten: Zwei Instanzen teilen sich das WebView2-Datenverzeichnis, und unterschiedliche Browser-Argumente (z. B. zwei Debug-Ports) lassen die zweite beim Start mit `0x8007139F` abstürzen. Vorher prüfen, dass der Klick ankommt (z. B. `mousedown`-Listener per CDP): Holt Windows das App-Fenster nicht in den Vordergrund (`SetForegroundWindow` wird verweigert, wenn der Nutzer gerade woanders arbeitet), landet der Klick im Fenster, das dort oben liegt – dann abbrechen statt weiterklicken (in WP-12 passiert). Screenshots (`PrintWindow`) enthalten die 31 px hohe Titelleiste: Bildkoordinaten ≠ CSS-Koordinaten, Klickziele immer per `getBoundingClientRect` bestimmen.

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
  - Verbindungsabbruch: `DatabaseException.IsConnectionLost` (ORA-00028/00603/01012/01089/01092/02396/03113/03114/03135/12537/12547/12570/12571 sowie bereits geschlossene Connection). Jede DB-Fehlerstelle der UI ruft `ShellState.ReportFailure`; Banner und Statusleiste bieten „Neu verbinden“ (= normaler Connect, Workspaces/Tabs kommen aus dem gespeicherten Zustand). Erkennung zunächst reaktiv beim nächsten Statement; seit v1.6 zusätzlich per Keep-alive (Abschnitt 6). Integrationstest killt die Session als SYSTEM (ergibt ORA-03135).
  - Release: Version 1.0.0 (`Directory.Build.props`), Publish: `dotnet publish src/FerretSharp.App -c Release -r win-x64 --self-contained -o <ziel>` (~180 MB, Ordner statt Single-File wegen WebView2/WPF-Nativbibliotheken).

### v2 – Sandbox-Editing

#### WP-08 Transaktionsmodell & Schreibschutz
- `OracleSession` bekommt ein Transaktions-API (Begin, Savepoint, Commit, Rollback, `ExecuteNonQueryAsync`).
- Prod gesperrt → `SET TRANSACTION READ ONLY` mit Snapshot-Semantik (Refresh = neue Tx, `ORA-01555` → Neustart).
- Statusleiste: Tx-Alter, Anzahl Pending/Flushed, Warnung bei Verbindungsverlust mit offener Transaktion.
- **Fertig wenn:** Integrationstest belegt, dass DML auf einer gesperrten Prod-Session von Oracle abgelehnt wird. → erfüllt (`SessionTransactionTests.Oracle_rejects_dml_in_a_locked_session`, ORA-01456).
- Umgesetzt (Release 1.8.0, ADR 0006):
  - Gesperrt sind die Workspace-Sessions der Profile mit „Schreibgeschützt“ (Prod voreingestellt), nicht nur Prod (Entscheidung des Nutzers); Dev/Test ohne Häkchen laufen bis WP-09 ohne Transaktion. `WorkspaceManager` sperrt direkt nach dem Öffnen (`IDatabaseConnection.UseReadOnlySnapshotsAsync`), die Explorer-Session bleibt ohne Transaktion.
  - Snapshot je neuer Abfrage statt bis F5 (siehe Abschnitt 6); `RowPage.DataAsOf` → Tab-Fußzeile „Stand 14:02:13“.
  - Statusleiste: „schreibgeschützt (Oracle)“ bei gesperrten Profilen; Tx-Alter und Pending/Flushed folgen mit WP-09. Das Verlust-Banner warnt, wenn eine schreibende Transaktion offen war (`WorkspaceManager.TransactionOf`).
  - `BeginTransactionAsync` (nicht „BeginReadWrite“: `ReadOnlyTests` werten „Write“ im Namen als Datenschreiben).

#### WP-09 Editieren
- `ChangeTracker`, `RowChange`, Dirty-Markierung im Grid (Zellfarbe je Zustand Pending/Flushed, `cellClassRules`), Inline-Editing über AG-Grid-Cell-Editoren (Validierung in .NET), Zeile hinzufügen/löschen. Kein Editieren bei `RowKey.None` oder exotischen Typen.
- `OracleTypeMapper` Text → Oracle-Typ inkl. Fehlermeldung bei ungültiger Eingabe.
- `OracleDataAccess.FlushAsync` gemäß 5.6 (Savepoint, `FOR UPDATE WAIT n`, RowKey-WHERE, `RETURNING`, `BindByName`).
- Lock-Konflikte (`ORA-30006`, `ORA-00054`) als Dialog, inkl. sperrender Session, falls ermittelbar.
- „Änderungen als SQL anzeigen“.
- **Fertig wenn:** Änderungen in Workspace A sind in B bis zum Commit unsichtbar; ein Lock-Konflikt zwischen A und B erscheint nach ≤ n s als Dialog (Integrationstest); Rollback stellt das Grid zurück; Tests für die Statement-Generierung. → erfüllt (`EditingTests` im Integrationsprojekt, E2E mit zwei Workspaces).
- Umgesetzt (Release 1.9.0); Entscheidungen des Nutzers: Commit schreibt Ausstehendes vorher automatisch, PK bestehender Zeilen nur lesbar, Concurrency-Check nur über die geänderten Spalten, Sperr-Wartezeit 3 s (Einstellungen: 1/3/5/10/30 s).
  - Bearbeitbar sind Profile ohne „Schreibgeschützt“ (Badge BEARBEITBAR), Tabellen mit Row-Key (PK oder ROWID). Nicht editierbar (`OracleTypeMapper.NotEditableReason`, als Tooltip): Views, virtuelle und Identity-Spalten, PK bestehender Zeilen, LOB/LONG/INTERVAL/TIMESTAMP WITH TZ/BINARY_FLOAT/DOUBLE/Objekttypen, NUMBER > 28 Stellen.
  - Core: `ChangeTracker` pro Tab (Ausstehend/Geschrieben je Zelle, Undo des letzten Schreibvorgangs), `DmlBuilder` (Query/), `IDataEditor`/`OracleDataEditor` statt `OracleDataAccess.FlushAsync`: Savepoint `FS_FLUSH_n` pro Schreibvorgang, alles oder nichts; je Zeile `SELECT … FOR UPDATE WAIT n` und Vergleich der geänderten Spalten, dann DML; Inserts lesen die Zeile über `RETURNING ROWID` neu (Defaults, Trigger). Fehler als `LockConflictException`, `ConcurrencyConflictException` (Überschreiben/Verwerfen), `RowGoneException`, `WriteFailedException` (ORA-Code → deutscher Text). Sperrende Sessions über `V$LOCKED_OBJECT`/`V$SESSION` (nur mit `SELECT_CATALOG_ROLE`, sonst Hinweis).
  - Oracle 23 meldet ein abgelaufenes `FOR UPDATE WAIT n` als **ORA-00054**, nicht ORA-30006 – beide werden als Sperrkonflikt behandelt.
  - UI: `WorkspaceEditing` (State/) orchestriert Schreiben/Commit/Rollback/Undo je Workspace. Grid mit `readOnlyEdit` + `cellEditRequest` und eigenem `FerretCellEditor`: Doppelklick oder Tippen startet, Enter prüft den Wert über `ValidateEdit` in .NET und lässt den Editor bei Fehlern mit Meldung offen. Neue Zeilen als angeheftete Zeilen oben („+ Zeile“), Entf markiert zum Löschen, Kontextmenü „Zeile löschen“/„Ausstehende Änderungen verwerfen“. Zellfarben `fs-pending`/`fs-flushed`, Zeilen `fs-row-deleted`/`fs-row-new`.
  - Statusleiste: „Tx seit X min · N ausstehend · M geschrieben“ (Warnfarbe ab 10 min), Schreiben (Ctrl+S), ↶, SQL, Commit (Ctrl+Shift+Enter, auf Prod mit Bestätigung), Rollback (mit Bestätigung). Punkt am Tab bei Änderungen.
  - Schutz vor Datenverlust: Tab schließen mit Ausstehendem fragt; Workspace schließen, Trennen, Verbindung wechseln/löschen und App beenden (`ExitGuard`, Closing-Handler im `MainWindow`) bieten Committen/Verwerfen/Abbrechen (`LeaveDialog`).

#### WP-10 Prod-Freischaltung & Politur v2 → Release v2.0.0
- Freischalt-Toggle für Prod mit Bestätigungsdialog (Rollback der Read-only-Tx, normale Tx starten); beim Sperren werden offene Änderungen erzwungen committed oder verworfen.
- Shortcuts v2 und Commit-Bestätigung auf Prod (beides seit 1.9.0 vorhanden; Prod-Profile ohne „Schreibgeschützt“ sind schon editierbar).
- CLOB/BLOB-Editor-Dialog.
- `CHANGELOG.md`, Tag `v2.0.0`.
- Umgesetzt (Release 2.0.0, ADR 0008); Entscheidungen des Nutzers: Freischaltung **pro Workspace**, gilt **bis zum manuellen Sperren** (nie gespeichert), auf Prod **Verbindungsnamen eintippen**, CLOBs bis **10 MB** im Dialog, größere nur per Datei.
  - Core: `OracleSession.StopReadOnlySnapshotsAsync` (über `IDatabaseConnection`), `WorkspaceManager.UnlockAsync`/`LockAsync`/`IsWritable`/`IsUnlocked`. Freigeschaltete Workspaces nur im Speicher: Schließen, Trennen, Neu verbinden sperren wieder. Sperren verlangt eine geschlossene schreibende Transaktion; lässt sich eine Session nicht wieder sperren, wird sie verworfen.
  - UI: Badge als Schalter („PROD · READ-ONLY“ → `UnlockDialog`, „PROD · FREIGESCHALTET“ gestreift → Sperren), „Freischalten …“/„Sperren“ in der Statusleiste, Einträge im „⋯“-Menü der Chips, offenes Schloss am Chip. Sperren läuft über `GuardAsync` („Workspace sperren“). Nach dem Freischalten laden die Tabs neu (sie zeigten den Snapshot). `FerretGrid` schaltet das Editieren zur Laufzeit um (`setEditable` in `grid.js`), ohne das Grid neu aufzubauen.
  - LOBs: `IDataAccess.ReadLobAsync` (ganzer Wert per Row-Key, `QueryBuilder.BuildSelectLob`), die Session holt LOBs vollständig (`InitialLOBFetchSize = -1`; Grid-Abfragen selektieren LOB-Spalten nie direkt). `ChangeTracker.SetContent` merkt den beim Öffnen gelesenen Wert als Referenz des Concurrency-Checks. Binds `Clob`/`NClob`/`Blob`. `LobEditorDialog` (Doppelklick, Enter oder „Inhalt öffnen …“; auf gesperrten Workspaces nur lesen): Textfeld bis `LobLimits.MaxEditLength`, Datei speichern/laden bis `MaxLoadLength` (100 MB) über `IFileSaveService.SaveBytesAsync`/`IFileOpenService`, Hex-Ansicht und Bildvorschau (`LobContent`).
  - Textfeld im LOB-Dialog: Der Text geht erst beim `change` (Blur) nach .NET – E2E mit echter Maus auf „Übernehmen“ klicken, ein JS-`click()` übernimmt nichts. Esc im Textfeld schließt den Dialog nicht.
  - Nebenbei behoben: Doppelklick auf Zellen neuer Zeilen startete im WPF-Host kein Editieren (seit 1.9): angeheftete Zeilen haben `row-index="t-0"`, das ergab `NaN`. Im E2E fiel das nicht auf, weil CDP-Doppelklicks AG Grids eigenes `dblclick` auslösen.

### v3 – .NET-Integration

Die Arbeitspakete werden zu Beginn von v3 mit dem Nutzer verfeinert. Grober Zuschnitt:

#### WP-11 Projekt-Anbindung & Modell-Export
- Pro Connection (oder Workspace) ein .NET-Projekt bzw. Build-Output verknüpfen; DbContext-Typ auswählen.
- EF-Core-Modell laden (Entity ↔ Tabelle, Property ↔ Spalte, Value Converter/Enums, Navigations, Owned Types). DbContext-Erzeugung wie `dotnet ef` (`IDesignTimeDbContextFactory`, sonst Host-Builder/Default-Konstruktor).
- **ADR zu Beginn:** In-Process (`AssemblyLoadContext` + `AssemblyDependencyResolver`) vs. Out-of-Process (Hilfsprozess im Kontext des Zielprojekts wie `dotnet ef`, exportiert das Modell als JSON). Tendenz Out-of-Process wegen Versionskonflikten bei EF-Core-/Oracle-Provider-Assemblies; WP-13 (LINQ-Konsole) fließt in die Entscheidung ein.
- Modell landet als Annotation-Schicht im Core (siehe Abschnitt 2), nicht als EF-Abhängigkeit von `FerretSharp.Core`.
- Umgesetzt (Release 2.1.0, ADR 0009). Ausgangslage beim Nutzer: DB-first von Hand (erst DB ändern, dann Entity/Konfiguration), keine Migrations, Namenskonvention im Code (Tabellen groß, `KundenId` → `KUNDEN_ID`), **eigene Value Converter**, ein DbContext in einer Klassenbibliothek (Konstruktor `DbContextOptions`), Entities in einem anderen Projekt, EF Core 8 (3.1/5-Projekte werden gerade umgestellt).
  - Entscheidung nach Abwägung mit dem Nutzer: **kompilierter DbContext statt statischer Quelltext-Analyse** – eigene Converter (bool ↔ J/N, Enum-Kürzel) lassen sich statisch nicht auswerten, Enum-Filter wären still falsch. **Hilfsprozess statt In-Process** (Runtime/Versionen des Projekts, Absturzsicherheit). Minimum EF Core 8.
  - `FerretSharp.ModelHost` (net8.0, gegen EF Core 8.0.0 nur kompiliert, `ExcludeAssets="runtime"`; `UseOracle` per Reflection, also unabhängig von der Provider-Version): `dotnet exec --runtimeconfig <erzeugt> --depsfile <Projekt>.deps.json --additionalprobingpath <NuGet-Ordner aus obj/project.assets.json>`. Context: `IDesignTimeDbContextFactory` → eigene Options mit Platzhalter-Connection-String + `DbContextOptions`-Konstruktor → parameterlos. Liest `IDesignTimeModel`, schreibt JSON in eine Datei (nicht stdout). Wertetabellen für Enums/konvertierte bools über den Converter des Projekts (`ConvertToProvider`). Format `ModelExport` als gemeinsame Quelldatei (Core + ModelHost). Liegt im App-Output unter `modelhost/` (App.csproj: ProjectReference ohne Output, `Private="false"`, RID/Ausgabeordner entfernt, Kopier-Targets für Build und Publish).
  - Core (`ClrModel/`): `ClrProjectLink` im `ConnectionProfile` (`ClrProject`), `BuildOutputLocator` (bin/&lt;Konfiguration&gt; oder Artifacts-Layout, Zielframework aus `runtimeTarget`, veralteter Build über neuere `.cs`/`.csproj` auch in referenzierten Projekten), `ModelHostRunner`/`DotNetCli` (installiertes `dotnet`, Timeout, Prozessbaum beenden), `ClrModelMapping` (Annotation-Schicht: Entity/Property je Tabelle/Spalte, Synonyme, Abweichungen), `ClrModelManager` (lädt nach dem Verbinden im Hintergrund, bei Änderung der Verknüpfung, „Neu laden“, „Neu bauen“).
  - UI: Abschnitt „C#-Modell“ im Verbindungsdialog (Projekt, Build-Konfiguration, DbContext), Statusleiste „C# · N Entities“, Seite „C#-Modell“ (`ShellPage.Model`) mit Abgleich, Spalten-Ansicht mit Property, C#-Typ und Converter.
  - `samples/FerretSharp.SampleModel` bildet den Stil des Nutzers nach (eigene `Directory.Build.props`/`Directory.Packages.props`, erbt nichts von FerretSharp); `ModelHostTests` laufen dagegen ohne DB. `tools/sample-db/05-clr-model.sql` ergänzt KUNDEN.GESPERRT (J/N) und KUNDENART.

  - Nachträge: 2.1.1 – Entities mit `ToView` bekommen durch die Konvention des Nutzers zusätzlich einen Tabellennamen; EF fragt dann über die View ab (Tabelle nur für SaveChanges). Abgleich prüft deshalb View vor Tabelle, `PropertyExport.ViewColumn` trägt die Spaltennamen der View, `MappedEntityCount` zählt jede Entity einmal. 2.1.2 – Abgleich liest Spaltennamen mit **einer** Abfrage pro Schema (`ISchemaReader.GetColumnNamesAsync`, großer `FetchSize`) statt drei Abfragen pro Tabelle: beim Nutzer (425 Entities, ~4000 Properties, DB über Kunden-VPN) von > 3 min auf 12 s. ModelHost meldet Schritte als `##ferretsharp-progress`-Zeilen auf stdout; Modell-Seite und Statusleiste zeigen den aktuellen Schritt mit Laufzeit, danach die Dauer jedes Schritts.
  - Erkenntnis beim Nutzer: Der Oracle-Provider quotet alle Namen (`"a"."Chargenr"`), Spalten mit gemischter Schreibweise im Modell (z. B. `Chargenr` gegen `CHARGENR`) führen bei der ersten Abfrage zu ORA-00904 – der Abgleich meldet sie zu Recht als „Abweichende Groß-/Kleinschreibung“.

#### WP-12 Schema-Anreicherung → Release 2.2.0
Entscheidungen des Nutzers (2026-10-05):
- **Namen:** Der Oracle-Name bleibt vorne (der Nutzer sucht über Oracle-Namen), C#-Namen dezent daneben. Umschaltbar über eine Einstellung **„C#-Namen: aus · daneben (Standard) · vorne“** („vorne“ tauscht Haupt- und Nebenbeschriftung). Explorer: Entity-Name als zweite Beschriftung, Suche findet DB- und C#-Namen. Grid-Kopf: unter dem Spaltennamen `KundeId · int`; Spaltensuche (Ctrl+F) und Spaltenauswahl im Filter finden auch Property-Namen; Tab-Tooltip mit Entity. SQL-Vorschau bleibt immer bei DB-Namen. Ohne verknüpftes Projekt ändert sich nichts.
- **Enums und konvertierte Werte** über die Wertetabellen (`PropertyExport.Values`, berechnet mit den Convertern des Projekts): Anzeige im Grid als **„Gewerbe (2)“** (C#-Name und DB-Wert); Bools mit Converter als **`true`/`false`**; Werte, die es im Enum nicht gibt, bleiben roh und werden markiert. Filter: Enum-Spalten bekommen ein Dropdown mit den Members (Operatoren `=`, `≠`, „in“), FerretSharp übersetzt in den DB-Wert. Editieren (seit 1.9): Dropdown, geschrieben wird der DB-Wert.
- **Navigations als FK-Quelle** (`FkSource.ClrModel`): Beziehungen aus dem Modell ohne FK-Constraint in der DB kommen beim Nutzer **häufig** vor → volle Priorität. In der FK-Navigation (Kontextmenü) nutzbar, markiert „aus C#-Modell“, deklarierte FKs nicht doppelt; eigener Abschnitt in der Ansicht „Constraints“.
- **Nicht in WP-12** (Entscheidung des Nutzers, Backlog): Sprung zur Entity-Klasse in Visual Studio und „Namen kopieren“.
- Arbeitsweise des Nutzers (für spätere Pakete wichtig): pro Datenbank ein eigener Klon des DbContext-Repos auf dem passenden Branch (Prod-DB → release-Branch, Test-DB → test-Branch, Dev-DB → dev-Branch), jeweils als C#-Projekt der Verbindung verknüpft; entwickelt wird in einem weiteren, eigenen Klon.
- Umgesetzt (Release 2.2.0, ADR 0010):
  - Core (`ClrModel/`): `TablePresentation` (Beschriftung `ColumnLabel`, Zellanzeige `Present`, Wertoptionen, Entity-Name; `Plain` = bisherige DB-Sicht), `ValueTable` (DB-Wert ↔ Member, Zahlen als `decimal`, CHAR ohne Padding, unbekannte Werte markiert, Flags-Enums zerlegt), `ClrModelMapping.ForeignKeys` (Navigation → `ForeignKeyInfo` mit `FkSource.ClrModel`, Name `Auftrag.Bearbeiter`; Owned/Table-Splitting-Selbstbezüge und Duplikate fallen weg). `SchemaCache.SetForeignKeys(source, …)`: weitere FK-Quellen, ein deklarierter FK über dieselben Spaltenpaare gewinnt (auch nach Schema-Refresh). `ColumnSearch.Find(…, alternateName)` mit C#-Wortgrenzen. `AppSettings.ClrNames` (`ClrNameDisplay`), `AppSettingsService.Changed`.
  - Entscheidung beim Umsetzen: Filter und Edits tragen **DB-Werte** (`2`, `J`); QueryBuilder/DML bleiben unverändert, gespeicherte Filter funktionieren ohne Modell. „aus“ blendet nur Namen aus (Beschriftung, Suche), Enum-Werte bleiben. Kopieren/Export liefern DB-Werte. Index-Hinweis nur für deklarierte FKs.
  - UI: `PresentationService` (State/, meldet nur echte Änderungen von Modell oder Einstellung), Explorer (Entity rechts, „vorne“ tauscht), Grid-Kopf mit dritter Zeile (62 px), `FerretSelectEditor` (Member-Liste; Enter oder Mausauswahl übernimmt), `ValuePicker` (Filterwert, „in“ als Mehrfachauswahl), Kontextmenü „aus C#-Modell“, Abschnitt „Beziehungen aus dem C#-Modell“ in „Constraints“, FK-Badge in Modellfarbe (`--clr`). Lädt das Modell nach dem Grid oder ändert sich die Einstellung, ändert `updateColumns` (grid.js) die Spalten an Ort und Stelle und lädt die Zeilen neu.
  - AG-Grid-Fallen (gefunden im E2E): Einfache Objekte in Column-Defs (`headerComponentParams`) werden beim Zusammenführen mit `defaultColDef` **tief kopiert** – veränderliche Metadaten als Funktion übergeben (`getMeta`). Die Kopfhöhe als Grid-Option (`headerHeight`) setzt die Zeilenhöhe auf 42 px zurück – nur als Theme-Parameter setzen.
  - Beispiel: `Auftrag.Bearbeiter` → `Mitarbeiter` ohne Constraint (`tools/sample-db/06-clr-relations.sql`); `ferret-sample` des Nutzers braucht das Skript noch (README in `tools/sample-db`).
  - Nachtrag 2.2.1 – **Enum-Anzeigenamen** (der Nutzer setzt `[Display(ResourceType = typeof(…Resources), Name = nameof(…))]` an seine Enum-Member): Der ModelHost liest `DisplayAttribute.GetName()` je Member (`ValueMapping.DisplayName`, optional, Format-Version unverändert) in der UI-Kultur von FerretSharp (`--culture`, vom Runner übergeben). Satelliten-Assemblies (`bin\de\X.resources.dll`) stehen bei Projektverweisen nicht in der deps.json – der `Resolving`-Handler des Hosts sucht sie im Kultur-Unterordner. Entscheidung des Nutzers: Anzeige nur mit Display-Text („Fertigungsauftrag (0)“), Member-Name im Zell-Tooltip, in der Filter-Liste und in der Spalten-Ansicht; gilt auch bei „C#-Namen: aus“ (wie alle Enum-Werte). Nur Enums, keine Property-Anzeigenamen. Beispiel: `AuftragStatus` mit `EnumTexts.resx` (neutral Englisch) + `EnumTexts.de.resx`, `Kundenart.Behoerde` mit `[Display(Name = "Behörde")]`. `.Designer.cs`-Dateien gelten als generiert und brauchen `#nullable enable`.

#### WP-13 LINQ-Konsole
- Roslyn-Scripting gegen den geladenen DbContext, Ergebnis im Grid, generiertes SQL (`ToQueryString()`) daneben.
- **Kern-Anwendungsfall (Nutzer):** eine LINQ-Query direkt aus dem eigenen Code hineinkopieren und ausführen bzw. ihr SQL/ihren Plan sehen – ohne Debugger und ohne den QueryString herauszusuchen. Daraus folgt:
  - Freie Bezeichner der kopierten Query (lokale Variablen, Parameter wie `customerId`, `request.From`, `ct`) erkennen (Roslyn-Diagnose CS0103) und als Eingabefelder anbieten bzw. in einem Variablen-Bereich deklarieren lassen; den Namen des Kontexts (`_context`, `dbContext`, `db` …) auf den geladenen DbContext abbilden.
  - Endet die Query ohne Materialisierung (`IQueryable`), nur SQL/Plan zeigen bzw. seitenweise ins Grid laden; `ToListAsync()`/`FirstOrDefaultAsync(ct)` usw. ausführen.
  - Extension-Methoden, Helfer und Enums des Projekts müssen verfügbar sein → Scripting im Kontext der Projekt-Assemblies (Argument für Out-of-Process im ADR aus WP-11).
- Idealerweise auf der Connection/Transaktion des Workspaces, damit eigene uncommittete Änderungen sichtbar sind (beeinflusst das ADR aus WP-11).
- Prod-Schutz gilt auch hier: Ausführung in `SET TRANSACTION READ ONLY`, solange nicht freigeschaltet; `SaveChanges` sowie `ExecuteUpdate`/`ExecuteDelete` nur nach Freischaltung.
- Umgesetzt (Release 2.3.0, ADR 0011). Entscheidungen des Nutzers: **SQL abfangen, FerretSharp führt aus** (nicht der Hilfsprozess mit eigener Connection), **Tab im Workspace**, **Monaco** als Editor, `ExecuteUpdate`/`ExecuteDelete` **nur auf schreibbaren Workspaces**.
  - Spike vorab: Der Oracle-EF-Provider läuft ohne Datenbank, wenn Interceptors das Öffnen der Verbindung und jedes Kommando unterdrücken (`CommandCapture`: zeichnet SQL und Parameter mit `OracleDbType` auf, gibt EF ein leeres Ergebnis). Roslyn läuft im Projektprozess nur bis **4.11**: ab 4.12 kommt `System.Reflection.Metadata` 9.0 mit, das in einer .NET-8-Runtime nicht neben der Framework-Version 8.0 ladbar ist (`FileLoadException`).
  - ModelHost: Konsolenmodus `--console <pipe>` (Named Pipe, JSON-Zeilen `LinqRequest`/`LinqResponse`, gemeinsame Datei `LinqProtocol.cs`); `LinqConsole` kompiliert Variablen + Code mit Globals (`__Context`, `__Token`), erkennt CS0103-Namen: Kontext (`name.DbSet` oder übliche Namen) und Token werden automatisch deklariert, die übrigen bekommen Vorschläge aus der Verwendung (`TypeSuggestion`, erst **nach** dem Deklarieren des Kontexts – sonst haben die Lambdas keine Typen). Ein `IQueryable`-Ergebnis wird aufgezählt, damit das Kommando entsteht. Ein Interceptor-Exemplar für die ganze Lebensdauer (neue Exemplare je Context ließen EF jedes Mal einen neuen internen Service-Provider bauen). Resolver lädt aus dem Host-Ordner (`typeof(Program).Assembly.Location`; `AppContext.BaseDirectory` ist bei `dotnet exec --depsfile` nicht verlässlich).
  - Core: `LinqConsoleHost` (Prozess + Pipe; Reader/Writer erst nach dem Verbinden anlegen – `AutoFlush` schreibt sofort, eine unverbundene Pipe wirft), `LinqConsoleService` (ein Host pro verknüpftem Projekt, Neustart nach neuem Build, Ende beim Entknüpfen/Trennen/Beenden; reagiert nur auf echte Link-Änderungen, nicht auf jeden Ladeschritt des Modells), `LinqStatements` (Kommando → `QuerySpec`, Werte zurück in CLR-Typen, `OracleDbType` → `OracleTypeHint`; nur SELECT/WITH lesen, nur UPDATE/DELETE schreiben, PL/SQL-Blöcke nie). `IDataAccess.ReadSqlAsync` (Seiten durch erneutes Ausführen und Überspringen – `SELECT * FROM (…)` scheitert an doppelten Spaltennamen der EF-Joins, ORA-00918; Spaltentypen aus dem Treiber), `IDataEditor.ExecuteAsync` (Transaktion bei Bedarf beginnen, eigener Savepoint `FS_LINQ_n`, gesperrte Workspaces abgelehnt).
  - UI: Tabs sind jetzt `WorkspaceTab` (`TableTab` | `LinqTab`); LINQ-Tabs werden mit `TabState.Linq` gespeichert (Tabelle `LinqTabState.NoTable` – ältere Versionen verwerfen sie wie verschwundene Tabellen). `LinqView` (Editoren, Hinweise, Diagnosen, Kommandos, SQL, Ergebnis), `MonacoEditor` + `js/monaco.js`, `SqlResultGrid` (dieselbe AG-Grid-Brücke, ohne Editieren/Sortieren). Ctrl+Enter/F5 führen aus (globale Shortcuts in der Capture-Phase erreichen auch den Editor; danach `StateHasChanged`, weil kein UI-Ereignis rendert), Ctrl+F öffnet die Suche des Editors, Ctrl+Shift+L öffnet eine Konsole.
  - Monaco 0.57.0 lokal unter `wwwroot/lib/monaco/` (README mit Herkunft/Integrity), AMD-Build `min/vs` ohne die Sprachdienste TS/CSS/HTML/JSON und ohne fremde Übersetzungen (6 MB). **Falle:** `vs/nls/lang/de.js` ist kein AMD-Modul (setzt nur Globals) – über `'vs/nls': { availableLanguages }` angefordert, wartet der Loader ewig; deshalb als normales Script vor dem Editor laden.
  - App: Der `modelhost`-Ordner enthält jetzt alle DLLs des Hosts samt Sprachordnern (Roslyn-Meldungen auf Deutsch); `CloseConnection` beendet den Konsolen-Prozess. Stirbt FerretSharp hart, endet der Host nach einigen Sekunden über die abgebrochene Pipe.

#### WP-14 Explain-Plan
- Plan-Dialog für die Grid-Abfrage (Button neben „SQL“ an der Filterleiste) und für LINQ-Queries aus der Konsole; der Nutzer wählt die Variante:
  - **Geschätzt:** `EXPLAIN PLAN FOR …` → `PLAN_TABLE`, ohne Ausführung. Schreibt in die (session-lokale, temporäre) `PLAN_TABLE` → nur über den bewussten Schreibweg aus WP-08, nicht über die Lesesperre. Prüfen, ob das in einer `READ ONLY`-Transaktion (gesperrtes Prod) erlaubt ist.
  - **Tatsächlich:** `DBMS_XPLAN.DISPLAY_CURSOR` bzw. `V$SQL_PLAN…` für die letzte Ausführung, optional mit echten Zeilenzahlen (`GATHER_PLAN_STATISTICS`). Reines SELECT, braucht aber Leserechte auf die `V$`-Views (`SELECT_CATALOG_ROLE`) → klarer Hinweis, wenn sie fehlen.
- Ein Plan-Modell im Core für beide Quellen (gleiche Spaltenstruktur), eine Baumansicht (Operation, Objekt, Kosten, Zeilen geschätzt/tatsächlich).
- LINQ: SQL kommt von EF (`ToQueryString()`/Befehlstext); für „Tatsächlich“ liefert die Konsole bzw. der Hilfsprozess SQL-Text/`SQL_ID` der Ausführung (z. B. EF-Interceptor). Hinweis im Dialog: Geschätzte Pläne kennen die Bind-Werte nicht und können vom tatsächlichen abweichen.
- Umgesetzt (Release 2.4.0, ADR 0012). Entscheidungen des Nutzers: EXPLAIN PLAN **auch auf Prod** (schreibt nur in die sitzungslokale `PLAN_TABLE`), tatsächlicher Plan **erste Seite oder ganzes Ergebnis, wählbar im Dialog**, Anzeige als **Dialog**.
  - Spike: `EXPLAIN PLAN` geht ohne Bind-Werte (Platzhalter gelten als Text: `TO_NUMBER(:P_0)`), aber **nicht in einer READ-ONLY-Transaktion** (ORA-00604/ORA-01456) → geschätzt immer auf der Explorer-Session (`ISchemaReader.ExplainAsync`). Die `SQL_ID` lässt sich im Client berechnen (letzte 8 Byte von MD5(Text + NUL), zwei Little-Endian-Hälften, Base 32 `0123456789abcdfghjkmnpqrstuvwxyz`) – stimmt mit `V$SQL` überein. Ohne `SELECT_CATALOG_ROLE` ORA-00942 auf `V_$SQL`.
  - Core: `ExecutionPlan`/`PlanStep`/`Plans` (Query/: `SqlId`, `WithStatistics` – Hint nach dem ersten SELECT, ein vorhandener Hint-Kommentar wird ergänzt, weil Oracle nur den ersten liest –, `Format` im Stil von DBMS_XPLAN, `IsMisestimate`: Faktor ≥ 10 bei ≥ 100 Zeilen, ohne ganzes Ergebnis nur Unterschätzungen). `OracleSession.ExplainPlanAsync` = eigener enger Weg (nur Statements, die die Lesesperre passieren; `EXPLAIN PLAN SET STATEMENT_ID = '<erzeugt>' FOR …`, Zeilen lesen, wieder löschen). `IDataAccess.ExplainActualAsync` prüft erst die Rechte (`OraclePlans.RightsProbe`), führt mit `GATHER_PLAN_STATISTICS` aus (500 Zeilen oder alle), sucht den jüngsten Child-Cursor der berechneten `SQL_ID` und liest `V$SQL_PLAN_STATISTICS_ALL`. Fehlende Rechte/gesperrte Session → `PlanUnavailableException` mit Grant-Hinweis.
  - UI: `PlanDialog` (Geschätzt | Tatsächlich, „ganzes Ergebnis“, Prädikate je Schritt, Markierungen Fehlschätzung/FULL, abgeschwächte Schritte mit Starts 0 – adaptive Pläne behalten verworfene Zweige im Cursor –, „Als Text kopieren“). Grid: „Plan“ an der Filterleiste; „ganzes Ergebnis“ nimmt dort die Abfrage ohne Seitenlimit (sonst misst man nur die erste Seite). LINQ: „Plan“ neben „SQL“ mit den Bind-Werten aus dem Code.
  - E2E-Erkenntnis: CDP-Klicks sind keine Nutzergeste, `navigator.clipboard.writeText` schlägt dann fehl – Kopieren nicht per CDP prüfen, und die Zwischenablage des Nutzers nicht überschreiben (er arbeitet nebenher).

#### WP-15 Code-Generierung → Release v3.0.0
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
- UI end-to-end prüfen, ohne die echten Nutzerdaten anzufassen: App mit `--data-dir=<scratch>` und `--theme=dark|light` starten, Umgebungsvariable `WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS=--remote-debugging-port=9333` setzen und die Seite per Chrome DevTools Protocol (`Runtime.evaluate`) bedienen; Screenshots per `PrintWindow` vom App-Fenster. Für eine Test-DB einen eigenen Container starten (`gvenzl/oracle-free:23-slim-faststart`, `APP_USER`/`APP_USER_PASSWORD`) und danach gezielt per Name entfernen – am einfachsten mit `tools/sample-db/New-SampleDb.ps1 -Name <eigener Name> -Port <freier Port>` (Beispielschema inkl. VERTRAG mit 71 Spalten). Der Container `ferret-sample` auf Port 1522 ist die Beispiel-DB des Nutzers (in seinen echten Verbindungen eingetragen) – nicht anfassen. Im Credential Manager angelegte Test-Einträge über die App wieder löschen. Läuft schon eine FerretSharp-Instanz des Nutzers (z. B. aus Visual Studio), teilt sich eine zweite Instanz deren WebView2-Datenordner und stürzt mit Debug-Port mit `0x8007139F` ab → zusätzlich `WEBVIEW2_USER_DATA_FOLDER=<scratch>` setzen und in einen eigenen Ordner bauen (`dotnet build src/FerretSharp.App -o <scratch>`), weil der Debug-Output dann gesperrt ist.
- E2E: Rechtsklick und Mittelklick mit `Input.dispatchMouseEvent` (echte Maus), die Zwischenablage über `Get-Clipboard` prüfen. **Kein `navigator.clipboard.readText()`** im WebView aufrufen: Es öffnet eine Berechtigungsabfrage, die als zusätzliches CDP-Target (`edge://permission-request-dialog/`) vor der App-Seite in `/json` steht; das CDP-Skript wählt deshalb das Target mit der URL `https://0.0.0.1/`.
- **Native Dialoge (Speichern unter) nicht per UI Automation bedienen**: Sie öffnen sich in den echten Ordnern des Nutzers (Dokumente, OneDrive). Ein Fehlgriff traf dort einen Ordner-Eintrag statt des Dateinamenfelds, und der Dialog speicherte die Testdatei im Dokumente-Ordner (in WP-07 passiert, Datei wurde ins Scratchpad verschoben). Export-Inhalte über die Zwischenablage prüfen; den Speichern-Pfad höchstens bis zum Öffnen des Dialogs testen.
- Statements per PowerShell-Pipe an `docker exec … sqlplus` bekommen ein BOM vorangestellt (SP2-0734) → `docker exec <name> bash -c "echo '…' | sqlplus …"` oder Skriptdatei per `docker cp`.
- Farbige Button-Varianten (`.btn.primary`, `.btn.danger-solid`) müssen im `:hover` ihren Hintergrund selbst setzen: `.btn:hover:not(:disabled)` setzt `--hover` und gewinnt sonst (Fehler in v1.0.0: Text beim Hover unlesbar). Text auf farbigen Flächen immer über Tokens (`--accent-text`, `--danger-text`), die im Dark Mode dunkel sind. Prüfen mit echtem Hover (`Input.dispatchMouseEvent` mouseMoved) und berechnetem Kontrast, nicht nur per Screenshot.
- CSS-Klassennamen in Komponenten nicht mit globalen Klassen kollidieren lassen (`.empty` ist der Leerzustand mit `position: absolute; inset: 0` – so überdeckte in WP-06 ein Menüeintrag das ganze Kontextmenü).
- E2E-Fallen: Nach einem Klick auf einen Tab ist `.page.active` noch kurz die alte Seite → auf etwas Spezifisches des Ziels warten. Synthetische Events erreichen Blazor, ersetzen aber keinen Test mit echter Eingabe (`Input.dispatchMouseEvent`/`dispatchKeyEvent`), sonst bleiben Fehler wie das fehlende `auxclick` unentdeckt. Im Infinite Row Model kennt das Grid anfangs nur ~501 Zeilen; `scrollTop` weiter unten wird abgeschnitten.
