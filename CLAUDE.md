# FerretSharp – Oracle explorer for .NET developers

> Projektanweisungen für Claude Code. Arbeitssprache mit dem Nutzer: **Deutsch**. Code, Kommentare und Commit-Messages: **Englisch**.
> Stand: 2026-10-14, zuletzt released 3.32.1 (Historie: `CHANGELOG.md`).

## Wo steht was

Diese Datei lädt in jede Session und enthält nur, was immer gilt. Die Fallen eines Bereichs stehen in `.claude/rules/` und laden, sobald eine passende Datei gelesen oder bearbeitet wird (`paths` im Kopf jeder Regel): `oracle`, `oracle-transactions`, `plsql-dictionary`, `ddl`, `clr-model`, `connection-transfer`, `ui`, `ui-grid`, `ui-monaco`, `ui-css`, `tests`, `localization`. Neue Fallen dort eintragen, wo sie gebraucht werden; eine neue Regel nur mit gültigem `paths`-Kopf (ohne lädt sie immer).

| Datei | Lesen … |
|---|---|
| `docs/architecture.md` – Domänenmodell, Query-Regeln, Änderungen, UI-Konzept, Shortcuts | vor Änderungen am jeweiligen Bereich |
| `docs/work-packages/` – Index mit Bereichen, je Paket ein Protokoll mit den Entscheidungen des Nutzers | bevor ein Bereich angefasst wird: im Index suchen, dann nur diese Datei |
| `docs/roadmap.md` (Geplantes, Labels, UX-Fragen), `docs/backlog.md` (Ideen) | beim Start eines Pakets; neue Ideen in den Backlog |
| `docs/release.md` – CI, Versionierung, Velopack | vor Änderungen an CI/Version und vor jedem Release |
| `docs/e2e-testing.md`, `docs/localization.md`, `docs/audit-templates.md` | vor E2E-Läufen, neuen Texten, Änderungen an `Core/Audit` |
| `docs/decisions/` – ADRs | bei Stack- oder Architekturänderungen |

## 1. Ziel

Ein eigener Datenbank-Editor für **Oracle**, stärker auf den eigenen Arbeitsablauf zugeschnitten als DBeaver:
- **Workspaces** pro Verbindung, jeder mit eigener Oracle-Connection und Transaktion.
- **FK-Navigation** im Grid, **zusammenbaubare Filter** (TablePlus-Stil: Spalte, Operator, Wert → WHERE).
- **Sandbox-Editing**: alle Änderungen in einer Transaktion, Commit/Rollback explizit. Prod ist visuell markiert und standardmäßig read-only.
- **Brücke zur C#-/EF-Core-Welt**: FerretSharp kennt das Datenmodell des eigenen .NET-Projekts (DbContext): Entity-/Property-Namen, Enums, Navigations als Beziehungen, LINQ-Konsole, Code-Generierung.

Der Nutzer ist erfahrener .NET-Entwickler (Visual Studio, Blazor, SignalR). Erklärungen auf Senior-Niveau, keine Grundlagen. Versionen v1–v4 und alle Pakete: `docs/work-packages/README.md`.

## 2. Invarianten

**Lesen und Schreiben sind getrennt (ADR 0006):**
- Leseweg: `OracleSession.ExecuteReaderAsync` lehnt alles außer reinen Abfragen ab (`IsReadOnlyStatement`: nach Leerraum/Kommentaren `SELECT`/`WITH`, kein `FOR UPDATE`, nur ein Statement) – eine Stolperfalle gegen Programmierfehler, kein SQL-Parser.
- Schreibweg: `OracleSession.ExecuteNonQueryAsync` ist `internal`, nimmt nur ein einzelnes INSERT/UPDATE/DELETE/MERGE (`IsWriteStatement`, nie DDL) und nur in einer offenen Transaktion. Transaktionssteuerung (Begin, Savepoint, Commit, Rollback) nur an `OracleSession`.
- Schemaweg (WP-22, ADR 0019): `OracleSession.ExecuteDdlAsync` ist `internal`, nimmt nur ein einzelnes DDL-Statement (`IsDdlStatement`: kein `TRUNCATE` und nichts, was wie TRUNCATE Zeilen unwiderruflich entfernt – Partitionen leeren/löschen, `FLASHBACK TABLE … TO`, `PURGE` –, keine Binds, kein PL/SQL außer Triggern auf Tabellen/Views, ADR 0021) und nur **ohne** offene Transaktion, also nie in einer gesperrten Session. Den Rollback vorher macht die UI nach Bestätigung.
- `ReadOnlyTests` halten das fest (Interfaces ohne Schreib-/Transaktionsmethoden, Schreib- und Schemaweg `internal`, alle SELECT/WITH-Texte passieren die Lesesperre). **Die Lesesperre nie aufweichen** – ein neuer Weg (z. B. PL/SQL ausführen) bekommt eine eigene enge Methode, ein ADR und Tests.
- Keine Garantie auf Datenbankseite (ODP.NET committet ohne Transaktion sofort; `SET TRANSACTION READ ONLY` hält weder DDL noch `PRAGMA AUTONOMOUS_TRANSACTION` auf) → Empfehlung an den Nutzer: für Prod einen User mit reinen SELECT-Grants.
- Logs und SQL-Verlauf enthalten auf Prod nie Werte (Bind-Werte maskiert) und nirgends Passwörter (`SqlRedaction`); die Workspace-Dateien halten den Arbeitsstand samt Werten (Entscheidung des Nutzers, `docs/architecture.md` 1.2).

