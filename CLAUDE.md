# FerretSharp – Oracle explorer for .NET developers

> Projektanweisungen für Claude Code. Bitte vollständig lesen, bevor ein Arbeitspaket umgesetzt wird.
> Arbeitssprache mit dem Nutzer: **Deutsch**. Code, Kommentare und Commit-Messages: **Englisch**.
> Stand: 2026-10-08 (v1 bis 1.7; v2: 1.8–2.0; v3: WP-11 → 2.1.0, Fixes 2.1.1/2.1.2; WP-12 → 2.2.0, Enum-Anzeigenamen 2.2.1; WP-13 → 2.3.0; WP-14 → 2.4.0; WP-15 → 3.0.0, v3 abgeschlossen; v4: WP-16 → 3.1.0, WP-17 → 3.2.0, WP-18 → 3.3.0, Leerzeichen nach Vorschlägen 3.3.1, Tabs umbenennen 3.4.0; WP-19 → 3.5.0; Stabilisierung R1 → 3.6.0, Protokolle nach `docs/work-packages.md`; Struktur-Refactoring R2 → 3.6.1; WP-20 → 3.7.0; WP-24 → 3.8.0; FK-Sprung mit mehreren Zeilen → 3.9.0; WP-27 Modell-Abgleich Typen/NULL/Längen → 3.10.0; WP-21 Formularansicht und Zeilenvergleich → 3.11.0, Umschalter-Zeilen offener Verbindungen 3.11.1; Bearbeiten von TIMESTAMP WITH [LOCAL] TIME ZONE, Dezimalkomma in Zeitstempeln → 3.12.0)

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
| **v4 – Komfort & SQL** | Modell-Cache, freier SQL-Editor, Skripte, LINQ-Autovervollständigung, Schema-Vergleich; geplant: Formularansicht, DDL im SQL-Editor, Tabellen-Designer (weitere Pakete aus dem Backlog nach Absprache) | WP-16 … WP-23 |
| **v5+** | Backlog (Abschnitt 10) | – |

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

Versionierung: SemVer, Git-Tag `vX.Y.Z` pro Release (der Push des Tags veröffentlicht das GitHub-Release – erst taggen, wenn der CHANGELOG-Abschnitt steht), `CHANGELOG.md` pflegen. Features der nächsten Version werden **nicht** vorgezogen, sondern in `docs/backlog.md` notiert.

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
| Grid | **AG Grid Community 34.3.1** (MIT) über JS-Interop, **Infinite Row Model** | Datenblöcke à 500 und Sortierung kommen aus .NET (`IDataAccess`), gekapselt in `FerretGrid` (Tabellen) bzw. `SqlResultGrid` (SQL/LINQ) über `GridBridge` + `wwwroot/js/grid.js`; Spalten als `GridColumn`, Rohzeilen der geladenen Blöcke in `RowBlocks`. Zellen gehen als fertig formatierte Strings über die Grenze (null = NULL), Spalten-IDs `c0`, `c1` … (Oracle-Namen dürfen Punkte enthalten). **Lokal im Repo** unter `FerretSharp.UI/wwwroot/lib/ag-grid/` (Herkunft/Hash in der README dort, kein CDN). Enterprise-Features (Kontextmenü, Zellbereich) nicht verwenden – eigene Lösungen in Blazor. |
| Layout | Tabs + Seitenleiste in Blazor | Kein Docking-Framework. |
| SQL-Anzeige | eigener Highlighter in Razor (`SqlCode`) | Editor für C# in der LINQ-Konsole: **Monaco 0.57** (ADR 0011, lokal unter `wwwroot/lib/monaco/`); auch im freien SQL-Editor (WP-17, mit SQL-Autovervollständigung aus .NET). |
| Oracle | `Oracle.ManagedDataAccess.Core` (23.x) | rein managed, kein Instant Client; **durchgängig async** mit `CancellationToken`. |
| Oracle-Version | Ziel **19c+**; 12.2 sollte funktionieren | `OFFSET/FETCH`, `ALL_TAB_IDENTITY_COLS` erst ab 12c. Kein ROWNUM-Fallback. |
| Tests | **xUnit v3** auf **Microsoft Testing Platform** + NSubstitute; Integration: **Testcontainers.Oracle** | Kein VSTest (`Microsoft.NET.Test.Sdk`/`xunit.runner.visualstudio` nicht verwenden). Image `gvenzl/oracle-free:23-slim-faststart`. Benötigt Docker. |
| Persistenz | JSON-Dateien (`System.Text.Json`) | Polymorphie über `[JsonPolymorphic]`/`[JsonDerivedType]`; keine `object`-Properties (werden zu `JsonElement`). |
| Secrets | Windows Credential Manager via **`Meziantou.Framework.Win32.CredentialManager`** | Implementierung liegt im **App**-Projekt (Windows-only), Core kennt nur `ISecretStore`. Passwörter nie im JSON. |
| Paketquellen | repo-lokales `nuget.config` (nur nuget.org) | Auf dem Entwicklungsrechner ist global zusätzlich eine DevExpress-Quelle eingerichtet; CPM verlangt dann Source Mapping. |
| CI | **GitHub Actions** (`.github/workflows/ci.yml`, seit 3.5.0) | Bei Push auf `main` und PRs: Linux-Job (Core und UI bauen, Unit- und Integrationstests mit Testcontainers; .NET-8-Runtime für den ModelHost), Windows-Job (ganze Solution mit `-warnaserror`, Unit-Tests – der Runner hat nur Windows-Container). |
| Distribution | `dotnet publish src/FerretSharp.App -c Release -r win-x64 --self-contained -o <ziel>` | Ordner-Deployment (kein Single-File). **GitHub-Releases** über `.github/workflows/release.yml`: Push eines Tags `vX.Y.Z` → prüft die Version gegen `Directory.Build.props`, baut, zippt (`FerretSharp-X.Y.Z-win-x64.zip`) und legt das Release mit dem Abschnitt aus `CHANGELOG.md` an; für bestehende Tags von Hand starten (`workflow_dispatch` mit Tag). Installer/Auto-Update (Velopack) = Backlog. |

