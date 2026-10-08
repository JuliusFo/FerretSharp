# FerretSharp – Oracle explorer for .NET developers

> Projektanweisungen für Claude Code. Arbeitssprache mit dem Nutzer: **Deutsch**. Code, Kommentare und Commit-Messages: **Englisch**.
> Stand: 2026-10-08, Release 3.15.0 (Historie: `CHANGELOG.md`).

## Wo steht was

Diese Datei wird in jede Session geladen und bleibt deshalb kurz. Details liegen hier:

| Datei | Inhalt | Wann |
|---|---|---|
| `src/FerretSharp.Core/CLAUDE.md` | Oracle-Fallstricke, Fallen in Session, Dictionary, C#-Modell, ModelHost | lädt automatisch bei Arbeit im Core |
| `src/FerretSharp.UI/CLAUDE.md` | UI-Muster (R2), Blazor-/WebView-, AG-Grid-, Monaco- und CSS-Fallen | lädt automatisch bei Arbeit in der UI |
| `docs/architecture.md` | Domänenmodell im Detail (Verbindungen, Workspaces, Schema, Filter-/Query-Regeln, Row-Identität, Änderungen), UI-Konzept, Shortcuts | vor Änderungen am jeweiligen Bereich |
| `docs/work-packages.md` | Übersicht und Protokolle der abgeschlossenen Pakete (Entscheidungen des Nutzers, Nachträge, Fallen) | bevor ein Bereich angefasst wird, den ein Paket gebaut hat |
| `docs/roadmap.md` | Geplante Pakete mit den Entscheidungen des Nutzers, offene UX-Fragen | beim Start eines Pakets |
| `docs/backlog.md` | Nicht eingeplante Ideen | neue Ideen dort notieren |
| `docs/e2e-testing.md` | UI end-to-end prüfen (CDP, echte Maus, Test-DB, Screenshots) | vor jedem E2E-Lauf |
| `docs/decisions/` | ADRs | bei Stack- oder Architekturänderungen |

Neue Erkenntnisse dorthin schreiben, wo sie gebraucht werden: Fallen eines Bereichs in die `CLAUDE.md` des Projekts, Paketprotokolle nach `docs/work-packages.md`. Hier nur, was in jeder Session gelten muss.

## 1. Ziel

Ein eigener Datenbank-Editor für **Oracle**, der stärker auf den eigenen Arbeitsablauf zugeschnitten ist als DBeaver:

- **Workspaces** (Unter-Sessions) pro Verbindung, jeder mit eigener Oracle-Connection und Transaktion → zwei Workspaces können dieselbe Tabelle unabhängig ansehen und bearbeiten.
- **FK-Navigation** im Grid, **zusammenbaubare Filter** (TablePlus-Stil: Spalte, Operator, Wert → WHERE).
- **Sandbox-Editing**: alle Änderungen in einer Transaktion, Commit/Rollback explizit. Prod ist visuell markiert und standardmäßig read-only.
- **Brücke zur C#-/EF-Core-Welt**: FerretSharp kennt das Datenmodell des eigenen .NET-Projekts (DbContext): Entity-/Property-Namen, Enums mit C#-Namen, Navigations als Beziehungen, LINQ-Konsole mit generiertem SQL, Code-Generierung aus Daten.

Der Nutzer ist erfahrener .NET-Entwickler (Visual Studio, Blazor, SignalR). Erklärungen auf Senior-Niveau, keine Grundlagen.

## 2. Versionen und Invarianten

| Version | Inhalt | Pakete |
|---|---|---|
| **v1 – Read-only Browser** (bis 1.7) | Connections, Schema, Grid, Filter, Workspaces, FK-Navigation, Export | WP-01 … WP-07 |
| **v2 – Sandbox-Editing** (1.8–2.0) | Transaktionsmodell, Editieren, Commit/Rollback, Lock-Handling, Prod-Freischaltung | WP-08 … WP-10 |
| **v3 – .NET-Integration** (2.1–3.0) | DbContext-Modell, Schema-Anreicherung, LINQ-Konsole, Explain-Plan, Code-Generierung | WP-11 … WP-15 |
| **v4 – Komfort & SQL** (3.x) | Modell-Cache, SQL-Editor, Skripte, Schema-Vergleich, mehrere Verbindungen, Formularansicht; geplant: DDL, Tabellen-Designer (`docs/roadmap.md`) | WP-16 … |