**C#-Modell als eigene Schicht:**
- Schema-Records bleiben reine DB-Sicht. Entity-/Property-Namen, CLR-Typen, Enum-Mappings kommen über die nach (Owner, Tabelle, Spalte) adressierte **Annotation-Schicht** (`ClrModelMapping`) – nicht durch Aufbohren von `TableDetails`/`ColumnInfo`.
- Anzeige und Eingabe von Zellwerten sowie Spaltenbeschriftungen laufen über die Präsentationsschicht (`TablePresentation`, in der UI `PresentationService`).
- FK-Quellen sind ein Enum (`Declared`, `Manual`, `Convention`, `ClrModel`), kein `bool IsVirtual`.
- Das Filtermodell (Spalte, Operator, Werte) bleibt so einfach, dass es sich 1:1 in einen LINQ-`Where`-Ausdruck übersetzen lässt.

**Versionierung:** SemVer, `CHANGELOG.md` pflegen, Tag `vX.Y.Z` je Release – der Push des Tags veröffentlicht das Release, also erst taggen, wenn der CHANGELOG-Abschnitt steht (`docs/release.md`). Features späterer Pakete nicht vorziehen, sondern in `docs/backlog.md` notieren.

## 3. Stack (entschieden – Änderungen nur per ADR in `docs/decisions/`)

| Bereich | Entscheidung | Hinweise |
|---|---|---|
| Runtime | **.NET 10 (LTS)**, C# 14 | SDK per `global.json` (`rollForward: latestFeature`). |
| Solution | **`FerretSharp.slnx`**, Central Package Management (`Directory.Packages.props`) | Repo-lokales `nuget.config` (nur nuget.org); global ist zusätzlich eine DevExpress-Quelle eingerichtet, CPM verlangt dann Source Mapping. |
| UI | **Blazor Hybrid**: WPF-Host mit `BlazorWebView`, Komponenten in einer Razor Class Library (ADR 0004) | App-TFM **`net10.0-windows10.0.19041.0`** (mit `net10.0-windows` stürzt BlazorWebView beim Start ab). |
| Theme, UI-Zustand | eigenes CSS mit Design-Tokens (`ferretsharp.css`); Komponenten + schlanke State-/Service-Klassen | **Nicht verwenden:** WPF-UI, AvalonDock, CommunityToolkit.Mvvm, AvalonEdit, CSS-Frameworks, MVVM- oder Docking-Frameworks. |
| JS-Interop | ein ES-Modul pro Thema in `wwwroot/js` | JS bleibt dünn. **Keine Geschäftslogik in JS.** |
| Hosting, Logging | `Microsoft.Extensions.Hosting`; **Serilog** (Datei unter `%APPDATA%\FerretSharp\logs`) | Die BlazorWebView nutzt den Service Provider des Hosts. Keine Bind-Werte von Prod loggen. |
| Grid, Editor | **AG Grid Community 34.3.1** (Infinite Row Model), **Monaco 0.57** (ADR 0011), beide lokal | Keine AG-Grid-Enterprise-Features. |
| App-Mitschnitt | **EventPipe** + `TraceEvent`, nur in `FerretSharp.AppTrace` (ADR 0023) | Feste Feldliste der EF-Events (`EfEvents`): nie Connection-String, nie Parameter. |
| Oracle | `Oracle.ManagedDataAccess.Core` (23.x), Ziel **19c+** (12.2 sollte gehen) | Rein managed, async mit `CancellationToken`. `OFFSET/FETCH` ab 12c, kein ROWNUM-Fallback. |
| Tests | **xUnit v3** auf **Microsoft Testing Platform** + NSubstitute; **Testcontainers.Oracle** | Kein VSTest. |
| Persistenz | JSON (`System.Text.Json`): schreiben über `AtomicJsonFile`, lesen über `VersionedJsonFile` | Regel je Datei: `docs/architecture.md` 1.7. Polymorphie über `[JsonPolymorphic]`; keine `object`-Properties (werden zu `JsonElement`). |
| Texte, Secrets | typisierte resx, Englisch (Standard) und Deutsch (ADR 0017); Windows Credential Manager | Credential Manager nur im **App**-Projekt, Core kennt `ISecretStore`. Passwörter nie im JSON. |
| CI, Distribution | GitHub Actions; **Velopack** (ADR 0024) | `docs/release.md`. |