**Nicht** verwenden: Entity Framework für den generischen Zugriff (kennt Schema nur über DbContext). EF-Integration ist ein späteres, optionales Feature (siehe Backlog).

## 4. Solution-Struktur

```
FerretSharp.slnx
├─ src/
│  ├─ FerretSharp.Core/                # net10.0 – reine Logik, KEIN WPF, KEINE Windows-only-APIs, baut unter Linux
│  │  ├─ Connections/                  # ConnectionProfile, OracleAddress, ConnectionStore, ISecretStore
│  │  ├─ Schema/                       # SchemaCache, TableSummary, TableDetails, ColumnInfo, ForeignKeyInfo, ISchemaReader
│  │  ├─ Query/                        # FilterCondition, FilterOperator, QueryBuilder, QuerySpec, QueryParameter, SortSpec, PageSpec
│  │  ├─ Data/                         # RowSet, RowKey, IDataAccess, RowBlocks   (v2: RowChange, ChangeTracker)
│  │  ├─ IO/                           # AtomicJsonFile (Schreiben über .tmp für alle JSON-Speicher)
│  │  ├─ Workspaces/                   # Workspace, WorkspaceStore, TabState
│  │  ├─ Forms/                        # Formularansicht einer Zeile (WP-21): RowForm, RowComparison
│  │  ├─ Compare/                      # Schema-Vergleich (WP-20): SchemaSnapshot, SchemaDiff, SchemaDdl, gespeicherte Vergleiche
│  │  └─ Oracle/                       # OracleSession, OracleSchemaReader, OracleDataAccess, OracleTypeMapper, OracleIdentifier
│  ├─ FerretSharp.UI/                  # net10.0, Razor Class Library – plattformneutral, KEIN WPF/Windows
│  │  ├─ Shell.razor                   # Root-Komponente: Layout, Seiten, Shortcuts (Leisten und Dialoge als eigene Komponenten)
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
├─ .github/workflows/               # ci.yml (Build + Tests), release.yml (Tag → GitHub-Release)
├─ tools/icon/New-AppIcon.ps1          # erzeugt das App-Icon: „FS“, F dunkel/S blau, kantige Buchstaben (eigene Formen, keine Schrift) auf runder heller Kachel; .ico mit 16–256 px
├─ docs/
│  ├─ decisions/                       # ADRs, eine Datei pro Entscheidung
│  ├─ images/                          # Screenshots der README (je hell/dunkel, Beispiel-DB) und social-preview.png
│  ├─ work-packages.md                 # Umsetzungsprotokolle der abgeschlossenen Pakete (Abschnitt 8)
│  └─ backlog.md
├─ README.md                          # Englisch (GitHub-Startseite); LICENSE (MIT)
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
record TabState(TableRef Table, TabMode Mode, FilterRows, AppliedFilters, Sorts, int? FirstVisibleRow) { PinnedColumns, OriginTab, Form }
```
- `PinnedColumns`: vom Nutzer angeheftete Spalten in Anheft-Reihenfolge, **pro Tab** (Entscheidung des Nutzers; ein FK-Sprung öffnet den neuen Tab ohne Pins). Der PK ist immer angeheftet und steht nicht in der Liste. Logik in `ColumnPinning` (Workspaces/): PK in Schema-Reihenfolge → angeheftete Spalten → Rest; gelöschte Spalten fallen beim Öffnen weg.
- `Form` (WP-21): Formular neben dem Grid offen, Breite, „Leere ausblenden“ – je Tab (Entscheidung des Nutzers); null, solange das Formular im Tab nie offen war.
- `OriginTab` (v1.6): Index des Tabs, aus dem ein FK-Sprung kam, für „Zurück“ (Alt+←). Zur Laufzeit hält `TableTab.Origin` die Referenz; ist der Ursprung geschlossen, führt „Zurück“ zum nächsten offenen Tab weiter hinten in der Sprungkette (`BackTarget`). „Vor“ (Alt+→, `TableTab.Forward`) wird nicht gespeichert; ein neuer Sprung aus einem Tab löscht dessen „Vor“ (wie im Browser). Mehrere Tabs derselben Tabelle zeigen im Titel die Kurzform ihrer Filter (`FilterSummary`).
- `SavedQuery` entfällt: Seit 3.2 werden SQL-Tabs (Text und Variablen) mit dem Workspace gespeichert.
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
- Verbindungsaufbau: Seit WP-24 können mehrere Verbindungen offen sein, eine davon sichtbar (`ConnectionHub` in UI/State: je offene Verbindung ein DI-Scope `ConnectionScope` mit eigener `ActiveConnection`, `WorkspaceManager`, `ClrModelManager`, `PresentationService`, `LinqConsoleService`; `Current`/`Previous`/`Shown`, `Idle` als Platzhalter ohne Verbindung). Je Verbindung öffnet `ActiveConnection` die Explorer-Session über `IDatabaseConnector`, lädt den `SchemaCache`, hängt die Workspaces an (`WorkspaceManager.AttachAsync`) und merkt die Nutzung in `recent.json` (`RecentConnections`). Trennen speichert die Workspaces und schließt alle Sessions. Oracle-Fehler kommen als `DatabaseException` mit ORA-Code an.

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
  - pro Flush einen `SAVEPOINT` → einzelne Flushes lassen sich zurücknehmen. Seit 3.6.0 sind Grid-Schreibvorgänge und SQL-/LINQ-Statements gemeinsam die `IDataEditor.Actions` der Transaktion; Undo nimmt immer die neueste zurück (ein Savepoint verwirft alles danach, auch fremde Statements).
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
- **Verbindungen** (es sind im Alltag 12+): oben links nur die sichtbare Verbindung als Umschalter. Seit WP-24 bleiben gewechselte Verbindungen im Hintergrund offen (Abschnitt „Offen“ im Umschalter mit „angezeigt“/„verbunden“/„N Aktionen offen“/„Verbindung verloren“ und „Trennen“); `Alt+O` springt zur vorigen. Nicht committete Arbeit im Hintergrund steht in der Statusleiste; eine im Hintergrund verlorene Verbindung wird beim Wechsel mit Banner gezeigt (neu verbunden erst über „Neu verbinden“). Klick oder `Ctrl+Shift+O` öffnet ein Popover mit Suche, „Zuletzt verwendet“ (ab WP-03), einklappbaren Gruppen und Pfeiltasten-/Enter-Bedienung; „⋯“ je Zeile → Bearbeiten, Duplizieren, Löschen (mit Bestätigung).
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
- Formularansicht (WP-21, Entscheidungen des Nutzers): eine Zeile als **Seitenleiste rechts neben dem Grid** (`RowFormPanel`, Breite ziehbar), der Vergleich markierter Zeilen als **Dialog** (`RowCompareDialog`, nur lesen, 2–20 Zeilen). **Das Grid führt:** Das Formular zeigt die Zeile der fokussierten Zelle (`FerretGrid.Focused`/`FocusChanged`), ▲ ▼ bewegen nur den Fokus im Grid (`focusRow` in grid.js) – Blöcke lädt AG Grid wie gewohnt, nach Schreiben/Neuladen findet das Formular die Zeile über `RowBlocks.IndexOf(RowKey)` wieder. Felder kommen aus `RowForm` (Core/Forms), Editieren über `FerretGrid.EditFromForm` (derselbe Kern wie `OnCellEdit`, synchron). Getippte, noch nicht bestätigte Werte übernimmt `ShellState.BeforeWrite` vor Ctrl+S/Commit. FK-Sprünge aus dem Formular nehmen den aktuellen (auch ausstehenden) Wert; eingehende FKs zählt `FkCounts` (gemeinsam mit dem Kontextmenü) erst, wenn die Zeile 400 ms gewählt bleibt. Spaltenkommentar nur als Tooltip.