**Lesen und Schreiben sind getrennt (ADR 0006):**
- Leseweg: `OracleSession.ExecuteReaderAsync` lehnt alles außer reinen Abfragen ab (`IsReadOnlyStatement`: nach Leerraum/Kommentaren `SELECT`/`WITH`, kein `FOR UPDATE`, nur ein Statement) – eine Stolperfalle gegen Programmierfehler, kein SQL-Parser.
- Schreibweg: `OracleSession.ExecuteNonQueryAsync` ist `internal`, nimmt nur ein einzelnes INSERT/UPDATE/DELETE (`IsWriteStatement`, nie DDL) und nur in einer offenen Transaktion. Transaktionssteuerung (Begin, Savepoint, Commit, Rollback) nur an `OracleSession`.
- `ReadOnlyTests` prüfen per Reflection, dass `IDataAccess`/`ISchemaReader`/`IDatabaseConnection` weder schreibende noch transaktionssteuernde Methoden anbieten und dass die Statements des `QueryBuilder` und alle SELECT/WITH-Texte in `Core/Oracle` die Lesesperre passieren; ein Integrationstest zeigt, dass ein `DELETE` abgewiesen wird.
- **Die Lesesperre nie aufweichen.** Ein neuer Weg (z. B. DDL in WP-22) bekommt eine eigene enge Methode, ein ADR und Tests.
- Keine Garantie auf Datenbankseite: Hat der DB-User Schreibrechte, könnte ein Fehler schreiben (ohne Transaktion committet ODP.NET sofort; `SET TRANSACTION READ ONLY` schützt nicht vor DDL). Empfehlung an den Nutzer: für Prod einen User mit reinen SELECT-Grants.

**C#-Modell als eigene Schicht:**
- Schema-Records bleiben reine DB-Sicht. Entity-/Property-Namen, CLR-Typen, Enum-Mappings kommen über die nach (Owner, Tabelle, Spalte) adressierte **Annotation-Schicht** (`ClrModelMapping`) – nicht durch Aufbohren von `TableDetails`/`ColumnInfo`.
- Anzeige und Eingabe von Zellwerten sowie Spaltenbeschriftungen laufen über die Präsentationsschicht (`TablePresentation`, in der UI `PresentationService`).
- FK-Quellen sind ein Enum (`Declared`, `Manual`, `Convention`, `ClrModel`), kein `bool IsVirtual`.
- Das Filtermodell (Spalte, Operator, Werte) bleibt so einfach, dass es sich 1:1 in einen LINQ-`Where`-Ausdruck übersetzen lässt.

**Versionierung:** SemVer, `CHANGELOG.md` pflegen, Git-Tag `vX.Y.Z` pro Release. Der Push des Tags veröffentlicht das GitHub-Release – erst taggen, wenn der CHANGELOG-Abschnitt steht. Features späterer Pakete nicht vorziehen, sondern in `docs/backlog.md` notieren.

## 3. Stack (entschieden – Änderungen nur per ADR in `docs/decisions/`)