**Nicht** verwenden: Entity Framework für den generischen Zugriff (kennt das Schema nur über einen DbContext).

## 4. Solution-Struktur

```
src/FerretSharp.Core/       net10.0 – reine Logik, KEIN WPF, KEINE Windows-only-APIs, baut unter Linux
  Oracle/ Query/ Schema/ Data/ Connections/ (Transfer/: Import/Export) Workspaces/ Settings/ IO/ Resources/
  Ddl/ (DDL-Text) Design/ (Designer) Compare/ Audit/ Display/ (Anzeigespalten) Forms/ AppTrace/ ClrModel/
src/FerretSharp.UI/         net10.0 Razor Class Library, plattformneutral: Shell.razor, Components/, State/, Resources/,
                            wwwroot/ (css, js als ES-Module, lib/ag-grid, lib/monaco)
src/FerretSharp.ModelHost/  Hilfsprozess im Kontext des C#-Projekts des Nutzers (.NET 8, ADR 0009)
src/FerretSharp.AppTrace/   an eine laufende App anhängen (EventPipe, ADR 0023), plattformneutral
src/FerretSharp.App/        net10.0-windows10.0.19041.0 – schlanker WPF-Host (Generic Host, Serilog, Host-Dienste)
tests/                      Core.Tests, UI.Tests (ohne DB), Integration.Tests (Testcontainers), Stand-ins
                            FakeModelHost, FakeEfApp, FakeEfModel; Shared/
samples/                    Beispielmodell (DbContext + Entities) zur Beispiel-DB
tools/                      icon/New-AppIcon.ps1 (Icon generiert, nicht von Hand), sample-db/, coverage/
```
Welches Paket welchen Ordner gebaut hat: Spalte „Bereiche“ in `docs/work-packages/README.md`.

Regeln:
- `FerretSharp.Core` und `FerretSharp.UI` dürfen **kein** `System.Windows` und keine Windows-only-APIs referenzieren (CA1416). Was nur der Host kann (Credential Manager, native Datei-Dialoge, Fenster, Updates), definiert UI/Core als Interface; die Implementierung liegt in `FerretSharp.App`.
- Alles Oracle-Spezifische liegt hinter Interfaces (`ISchemaReader`, `IDataAccess`, `IDataEditor`), damit Unit-Tests mit Mocks laufen.
- SQL-Strings entstehen ausschließlich in `Core/Query/` und `Core/Oracle/`, DDL-Text nur in `Core/Ddl/` (`OracleDdl`; Vergleich, Designer und Audit bauen darauf, nur der Trigger-Text einer Audit-Vorlage kommt vom Nutzer). Komponenten in `FerretSharp.UI` enthalten kein SQL (Ausnahme: Anzeige eines erzeugten Statements).
- `QueryBuilder` kennt den Oracle-Treiber nicht: Er liefert eigene `QueryParameter`, das Mapping auf `OracleParameter` passiert in `Core/Oracle`.

## 5. Kernbegriffe (Details: `docs/architecture.md`)