**Shortcuts**

| Taste | Aktion | Version |
|---|---|---|
| Ctrl+Shift+O | Verbindungs-Umschalter öffnen | v1 |
| Alt+O | Zur vorigen offenen Verbindung wechseln (ohne Trennen) | WP-24 |
| Ctrl+Enter | Filter anwenden | v1 |
| Ctrl+C | Im Grid: Wert der fokussierten Zelle; bei mehreren markierten Zeilen diese als Tabelle (Tab-getrennt, mit Kopfzeile). Mit der Maus markierter Text innerhalb einer Zelle wird normal kopiert. Kopiert wird immer der volle Wert (`DelimitedExport.CellText`), nicht der gekürzte Anzeigetext. | v1.3 |
| F5 | Refresh (v2 in Read-only-Tx: neue Transaktion) | v1 |
| Ctrl+F | Datenansicht: Spalte suchen und hinspringen (Scrollen, Hervorheben, Fokus auf die Zelle der ersten sichtbaren Zeile); Strukturansicht: Spalten filtern (v1.6) | v1.5 |
| Alt+← / Alt+→ | Zurück zum Tab, aus dem ein FK-Sprung kam / wieder vor (verhindert nebenbei die Zurück-Navigation der WebView) | v1.6 |
| Ctrl+P | Tabelle suchen (Backlog) | – |
| Ctrl+S | Pending-Änderungen flushen (kein Commit) | v1.9 |
| Ctrl+Shift+Enter | Commit (auf Prod immer mit Bestätigung) | v1.9 |
| Ctrl+Shift+L | Neue LINQ-Konsole (mit verknüpftem C#-Projekt); im LINQ-Tab führen Ctrl+Enter und F5 aus, Ctrl+F sucht im Editor, Ctrl+Leertaste schlägt Member/Typen vor (3.5) | 2.3 |
| Ctrl+Shift+Q | Neuer SQL-Editor; im SQL-Tab führen Ctrl+Enter und F5 das Statement am Cursor aus, Ctrl+F sucht, Ctrl+Leertaste schlägt Tabellen/Spalten vor | 3.2 |
| Alt+X | Im SQL-Tab: das ganze Skript (bzw. die markierten Statements) nacheinander ausführen | 3.3 |
| Alt+Enter | Im Grid: Formular der fokussierten Zeile öffnen/schließen; bei mehreren markierten Zeilen: vergleichen. Im Formular: schließen. Nur lokal (nicht in `shortcuts.js`), damit Monaco seine Tasten behält | WP-21 |
| Alt+↑ / Alt+↓ | Im Formular: vorige/nächste Zeile (bewegt den Fokus im Grid; im Grid selbst reichen ↑/↓). Nur lokal | WP-21 |
| – | Rollback nur über Button, mit Bestätigung | v2 |

`Esc` bleibt dem Grid vorbehalten (Zelleingabe abbrechen) bzw. schließt Menüs/Dialoge. Globale Shortcuts registriert die `Shell` über `wwwroot/js/shortcuts.js` (Capture-Listener → `OnShortcut` in .NET); `F12` öffnet im Debug-Build die DevTools.

UI-Muster (seit R2):
- Dialoge und Bestätigungen fordern Komponenten über den kaskadierten `ShellState` an. Was mit Workspaces und ihren Änderungen passiert (Commit, Rollback, Sperren/Freischalten, Verbinden, Trennen, Löschen, Tab schließen, „vorher fragen“ über `GuardAsync`), liegt im ebenfalls kaskadierten `WorkspaceLifecycle`. Die Shell besteht aus `TabBar`, `StatusBar`, `WorkspaceBadge`, `ConnectionLostBanner` und `DialogHost`; diese bekommen das Lifecycle-Objekt als Parameter, damit sie mit der Shell neu rendern.
- Jeder Dialog nutzt `ModalFrame` (Hintergrund, Rolle, Esc, Klick daneben nur bei reinen Anzeige-Dialogen, Fokus).
- DB-Aufrufe aus Komponenten laufen über `Shell.RunDbAsync(Logger, Active.Profile, …)` (bzw. `CallDbAsync` auf dem UI-Thread). Das ergibt einheitlich `DbResult` mit Fehler (geloggt, Verbindungsverlust gemeldet), Ablehnung (`RefusedException` → Meldung) oder Abbruch (still). Fachliche Ablehnungen im Core sind `RefusedException`, Programmierfehler-Sperren bleiben `InvalidOperationException`.
- JSInvokable-Methoden werfen keine Ausnahmen an JS zurück (Blazor lässt die Task unbeobachtet → `[ERR]` im Log), sondern melden Fehler im Ergebnis (`GridPage.Failed`).
- Dienste einer Verbindung (`ActiveConnection`, `WorkspaceManager`, `ClrModelManager`, `PresentationService`, `LinqConsoleService`) sind **Scoped** und kommen als `[CascadingParameter]` über `ConnectionScopeView`, **nie per `@inject`** (sonst bekäme die Komponente die Instanz des WebView-Scopes, eine Geisterverbindung). Explorer und Tab-Seiten jeder offenen Verbindung stehen unter deren eigenem `ConnectionScopeView` und bleiben gemountet; Leisten, Dialoge und Seiten hängen an `Hub.Shown` und sind darauf gekeyt (Geschwister brauchen verschiedene Keys). Aktionen auf einem Workspace laufen über `ConnectionHub.OwnerOf(workspaceId)`.
- Komponenten ohne Parameter rendern bei Parent-Updates **nicht** neu → sie abonnieren `ShellState.Changed` bzw. `ConnectionManager.Changed`/`WorkspaceManager.Changed` selbst. Tab-Zustand, der kein Neurendern braucht (Tippen im Filter, Scrollen, Sortieren), meldet `ShellState.MarkDirty()`; der Lifecycle reicht dann die Tabs aller offenen Workspaces an `WorkspaceManager.UpdateTabs` weiter (speichert nur bei Änderung).

Blazor kennt **kein `auxclick`-Event**: `@onauxclick` wird kommentarlos als HTML-Attribut ausgegeben und tut nichts (so war Mittelklick-Schließen der Tabs seit WP-04 wirkungslos). Mittelklick über `@onmouseup` mit `e.Button == 1`.

**Kein `@ondblclick` verwenden.** Der WPF-Host (`WebView2CompositionControl`) reicht beim zweiten Klick eines echten Doppelklicks das Mouse-down doppelt an die WebView weiter: Chromium zählt `detail` 1 → 2 → 3, der zweite `click` kommt mit `detail=3`, und `dblclick` feuert nie. Doppelklick deshalb über `@onclick` mit `e.Detail >= 2` erkennen (Workspace-Chip, Verbindungszeile). Fiel lange nicht auf, weil die E2E-Tests Doppelklicks synthetisch bzw. per CDP direkt in die WebView schickten, also am Host vorbei. Gefunden durch den Nutzer in v1.1.0.

**Mausinteraktionen mit echter Windows-Eingabe prüfen** (`SendInput` über `realclick.ps1` im Scratchpad: `ClientToScreen` des Fensters + CSS-Position × `devicePixelRatio`), nicht nur per CDP. Nur so läuft die Eingabe durch den WPF-Host wie beim Nutzer. Dabei immer nur **eine** App-Instanz mit Debug-Port starten: Zwei Instanzen teilen sich das WebView2-Datenverzeichnis, und unterschiedliche Browser-Argumente (z. B. zwei Debug-Ports) lassen die zweite beim Start mit `0x8007139F` abstürzen. Vorher prüfen, dass der Klick ankommt (z. B. `mousedown`-Listener per CDP): Holt Windows das App-Fenster nicht in den Vordergrund (`SetForegroundWindow` wird verweigert, wenn der Nutzer gerade woanders arbeitet), landet der Klick im Fenster, das dort oben liegt – dann abbrechen statt weiterklicken (in WP-12 passiert). Screenshots (`PrintWindow`) enthalten die 31 px hohe Titelleiste: Bildkoordinaten ≠ CSS-Koordinaten, Klickziele immer per `getBoundingClientRect` bestimmen.

## 8. Arbeitspakete

Jedes Paket: eigener Branch `wp/NN-kurzname`, am Ende `dotnet build -warnaserror` + `dotnet test` grün, kurzer Eintrag in `docs/decisions/` bei nicht-trivialen Entscheidungen. Vor dem Start eines Pakets kurz den Plan nennen, dann umsetzen.

### Abgeschlossen

Umsetzungsprotokolle (was gebaut wurde, Entscheidungen des Nutzers, Nachträge, Fallen, E2E-Kniffe) stehen in **`docs/work-packages.md`** – dort nachlesen, bevor ein Bereich angefasst wird, den ein früheres Paket gebaut hat. Begründungen: `docs/decisions/`.

| Paket | Inhalt | Release | ADR |
|---|---|---|---|
| WP-01 | Solution-Gerüst, Host, Theme, Testprojekte | – | 0001–0004 |
| WP-02 | Connections, Credential Manager, `OracleSession` | – | – |
| WP-03 | Schema-Cache, Explorer, Buchstabenleiste | – | – |
| WP-04/04b | Grid (AG Grid), Paging, Filter, `QueryBuilder`; Views & Synonyme | – | – |
| WP-05 | Workspaces (eigene Session je Workspace, Persistenz) | – | 0005 |
| WP-06 | FK-Navigation (deklarierte FKs, neuer Tab je Sprung) | – | – |
| WP-07 | Export, Fehlerdialoge, Verbindungsabbruch | 1.0.0 | – |
| WP-08 | Transaktionsmodell, Read-only-Snapshots | 1.8.0 | 0006 |
| WP-09 | Editieren, `ChangeTracker`, Sperrkonflikte | 1.9.0 | 0007 |
| WP-10 | Prod-Freischaltung je Workspace, LOB-Editor | 2.0.0 | 0008 |
| WP-11 | C#-Projekt verknüpfen, ModelHost (Hilfsprozess) | 2.1.0 | 0009 |
| WP-12 | C#-Namen, Enum-Werte, Navigations als FK-Quelle | 2.2.0 | 0010 |
| WP-13 | LINQ-Konsole (SQL abfangen, FerretSharp führt aus) | 2.3.0 | 0011 |
| WP-14 | Explain-Plan (geschätzt/tatsächlich) | 2.4.0 | 0012 |
| WP-15 | Code-Generierung (LINQ, Initializer, HasData) | 3.0.0 | 0013 |
| WP-16 | Modell-Cache | 3.1.0 | – |
| WP-17 | Freier SQL-Editor | 3.2.0 | 0014 |
| WP-18 | Skript ausführen, Verbindung löschen; Tabs umbenennen | 3.3.0–3.4.0 | – |
| WP-19 | LINQ-Autovervollständigung | 3.5.0 | 0015 |
| R1 | Stabilisierung nach Code-Review, gemeinsamer Undo-Stapel | 3.6.0 | – |
| R2 | Struktur-Refactoring (DB-Aufrufe, Dialoge, Shell, Session, Grid-Brücke) | 3.6.1 | – |
| WP-20 | Schema-Vergleich: N Schemas als Matrix, gespeicherte Vergleiche, DDL-Vorschlag | 3.7.0 | – |
| WP-24 | Mehrere offene Verbindungen, eine sichtbar (Alt+O zur vorigen) | 3.8.0 | – |
| Klein | FK-Sprung mit mehreren markierten Zeilen (`in`-Filter) | 3.9.0 | – |
| WP-27 | Abgleich C#-Modell ↔ DB: Typ, NULL, Länge, Stellen (`ColumnTypeCheck`, Export-Format 2) | 3.10.0 | – |
| WP-21 | Formularansicht einer Zeile (Seitenleiste, editierbar über den `ChangeTracker`), Vergleich markierter Zeilen (Dialog) | 3.11.0 | – |

**Kontext des Nutzers** (wichtig für die kommenden Pakete):
- DB-first von Hand: erst die DB ändern, dann Entity/Konfiguration; keine Migrations. Namenskonvention im Code (Tabellen groß, `KundenId` → `KUNDEN_ID`), **eigene Value Converter** (bool ↔ J/N, Enum-Kürzel), Enum-Member mit `[Display(ResourceType = …, Name = …)]`. Ein DbContext in einer Klassenbibliothek (Konstruktor `DbContextOptions`), Entities in einem anderen Projekt, EF Core 8.
- Pro Datenbank ein eigener Klon des DbContext-Repos auf dem passenden Branch (Prod-DB → release, Test-DB → test, Dev-DB → dev), jeweils als C#-Projekt der Verbindung verknüpft; entwickelt wird in einem weiteren Klon.
- Größenordnung beim Nutzer: 425 Entities, ~4000 Properties, DB über Kunden-VPN; ohne Cache braucht der ModelHost ~12 s (meist `OnModelCreating`). Beziehungen ohne FK-Constraint sind häufig.
- Der Oracle-EF-Provider quotet alle Namen: Spalten mit gemischter Schreibweise im Modell (`Chargenr` gegen `CHARGENR`) ergeben ORA-00904.

**Fallen aus den bisherigen Paketen** (Einzelheiten in `docs/work-packages.md`):
- Komponenten mit JS-Interop prüfen nach jedem `await` im Start und in Handlern, ob sie schon abgebaut sind (`_disposed`), und rufen danach kein JS mehr auf (Muster: `MonacoEditor.CallAsync`, `GridBridge.CallAsync`). Ausnahmen in einer Tab-Ansicht fängt seit 3.6.0 `TabFrame` (ErrorBoundary je Tab); vorher schloss jede Ausnahme alle Tabs.
- `OracleSession` ist der einzige Besitzer der Connection: Dispose bricht das laufende Kommando ab und wartet auf das Gate; Wartende bekommen danach `OperationCanceledException`. Die Statement-Sperren (`StatementGuard`) nutzen den Tokenizer des SQL-Editors (`SqlScript.Tokenize`).
- AG Grid: Objekte in Column-Defs (`headerComponentParams`) werden mit `defaultColDef` **tief kopiert** – veränderliche Metadaten als Funktion (`getMeta`). Kopfhöhe nur als Theme-Parameter, nicht als `headerHeight` (setzt die Zeilenhöhe zurück). Angeheftete Zeilen haben `row-index="t-0"`.
- `GridBridge.CallAsync(…, params object?[] args)`: Eine Liste als einziges Argument immer als `List<…>` übergeben, **kein Array** – ein `GridRowUpdate[]` wird wegen Array-Kovarianz selbst zum params-Array, grid.js bekommt dann ein Objekt statt einer Liste (so zeichnete „Übernehmen“ im LOB-Dialog seit 3.6.1 die Zeile nicht neu; in WP-21 gefunden).
- Monaco: `vs/nls/lang/de.js` ist kein AMD-Modul → als normales Script laden. Offsets sind UTF-16; Text und Offsets immer aus demselben `getRunContext`.
- C#-Modell gegen die DB (WP-27) nur mit **konfigurierten** Facetten vergleichen: Die Provider-Vorgaben (`NVARCHAR2(2000)`, `NUMBER(10)` für `int`) beschreiben nicht die Absicht des Projekts und erzeugen Massen an Abweichungen. Für NULL `ColumnNullable` (EFs Spaltensicht) statt `Nullable` der Property (TPH, Owned).
- ModelHost: Roslyn im Projektprozess nur bis 4.11 (ab 4.12 `System.Reflection.Metadata` 9.0, nicht ladbar in .NET 8); `AppContext.BaseDirectory` ist unter `dotnet exec --depsfile` nicht verlässlich (`typeof(Program).Assembly.Location`); Satelliten-Assemblies stehen nicht in der deps.json; ein Interceptor-Exemplar für die ganze Lebensdauer. Roslyn sieht eine Position am Textende als hinter einem unfertigen Lambda.
- Dictionary über ein ganzes Schema (WP-20): Constraints gelöschter Tabellen bleiben im Papierkorb unter `BIN$…` in `ALL_CONSTRAINTS` → owner-weite Abfragen nach der Objektliste filtern. Eine `LONG`-Spalte (`DATA_DEFAULT`) zählt mit 32.767 Byte in `OracleDataReader.RowSize` → Fetch-Größe deckeln (`FetchManyRows`, 16 MB). `DEFAULT ON NULL` entfernen nimmt in Oracle 23 auch NOT NULL weg.
- `EXPLAIN PLAN` geht nicht in einer READ-ONLY-Transaktion (ORA-01456) → Explorer-Session. `ReadSqlAsync` blättert durch erneutes Ausführen (`SELECT * FROM (…)` scheitert an doppelten Spaltennamen der EF-Joins, ORA-00918).
- E2E: CDP-Klicks sind keine Nutzergeste (`navigator.clipboard.writeText` schlägt fehl) → Zwischenablage per CDP durch eine Mitschrift ersetzen. Text im LOB-Dialog geht erst beim `change` nach .NET → mit echter Maus übernehmen. Monaco über `getModels()…setValue`/`setPosition` füttern, `triggerSuggest` erst nach > 250 ms. `Input.dispatchKeyEvent`-Modifier: Alt 1, Ctrl 2, Meta 4, Shift 8. Ausdrücke mit Anführungszeichen über eine Datei an `node` geben (PowerShell 5.1 verliert sie). Nach E2E-Läufen das Log der Testinstanz auf `[ERR]` prüfen. Abfragen über `Kunde` im Beispielmodell scheitern absichtlich mit ORA-00904 (Drift).

### Geplant (v4)

Pakete aus dem Backlog, nach v3 mit dem Nutzer ausgewählt (2026-10-05). Versionen: Minor-Releases 3.x (nichts Inkompatibles).

#### WP-22 DDL im SQL-Editor (geplant)
Wunsch des Nutzers (2026-10-06): Tabellen anlegen und ändern, passend zum DB-first-Ablauf („erst DB ändern, dann Entity“). Erste Stufe: DDL im SQL-Editor (und in Skripten) zulassen. Entscheidungen des Nutzers:
- **Wo:** auf allen schreibbaren Workspaces – Profile ohne „Schreibgeschützt“ sowie **auch Prod nach dem Freischalten** (WP-10). Gesperrte Workspaces nie (READ-ONLY-Transaktion schützt nicht vor DDL, deshalb weiter Abweisung im Editor).
- **Ausführen:** FerretSharp führt das DDL aus (nicht nur erzeugen/kopieren).
- **Offene Transaktion:** Hat der Workspace eine Transaktion, wird eine **Meldung** ausgegeben und die **Transaktion abgebrochen** (Rollback der nicht committeten Änderungen), bevor das DDL läuft – sonst würde DDL sie still mitcommitten. Entschieden (2026-10-06): **Rollback nur nach Bestätigung** – der Dialog zeigt, was verworfen wird (ausstehende/geschriebene Grid-Änderungen, per SQL geänderte Zeilen); „Abbrechen“ führt das DDL nicht aus.
- Vorschlag zur Umsetzung (beim Start mit dem Nutzer abstimmen):
  - Eigener enger Weg an `OracleSession` (z. B. `ExecuteDdlAsync`, `internal`): nur ein einzelnes DDL-Statement (`SqlStatementKind.Ddl`), nur ohne offene Transaktion, nie in einer gesperrten Session; `IsWriteStatement` und die Leseschranke bleiben unverändert. `ReadOnlyTests` erweitern. ADR.
  - Weiterhin abgewiesen: PL/SQL-Blöcke, `ALTER SESSION/SYSTEM`, `COMMIT`/`ROLLBACK` und **`TRUNCATE`** (Entscheidung des Nutzers: löscht alle Zeilen ohne Rollback – bleibt abgewiesen).
  - Bestätigung vor jedem DDL (zeigt das Statement; auf Prod immer, mit Verbindungsname); in Skripten eine Bestätigung für alle DDL-Statements. DDL ist **nicht** rückgängig zu machen – im Dialog so sagen.
  - Danach Schema-Cache neu laden (Explorer, Spalten-Ansichten, Autovervollständigung) und das C#-Modell neu abgleichen; Hinweis auf neue „Spalten ohne Property“.
  - Verlauf: DDL-Statements wie DML aufnehmen.

#### WP-23 Tabellen-Designer + Entity aus Tabelle (geplant, nach WP-22)
- Spalten-Ansicht eines Tabs bearbeitbar: Spalte hinzufügen, Typ/Länge/Precision, NULL, Default, Kommentar ändern, umbenennen, löschen; PK, Unique, FK, Indizes; neue Tabelle anlegen. FerretSharp erzeugt das DDL und zeigt es **vor** dem Ausführen (ausführen über den Weg aus WP-22 oder nur kopieren, z. B. als Skript fürs Repo).
- Indizes setzen (Wunsch der Kollegen des Nutzers, 2026-10-07; bleibt in WP-23, Entscheidung des Nutzers): „Index anlegen“ in der Indizes-Ansicht (Spalten, optional UNIQUE), beim Hinweis „Fremdschlüssel ohne Index“ ein Klick „Index dafür anlegen“.
- Oracle-Fallen im Designer abfangen: NOT NULL auf Spalte mit NULL-Werten (vorher zählen), Typänderung gefüllter Spalten (oft nur über neue Spalte + Umkopieren), VARCHAR2 BYTE/CHAR-Semantik, Index für neue FKs vorschlagen (`IndexAdvice`), Identity/Default ON NULL.
- Verzahnung mit dem C#-Modell (Backlog-Idee „Entity aus Tabelle erzeugen“): nach der Änderung Property-Zeile bzw. Entity + `IEntityTypeConfiguration` im Stil des Projekts (Namenskonvention, J/N-Converter) zum Kopieren.


#### WP-25 Tastenkürzel einstellbar (geplant)
Wunsch der Kollegen des Nutzers (2026-10-07). Unabhängig von den anderen Paketen.
- Eigene Seite bzw. eigener Bereich in den Einstellungen (Entscheidung des Nutzers: aufgeräumt, nicht zwischen die übrigen Einstellungen): **alle** Kürzel als Liste – die änderbaren mit „Ändern“ (Taste drücken) und „Zurücksetzen“, die festen ausgegraut mit Hinweis (auch die wichtigsten von Monaco: Suchen, Vorschläge; Esc im Grid). Dient zugleich als Übersicht, welche Aktion welches Kürzel hat.
- Konflikte erkennen und melden; Kürzel, die Windows/WebView abfangen (`Alt+Shift` Sprachwechsel, `Ctrl+Alt` = AltGr, `Alt+F4`), ablehnen oder warnen.
- Speichern in `AppSettings`; `Shell` registriert die Kürzel aus den Einstellungen bei `shortcuts.js` (heute Konstanten in `Shell.razor`); Anzeige der Kürzel in Tooltips/Buttons (`<kbd>`) aus derselben Quelle.

#### WP-26 Audit-/Historientabellen aus einer Vorlage (geplant, nach WP-22/WP-23)
Wunsch der Kollegen des Nutzers (2026-10-07): beim Anlegen einer Tabelle die Historientabelle und den Trigger gleich mit erzeugen, für bestehende Tabellen nachziehen. Entscheidung des Nutzers: das Schema kommt aus einer **Vorlagendatei**, nicht fest aus dem Code – damit FerretSharp auch außerhalb seiner Firma passt.
- Eingebaute, dokumentierte Standardvorlage; in den Einstellungen ein Pfad zu einer eigenen Vorlage (z. B. im Repo der Firma); später evtl. je Verbindung überschreibbar.
- Vorlage = SQL mit Platzhaltern (Tabelle, Historientabelle, Spalten mit Typen, Listen für `:OLD.`/`:NEW.`, Wiederholung über die Spalten); eigene minimale Syntax statt einer Template-Bibliothek (neue Abhängigkeit bräuchte ein ADR). Die Firmenvorlage des Nutzers (Tabelle + Trigger) dient als Testfall.
- Nachziehen: Spalte in `KUNDEN` neu → fehlt in `KUNDEN_HIST`/Trigger veraltet → `ALTER` + `CREATE OR REPLACE TRIGGER` vorschlagen (Vergleichslogik aus WP-20 wiederverwenden). Passt zum Backlog-Eintrag „Audit-/Historientabellen: Unterschiede hervorheben“ (Muster 1).
- Trigger enthalten PL/SQL – WP-22 weist PL/SQL-Blöcke ab; für `CREATE [OR REPLACE] TRIGGER` aus der Vorlage braucht es eine bewusste, enge Ausnahme (beim Start von WP-22/26 mit dem Nutzer entscheiden, ADR).

## 9. Offene UX-Fragen
- Shortcut-Belegung für Commit/Rollback (Abschnitt 7) – vorläufig, Nutzerfeedback einholen.
- Workspaces: Standardname ist „Workspace N“ mit der kleinsten freien Nummer (nach Umbenennen von „Workspace 1“ heißt der nächste wieder „Workspace 1“). Shortcuts zum Wechseln (z. B. Ctrl+1…9) und eine Oberfläche für die Notizen fehlen noch.

## 10. Backlog (nach v3)

- Fuzzy-Suche in der Tabellenliste (Ctrl+P-Stil), Gruppierung nach Präfix.
- Keyset-Paging für sehr große Tabellen; exakte Scroll-Wiederherstellung.
- Verbindungsoptionen: TCPS/Wallet, Proxy-User, Kerberos/OS-Auth.
- Installer/Auto-Update (Velopack).
- Migrations-Cockpit (Pending Migrations, Schema-Diff Modell ↔ DB).
- Plugins als C#-Scripts (Roslyn). Auch von Kollegen des Nutzers gewünscht (2026-10-07) – **wartet auf konkrete Anwendungsfälle** (der Nutzer fragt nach); ohne sie keine Plugin-Schnittstelle (müsste stabil bleiben).
- Flyway (Kollegen des Nutzers, 2026-10-07; sie nutzen Flyway bereits, manche Eigenheiten nerven) – **wartet auf Details**, was genau stört. Vorschlag bisher: kein Nachbau des Migrationslaufs, sondern `flyway_schema_history` je Umgebung lesen (auch als Zeile im Schema-Vergleich) und DDL-Vorschläge/Designer-Ergebnisse als nächste `V…__….sql` im Repo-Ordner speichern.
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
- README-Screenshots (`docs/images/`, je `-light`/`-dark`): eigene Instanz mit Beispiel-DB und verknüpftem `samples`-Projekt, Fenster per `SetWindowPos` auf 1440×900 (ohne Aktivieren), `PrintWindow`, dann auf den Client-Bereich zuschneiden (x 8–1431, y 31–891 bei 100 % Skalierung: unsichtbare Ränder und Titelleiste weg). Theme über `--theme`, die App stellt Workspaces und Tabs nach dem Neustart wieder her (Ergebnisse neu ausführen). `docs/images/social-preview.png` (1280×640) lädt der Nutzer von Hand unter Settings → Social preview hoch (keine API).
- Statements per PowerShell-Pipe an `docker exec … sqlplus` bekommen ein BOM vorangestellt (SP2-0734) → `docker exec <name> bash -c "echo '…' | sqlplus …"` oder Skriptdatei per `docker cp`.
- Farbige Button-Varianten (`.btn.primary`, `.btn.danger-solid`) müssen im `:hover` ihren Hintergrund selbst setzen: `.btn:hover:not(:disabled)` setzt `--hover` und gewinnt sonst (Fehler in v1.0.0: Text beim Hover unlesbar). Text auf farbigen Flächen immer über Tokens (`--accent-text`, `--danger-text`), die im Dark Mode dunkel sind. Prüfen mit echtem Hover (`Input.dispatchMouseEvent` mouseMoved) und berechnetem Kontrast, nicht nur per Screenshot.
- CSS-Klassennamen in Komponenten nicht mit globalen Klassen kollidieren lassen (`.empty` ist der Leerzustand mit `position: absolute; inset: 0` – so überdeckte in WP-06 ein Menüeintrag das ganze Kontextmenü).
- E2E-Fallen: Nach einem Klick auf einen Tab ist `.page.active` noch kurz die alte Seite → auf etwas Spezifisches des Ziels warten. Synthetische Events erreichen Blazor, ersetzen aber keinen Test mit echter Eingabe (`Input.dispatchMouseEvent`/`dispatchKeyEvent`), sonst bleiben Fehler wie das fehlende `auxclick` unentdeckt. Im Infinite Row Model kennt das Grid anfangs nur ~501 Zeilen; `scrollTop` weiter unten wird abgeschnitten.