| Bereich | Entscheidung | Hinweise |
|---|---|---|
| Runtime | **.NET 10 (LTS)**, C# 14 | SDK per `global.json` (`rollForward: latestFeature`). |
| Solution | **`FerretSharp.slnx`**, Central Package Management (`Directory.Packages.props`) | |
| UI | **Blazor Hybrid**: WPF-Host mit `BlazorWebView`, Komponenten in einer Razor Class Library | ADR 0004. App-TFM **`net10.0-windows10.0.19041.0`** (mit `net10.0-windows` stürzt BlazorWebView beim Start ab). |
| Theme | eigenes CSS mit Design-Tokens (`ferretsharp.css`), hell/dunkel über `prefers-color-scheme` | **Nicht verwenden:** WPF-UI, AvalonDock, CommunityToolkit.Mvvm, AvalonEdit, CSS-Frameworks. |
| UI-Zustand | Komponenten + schlanke State-/Service-Klassen in `FerretSharp.UI` | Kein MVVM-Framework, kein Docking-Framework. Lange Operationen async mit `CancellationToken`. |
| JS-Interop | ein ES-Modul pro Thema in `wwwroot/js` | JS bleibt dünn. **Keine Geschäftslogik in JS.** |
| Hosting/DI | `Microsoft.Extensions.Hosting` | Die BlazorWebView nutzt den Service Provider des Hosts. |
| Logging | `Microsoft.Extensions.Logging` + **Serilog** (Datei unter `%APPDATA%\FerretSharp\logs`) | Keine Bind-Werte von Prod-Verbindungen loggen (maskieren). |
| Grid | **AG Grid Community 34.3.1** (MIT), **Infinite Row Model**, lokal unter `wwwroot/lib/ag-grid/` | Blöcke à 500 und Sortierung aus .NET. Keine Enterprise-Features. |
| Editor | **Monaco 0.57** (ADR 0011), lokal unter `wwwroot/lib/monaco/` | LINQ-Konsole und SQL-Editor; SQL-Anzeige sonst über den eigenen Highlighter `SqlCode`. |
| Oracle | `Oracle.ManagedDataAccess.Core` (23.x), Ziel **19c+** (12.2 sollte gehen) | Rein managed, durchgängig async mit `CancellationToken`. `OFFSET/FETCH` und `ALL_TAB_IDENTITY_COLS` erst ab 12c, kein ROWNUM-Fallback. |
| Tests | **xUnit v3** auf **Microsoft Testing Platform** + NSubstitute; Integration: **Testcontainers.Oracle** (`gvenzl/oracle-free:23-slim-faststart`) | Kein VSTest (`Microsoft.NET.Test.Sdk`/`xunit.runner.visualstudio` nicht verwenden). |
| Persistenz | JSON-Dateien (`System.Text.Json`) über `AtomicJsonFile` | Polymorphie über `[JsonPolymorphic]`/`[JsonDerivedType]`; keine `object`-Properties (werden zu `JsonElement`). |
| Secrets | Windows Credential Manager via **`Meziantou.Framework.Win32.CredentialManager`** | Implementierung im **App**-Projekt, Core kennt nur `ISecretStore`. Passwörter nie im JSON. |
| Paketquellen | repo-lokales `nuget.config` (nur nuget.org) | Global ist zusätzlich eine DevExpress-Quelle eingerichtet; CPM verlangt dann Source Mapping. |
| CI | **GitHub Actions** (`.github/workflows/ci.yml`) | Linux: Core und UI bauen, Unit- und Integrationstests (.NET-8-Runtime für den ModelHost). Windows: ganze Solution mit `-warnaserror`, Unit-Tests. |
| Distribution | `dotnet publish src/FerretSharp.App -c Release -r win-x64 --self-contained -o <ziel>` | Ordner-Deployment. Release über `.github/workflows/release.yml`: Tag `vX.Y.Z` → Version gegen `Directory.Build.props` prüfen, bauen, zippen (`FerretSharp-X.Y.Z-win-x64.zip`), Release mit dem CHANGELOG-Abschnitt; für bestehende Tags per `workflow_dispatch`. |

**Nicht** verwenden: Entity Framework für den generischen Zugriff (kennt das Schema nur über einen DbContext).

## 4. Solution-Struktur