- **Verbindung** (`ConnectionProfile`): Art (Prod/Test/Dev/Other → Farbe), Adresse, User, „Schreibgeschützt“ (Prod voreingestellt). Mehrere offen, eine sichtbar: je Verbindung ein DI-Scope `ConnectionScope` im `ConnectionHub`.
- **Workspace**: eigene Session (Connection und Transaktion, `Pooling=false`, MODULE/ACTION = FerretSharp/Workspace-Name), Tabs als `TabState`, Persistenz als JSON; das Schema lädt eine separate Explorer-Session (ADR 0005).
- **Query**: Werte nur als Bind-Variablen (jede kommt im SQL vor), Spaltennamen gegen das Schema validiert und gequotet, immer deterministisch sortiert (PK → ROWID). Row-Key: durchgesetzter PK, sonst ROWID; Views nur lesbar.
- **Änderungen**: Pending (`ChangeTracker`) → Flushed (DML in der Session, Zeilen gesperrt, Savepoint je Flush) → Committed, nur auf Nutzerbefehl. Grid und SQL/LINQ teilen einen Undo-Stapel (ADR 0018); DDL läuft außerhalb jeder Transaktion (ADR 0019).

## 6. Arbeitsweise

- **Pakete:** eigener Branch `wp/NN-kurzname` (kleinere Themen `feat/…`, `fix/…`, `docs/…`); neue Arbeit immer von `main` abzweigen. Vor dem Start den Plan nennen, dann umsetzen. Ein Paket zu Ende bringen, bevor andere Themen angefasst werden; Ideen unterwegs nach `docs/backlog.md`. Bei Unsicherheit über UX-Details die einfachste Variante bauen und als Frage in der Zusammenfassung bzw. in `docs/roadmap.md` notieren, nicht blockieren.
- **Fertig heißt:** `dotnet build -warnaserror` und `dotnet test` grün, jeder sichtbare Text in Englisch und Deutsch, ADR bei nicht-trivialen Entscheidungen, Protokoll in `docs/work-packages/` (eigene Datei, Zeile im Index), Eintrag im `CHANGELOG.md`. Danach eine kurze Zusammenfassung auf Deutsch (was gebaut, was offen, was zu testen).
- **Commits** klein und thematisch, Conventional Commits (`feat:`, `fix:`, `test:`, `docs:`, `refactor:`).
- **Core zuerst mit Tests, dann UI.** Unit-Tests laufen ohne DB (Mocks). Alles, was eine echte DB braucht, gehört in `FerretSharp.Integration.Tests` und muss sich ohne Docker sauber überspringen.
- **Ordner rekursiv löschen nur über `SafeDelete.DirectoryBelow(root, path)`** (Core/IO: nur strikt unter einer festen Wurzel, nie Wurzel, Laufwerk oder relativer Pfad), in Tests über `TestFolder` (`tests/Shared`). Nie einen Pfad aus Einstellungen, Umgebungsvariablen oder Eingaben ungeprüft löschen; auch keine `Remove-Item -Recurse`/`rm -r`/`RemoveDir` in Skripten. `SafeDeleteTests` durchsucht Code, Tests und Skripte danach.
- **UI end-to-end prüfen, ohne echte Nutzerdaten anzufassen** (Ablauf: `docs/e2e-testing.md`):
  - App nur mit `--data-dir=<scratch>` starten; eigene Test-DB-Container per Name anlegen und wieder entfernen.
  - Der Container `ferret-sample` auf Port 1522 ist die Beispiel-DB des Nutzers – **nicht anfassen** (`New-SampleDb.ps1` nie ohne `-Name`).
  - Native Dialoge (Speichern unter) nicht per UI Automation bedienen: Sie öffnen sich in den echten Ordnern des Nutzers.
  - Mausinteraktionen mit echter Windows-Eingabe prüfen, nicht nur per CDP.

## 7. Kontext des Nutzers

- DB über Kunden-VPN (Roundtrips teuer, Verbindungen werden still), im Alltag 12+ Verbindungen.
- Eigenes .NET-Projekt mit EF Core 8, DB-first von Hand, Beziehungen ohne FK-Constraint häufig, 425 Entities – Details: `.claude/rules/clr-model.md`.

## 8. Als Nächstes

- Gesamt-Review nach 3.31.0 (`docs/review-3.31.0.md`) abgeschlossen: Teil A als R5a in 3.31.1, Teil C (Doku), Teil B als R5b in 3.32.0. 3.32.1 hebt `System.Security.Cryptography.Pkcs` (kommt mit den Oracle-Paketen) direkt auf 8.0.2 – die direkte Referenz kann weg, sobald ein Oracle-Paket selbst 8.0.2 oder neuer mitbringt (WP-36).
- Danach WP-36 (Abhängigkeiten), dann WP-37 … WP-47 in der Reihenfolge aus `docs/roadmap.md` (ausgewählt mit dem Nutzer am 2026-10-14).