```
FerretSharp.slnx
├─ src/
│  ├─ FerretSharp.Core/        # net10.0 – reine Logik, KEIN WPF, KEINE Windows-only-APIs, baut unter Linux
│  │  ├─ Connections/ Schema/ Query/ Data/ Workspaces/ Settings/ IO/
│  │  ├─ Forms/                # Formularansicht, Zeilenvergleich (WP-21)
│  │  ├─ Compare/              # Schema-Vergleich, DDL-Vorschlag (WP-20)
│  │  ├─ ClrModel/             # C#-Modell, LINQ-Konsole, ModelHost-Aufruf (v3)
│  │  └─ Oracle/               # OracleSession, OracleSchemaReader, OracleDataAccess, OracleTypeMapper
│  ├─ FerretSharp.UI/          # net10.0, Razor Class Library – plattformneutral, KEIN WPF/Windows
│  │  ├─ Shell.razor, Components/, State/
│  │  └─ wwwroot/              # css/, js/ (ES-Module), lib/ag-grid, lib/monaco
│  ├─ FerretSharp.ModelHost/   # Hilfsprozess im Kontext des C#-Projekts des Nutzers (.NET 8, ADR 0009)
│  └─ FerretSharp.App/         # net10.0-windows10.0.19041.0 – schlanker WPF-Host (Generic Host, Serilog, Host-Dienste)
├─ tests/                      # Core.Tests und UI.Tests (ohne DB; UI.Tests prüfen die State-Klassen ohne Rendern, `TestApp`),
│                              # Integration.Tests (Testcontainers, überspringt ohne Docker)
├─ samples/                    # Beispielmodell (DbContext + Entities) zur Beispiel-DB
├─ tools/                      # icon/New-AppIcon.ps1 (App-Icon generiert, nicht von Hand bearbeiten), sample-db/New-SampleDb.ps1
└─ docs/                       # siehe „Wo steht was“; images/ für README-Screenshots
```

Regeln:
- `FerretSharp.Core` und `FerretSharp.UI` dürfen **kein** `System.Windows` und keine Windows-only-APIs referenzieren (CA1416). Was nur der Host kann (Credential Manager, native Datei-Dialoge, Fenster), definiert UI/Core als Interface; die Implementierung liegt in `FerretSharp.App`.
- Alles Oracle-Spezifische liegt hinter Interfaces (`ISchemaReader`, `IDataAccess`), damit Unit-Tests mit Mocks laufen.
- SQL-Strings entstehen ausschließlich in `Core/Query/` und `Core/Oracle/`. Komponenten in `FerretSharp.UI` enthalten kein SQL (Ausnahme: Anzeige eines erzeugten Statements).
- `QueryBuilder` kennt den Oracle-Treiber nicht: Er liefert eigene `QueryParameter`, das Mapping auf `OracleParameter` passiert in `Core/Oracle`.

## 5. Kernbegriffe (Details: `docs/architecture.md`)

- **Verbindung** (`ConnectionProfile`): Art (Prod/Test/Dev/Other → Farbe), Adresse (Host/Port oder TNS-Alias), User, optional Schema und Gruppe, „Schreibgeschützt“ (Prod voreingestellt). Seit WP-24 können mehrere Verbindungen offen sein, eine davon sichtbar: je Verbindung ein DI-Scope `ConnectionScope` im `ConnectionHub`.
- **Workspace**: eigene Session (eigene Connection und Transaktion, `Pooling=false`, MODULE/ACTION = FerretSharp/Workspace-Name), Tabs als `TabState`, Persistenz je Workspace als JSON. Das Schema lädt eine separate Explorer-Session (ADR 0005).
- **Schema**: `SchemaCache` lädt Tabellenliste und FKs beim Verbinden, Spalten/Keys lazy pro Tabelle; Synonyme zeigen auf das echte Objekt. `ALL_*`-Views immer nach `OWNER` filtern.
- **Query**: Werte nur als Bind-Variablen, jede gebundene Variable kommt im SQL vor, Spaltennamen gegen das Schema validiert und gequotet, immer deterministisch sortiert (PK → ROWID als Tiebreaker).
- **Row-Identität** (`RowKey`): PK, sonst ROWID; Views ohne PK sind dauerhaft read-only.
- **Änderungen**: Pending (nur im `ChangeTracker`) → Flushed (DML in der Session, Zeilen gesperrt, Savepoint je Flush) → Committed. Grid-Schreibvorgänge und SQL-/LINQ-Statements bilden einen gemeinsamen Undo-Stapel. Commit/Rollback nur auf expliziten Nutzerbefehl.

## 6. Arbeitsweise

- **Pakete:** eigener Branch `wp/NN-kurzname` (kleinere Themen `feat/…`, `fix/…`, `docs/…`); neue Arbeit immer von `main` abzweigen. Vor dem Start den Plan nennen, dann umsetzen. Ein Paket zu Ende bringen, bevor andere Themen angefasst werden; Ideen unterwegs nach `docs/backlog.md`. Bei Unsicherheit über UX-Details die einfachste Variante bauen und als Frage in der Zusammenfassung bzw. in `docs/roadmap.md` notieren, nicht blockieren.
- **Fertig heißt:** `dotnet build -warnaserror` und `dotnet test` grün, ADR bei nicht-trivialen Entscheidungen, Protokoll in `docs/work-packages.md`, Eintrag im `CHANGELOG.md`. Danach eine kurze Zusammenfassung auf Deutsch (was gebaut, was offen, was zu testen).
- **Commits** klein und thematisch, Conventional Commits (`feat:`, `fix:`, `test:`, `docs:`, `refactor:`).
- **Core zuerst mit Tests, dann UI.** Unit-Tests laufen ohne DB (Mocks). Alles, was eine echte DB braucht, gehört in `FerretSharp.Integration.Tests` und muss sich ohne Docker sauber überspringen.
- **Ordner rekursiv löschen nur über `SafeDelete.DirectoryBelow(root, path)`** (Core/IO: nur strikt unter einer festen Wurzel, nie Wurzel, Laufwerk oder relativer Pfad), in Tests über `TestFolder` (`tests/Shared`). Nie einen Pfad aus Einstellungen, Umgebungsvariablen oder Eingaben ungeprüft löschen; auch keine `Remove-Item -Recurse`/`rm -r`/`RemoveDir` in Skripten. `SafeDeleteTests` durchsucht Code, Tests und Skripte danach.
- **UI end-to-end prüfen, ohne echte Nutzerdaten anzufassen** (Ablauf: `docs/e2e-testing.md`):
  - App nur mit `--data-dir=<scratch>` starten; eigene Test-DB-Container per Name anlegen und wieder entfernen.
  - Der Container `ferret-sample` auf Port 1522 ist die Beispiel-DB des Nutzers – **nicht anfassen**.
  - Native Dialoge (Speichern unter) nicht per UI Automation bedienen: Sie öffnen sich in den echten Ordnern des Nutzers.
  - Mausinteraktionen mit echter Windows-Eingabe prüfen, nicht nur per CDP.

## 7. Kontext des Nutzers

- DB-first von Hand: erst die DB ändern, dann Entity/Konfiguration; keine Migrations. Namenskonvention im Code (Tabellen groß, `KundenId` → `KUNDEN_ID`), **eigene Value Converter** (bool ↔ J/N, Enum-Kürzel), Enum-Member mit `[Display(ResourceType = …, Name = …)]`. Ein DbContext in einer Klassenbibliothek (Konstruktor `DbContextOptions`), Entities in einem anderen Projekt, EF Core 8.
- Pro Datenbank ein eigener Klon des DbContext-Repos auf dem passenden Branch (Prod-DB → release, Test-DB → test, Dev-DB → dev), jeweils als C#-Projekt der Verbindung verknüpft; entwickelt wird in einem weiteren Klon.
- Größenordnung: 425 Entities, ~4000 Properties, DB über Kunden-VPN; ohne Cache braucht der ModelHost ~12 s (meist `OnModelCreating`). Beziehungen ohne FK-Constraint sind häufig. Im Alltag 12+ Verbindungen.
- Der Oracle-EF-Provider quotet alle Namen: Spalten mit gemischter Schreibweise im Modell (`Chargenr` gegen `CHARGENR`) ergeben ORA-00904.

## 8. Als Nächstes (Details und Entscheidungen des Nutzers: `docs/roadmap.md`)

- **WP-22** DDL im SQL-Editor (auf schreibbaren Workspaces, Rollback offener Transaktionen nur nach Bestätigung, `TRUNCATE` bleibt abgewiesen).
- **WP-23** Tabellen-Designer + Entity aus Tabelle, Indizes anlegen (nach WP-22).
- **WP-25** Tastenkürzel einstellbar (unabhängig).
- **WP-26** Audit-/Historientabellen aus einer Vorlagendatei (nach WP-22/23).
