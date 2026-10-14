# Arbeitspakete – Index

Was in den abgeschlossenen Paketen gebaut und entschieden wurde, mit Nachträgen, Fallen und E2E-Erkenntnissen: je Paket eine Datei (bis 3.31.1 eine gemeinsame `docs/work-packages.md`, im Gesamt-Review 3.31.0 aufgeteilt). Vor Änderungen an einem Bereich das Paket lesen, das ihn gebaut hat – die Spalte „Bereiche“ führt vom Code zum Paket. Geplante Pakete: `docs/roadmap.md`; übergreifende Fallen: `.claude/rules/`, `docs/e2e-testing.md`. Begründungen der Entscheidungen: `docs/decisions/`.

„Abschnitt N“ in älteren Protokollen meint die `CLAUDE.md` zum Zeitpunkt des Pakets (bis 3.14.0: 5 Domänenmodell und 7 UI-Konzept → `docs/architecture.md`, 6 Oracle-Fallstricke → `.claude/rules/oracle.md` (bis 3.31.1 `src/FerretSharp.Core/CLAUDE.md`), 11 Arbeitsweise → `CLAUDE.md` bzw. `docs/e2e-testing.md`). „Oben“/„unten“ in einem Protokoll meint die frühere gemeinsame Datei; Pakete werden über ihre Nummer genannt.

## Versionen

| Version | Inhalt | Pakete |
|---|---|---|
| **v1 – Read-only Browser** (bis 1.7) | Connections, Schema, Grid, Filter, Workspaces, FK-Navigation, Export | WP-01 … WP-07 |
| **v2 – Sandbox-Editing** (1.8–2.0) | Transaktionsmodell, Editieren, Commit/Rollback, Lock-Handling, Prod-Freischaltung | WP-08 … WP-10 |
| **v3 – .NET-Integration** (2.1–3.0) | DbContext-Modell, Schema-Anreicherung, LINQ-Konsole, Explain-Plan, Code-Generierung | WP-11 … WP-15 |
| **v4 – Komfort & SQL** (3.x) | Modell-Cache, SQL-Editor, Skripte, Schema-Vergleich, mehrere Verbindungen, Formularansicht, Tastenkürzel, PL/SQL ansehen, Änderungsübersicht mit Undo/Redo, DDL im SQL-Editor, Tabellen-Designer mit Entity aus Tabelle, Audit-Tabellen und Trigger aus einer Vorlage, Anzeigespalten (Namen neben FK-Werten), Statements der laufenden App, Installer, Tabs anheften, Verbindungen importieren | WP-16 … WP-35 |
| **Stabilisierung** | Fehlerbehebung und Struktur nach Reviews (a = Fehler, Patch-Release; b = Struktur) | R1 … R5 |

- v3: Die Arbeitspakete werden zu Beginn von v3 mit dem Nutzer verfeinert. Grober Zuschnitt: WP-11 … WP-15.
- v4: Pakete aus dem Backlog, nach v3 mit dem Nutzer ausgewählt (2026-10-05). Versionen: Minor-Releases 3.x (nichts Inkompatibles).

## Übersicht

Reihenfolge wie die Releases. Kleinere Releases ohne eigenes Paket (Fixes, Komfort) stehen nur im `CHANGELOG.md`.

| Paket | Datei | Inhalt | Bereiche | Release | ADR |
|---|---|---|---|---|---|
| WP-01 | [v1.md](v1.md) | Solution-Gerüst, Host, Theme, Testprojekte | `App` (Host, Serilog), `ferretsharp.css` | – | 0001–0004 |
| WP-02 | [v1.md](v1.md) | Connections, Credential Manager, `OracleSession` | Core/Connections, Oracle/`OracleSession`, App/`ISecretStore` | – | – |
| WP-03 | [v1.md](v1.md) | Schema-Cache, Explorer, Buchstabenleiste | Core/Schema (`SchemaCache`), Oracle/`OracleSchemaReader`, UI `Explorer` | – | – |
| WP-04/04b | [v1.md](v1.md) | Grid (AG Grid), Paging, Filter, `QueryBuilder`; Views & Synonyme | Core/Query (`QueryBuilder`, `FilterRules`, seit R5b `ValueInput`), Oracle/`OracleDataAccess`, UI `FerretGrid`/`grid.js` | – | – |
| WP-05 | [v1.md](v1.md) | Workspaces (eigene Session je Workspace, Persistenz) | Core/Workspaces (`WorkspaceManager`, `WorkspaceStore`), UI `WorkspaceBar` | – | 0005 |
| WP-06 | [v1.md](v1.md) | FK-Navigation (deklarierte FKs, neuer Tab je Sprung) | Core/Query (`FkNavigation`), UI Kontextmenü | – | – |
| WP-07 | [v1.md](v1.md) | Export, Fehlerdialoge, Verbindungsabbruch | Core/Data (`DelimitedExport`), Query/`InsertExport`, `DatabaseException.IsConnectionLost`, UI Banner | 1.0.0 | – |
| WP-08 | [wp-08-transactions.md](wp-08-transactions.md) | Transaktionsmodell, Read-only-Snapshots | Oracle/`OracleSession` (Transaktionen), Core/Connections | 1.8.0 | 0006 |
| WP-09 | [wp-09-editing.md](wp-09-editing.md) | Editieren, `ChangeTracker`, Sperrkonflikte | Core/Data (`ChangeTracker`), Oracle/`OracleDataEditor`, Query/`DmlBuilder`, UI Grid | 1.9.0 | 0007 |
| WP-10 | [wp-10-unlock-lobs.md](wp-10-unlock-lobs.md) | Prod-Freischaltung je Workspace, LOB-Editor | Core/Data (`LobContent`), UI `WorkspaceBadge`, LOB-Dialog | 2.0.0 | 0008 |
| WP-11 | [wp-11-clr-model.md](wp-11-clr-model.md) | C#-Projekt verknüpfen, ModelHost (Hilfsprozess) | Core/ClrModel (`ClrModelManager`), `ModelHost` | 2.1.0 | 0009 |
| WP-12 | [wp-12-model-enrichment.md](wp-12-model-enrichment.md) | C#-Namen, Enum-Werte, Navigations als FK-Quelle | Core/ClrModel (`ClrModelMapping`, `TablePresentation`), UI `PresentationService` | 2.2.0 | 0010 |
| WP-13 | [wp-13-linq-console.md](wp-13-linq-console.md) | LINQ-Konsole (SQL abfangen, FerretSharp führt aus) | Core/ClrModel (`LinqConsoleHost`, `LinqConsoleService`), `ModelHost`, Monaco | 2.3.0 | 0011 |
| WP-14 | [wp-14-explain-plan.md](wp-14-explain-plan.md) | Explain-Plan (geschätzt/tatsächlich) | Oracle (Plan, `V$SQL`), Core/Schema, UI Plan-Ansicht | 2.4.0 | 0012 |
| WP-15 | [wp-15-code-generation.md](wp-15-code-generation.md) | Code-Generierung (LINQ, Initializer, HasData) | Core/ClrModel (`CSharpCode`) | 3.0.0 | 0013 |
| WP-16 | [wp-16-model-cache.md](wp-16-model-cache.md) | Modell-Cache | Core/ClrModel | 3.1.0 | – |
| WP-17 | [wp-17-sql-editor.md](wp-17-sql-editor.md) | Freier SQL-Editor | Core/Query (`SqlScript`, Tokenizer), Oracle (Leseweg), UI `SqlView`, `SqlResultGrid` | 3.2.0 | 0014 |
| WP-18 | [wp-18-sql-script.md](wp-18-sql-script.md) | Skript ausführen, Verbindung löschen; Tabs umbenennen | Core/Query (`SqlSplitter`), UI SQL-Editor | 3.3.0–3.4.0 | – |
| WP-19 | [wp-19-linq-completion.md](wp-19-linq-completion.md) | LINQ-Autovervollständigung | Core/ClrModel, `ModelHost` (Roslyn) | 3.5.0 | 0015 |
| R1 | [r1-stabilization.md](r1-stabilization.md) | Stabilisierung nach Code-Review, gemeinsamer Undo-Stapel | Core/Data (`WriteLog`), Oracle, ClrModel, UI | 3.6.0 | – |
| R2 | [r2-structure.md](r2-structure.md) | Struktur-Refactoring (DB-Aufrufe, Dialoge, Shell, Session, Grid-Brücke) | UI (`RunDbAsync`, `ModalFrame`, `WorkspaceLifecycle`, `GridBridge`), Oracle/`OracleSession` | 3.6.1 | – |
| WP-20 | [wp-20-schema-compare.md](wp-20-schema-compare.md) | Schema-Vergleich: N Schemas als Matrix, gespeicherte Vergleiche, DDL-Vorschlag | Core/Compare (`SchemaComparison`, `DdlWriter`), Oracle/`OracleSchemaReader.Snapshot` | 3.7.0 | – |
| WP-24 | [wp-24-open-connections.md](wp-24-open-connections.md) | Mehrere offene Verbindungen, eine sichtbar (Alt+O zur vorigen) | UI State (`ConnectionHub`, `ConnectionScope`, `ConnectionScopeView`) | 3.8.0 | – |
| Klein | [small-multi-row-fk-jump.md](small-multi-row-fk-jump.md) | FK-Sprung mit mehreren markierten Zeilen (`in`-Filter) | Core/Query (`FkNavigation`) | 3.9.0 | – |
| WP-27 | [wp-27-model-type-check.md](wp-27-model-type-check.md) | Abgleich C#-Modell ↔ DB: Typ, NULL, Länge, Stellen (`ColumnTypeCheck`, Export-Format 2) | Core/ClrModel, `ModelHost` | 3.10.0 | – |
| WP-21 | [wp-21-form-view.md](wp-21-form-view.md) | Formularansicht einer Zeile (Seitenleiste, editierbar über den `ChangeTracker`), Vergleich markierter Zeilen (Dialog) | Core/Forms (`RowForm`, `RowComparison`), UI `RowFormPanel`, `FormCursor` | 3.11.0 | – |
| R3a | [r3a-fixes.md](r3a-fixes.md) | Fehlerbehebung nach dem Review 3.14.0 (Verbindungsverlust je Verbindung, Schreiben serialisiert, UI-Testprojekt) | UI State, Oracle, Compare; `tests/FerretSharp.UI.Tests` | 3.14.1 | – |
| R3b | [r3b-structure.md](r3b-structure.md) | Struktur-Refactoring nach dem Review 3.14.0 (Schema-Reader, Typwissen, SessionGate, UI-Bausteine, State) | Oracle (`OracleSchemaReader.*`, `SessionGate`, `OracleReading`), Schema/`OracleTypes`, UI (`FerretComponent`, `ExportActions`) | 3.15.0 | – |
| WP-25 | [wp-25-shortcuts.md](wp-25-shortcuts.md) | Tastenkürzel einstellbar (Einstellungen › Tastenkürzel, Übersicht aller Kürzel) | Core/Settings (`ShortcutMap`, `KeyChord`), UI `ShortcutService`, `shortcuts.js` | 3.16.0 | – |
| WP-28 | [wp-28-plsql.md](wp-28-plsql.md) | Prozeduren, Funktionen, Packages und Trigger ansehen (Quelltext, Parameter, Fehler, Abhängigkeiten, Quelltextsuche) | Core/Schema (`PlSqlObjects.cs`), Oracle, UI `PlSqlTabView`, Monaco nur lesend | 3.17.0 | (0011) |
| PR #3 | – | Schattenkopie der Build-Ausgabe für den ModelHost, Modell und LINQ-Konsole nach einem Build neu laden, Hänger-Diagnose als Einstellung (Beitrag von Phillip Ulbricht) | Core/ClrModel (`BuildOutputShadow`, `BuildOutputWatcher`), App/`UiStallMonitor` | 3.18.0 | 0016 |
| WP-29 | [wp-29-english-ui.md](wp-29-english-ui.md) | Oberfläche auf Englisch (Standard) und Deutsch: typisierte resx je Bereich, Sprache in den Einstellungen bzw. `--lang`, Screenshots auf Englisch | `Resources/` aller Projekte, `docs/localization.md` | 3.19.0 | 0017 |
| WP-30 | [wp-30-change-overview.md](wp-30-change-overview.md) | Offene Änderungen (Panel), Undo bis zu einer Aktion, Redo für Grid und Statements, Ctrl+Z/Ctrl+Y für ausstehende Zellen (Issue #7) | Core/Data (`WriteLog`, Bearbeitungsverlauf), UI State, Änderungsübersicht | 3.20.0 | 0018 |
| WP-22 | [wp-22-ddl.md](wp-22-ddl.md) | DDL im SQL-Editor (eigener Schemaweg, Rollback nach Bestätigung, Schema und C#-Modell folgen), DDL-Vorschlag des Vergleichs im SQL-Editor öffnen (Issue #9) | Oracle (`ExecuteDdlAsync`, `StatementGuard`), Query/`SqlClassifier`, UI `WorkspaceLifecycle.Schema` | 3.21.0 | 0019 |
| WP-23 | [wp-23-table-designer.md](wp-23-table-designer.md) | (1) Index anlegen in der Indizes-Ansicht, „Index dafür anlegen“ für FKs ohne Index; gemeinsamer DDL-Baustein (Ausführen, Im SQL-Editor öffnen, Speichern). (2) Spalten-Designer: Bearbeitungsmodus der Spalten-Ansicht (Spalten, Schlüssel, Checks, FKs mit Index, Prüfungen vorab mit Zählungen, Rest des Entwurfs nach Abbruch), Property-Zeile im Stil des Projekts. (3) Neue Tabelle im Designer (Entwurfs-Tab, Rest des Entwurfs nach Abbruch im Tabellen-Tab), Entity + `IEntityTypeConfiguration` aus einer Tabelle (Issue #10) | Core/Design (`TableDesign`, `IndexDesign`), Ddl, ClrModel (`EntityCode`, `PropertyCode`), UI `TableDesigner`, `NewTableView` | 3.22.0–3.24.0 | 0020 |
| R4a | [r4a-stabilization.md](r4a-stabilization.md) | Fehlerbehebung nach dem Review 3.24.0 (HTML in Zellen, Tracker nach DDL, DDL-Rollback, Designer-Ausdrücke, Einstellungsdateien, Undo/Redo, C#-Entity) | UI, Core/ClrModel, Settings | 3.24.1 | (0019) |
| R4b | [r4b-structure.md](r4b-structure.md) | Struktur-Refactoring nach dem Review 3.24.0 (`Core/Ddl`, `SqlScript` in Teilen, `StatementRunner`, `GridWriteHistory`, Designer-State, ModelHost-Fake-Tests, Explorer-Render) | Core/Ddl, Query (`SqlTokenizer`, `SqlSplitter`, `SqlAnalysis`), UI State (`StatementRunner`, `GridWriteHistory`), `tests/FerretSharp.FakeModelHost` | 3.25.0 | (0019, 0020) |
| WP-26 | [wp-26-history-tables.md](wp-26-history-tables.md) | Audit-Tabellen und Trigger aus einer Vorlagendatei: anlegen, abgleichen, im Designer mitziehen; Trigger über den Schemaweg (aus, prüfen, ein), Kompilierfehler als Marker | Core/Audit (`AuditTemplate`, `AuditPlanner`), Oracle (Trigger auf dem Schemaweg), `docs/audit-templates.md` | 3.26.0 | 0021 |
| WP-31 | [wp-31-display-columns.md](wp-31-display-columns.md) | Anzeigespalten: Namen neben FK-Werten im Grid und im Formular, „nicht gefunden“ für verwaiste Werte, geteilte Datei (Issue #11) | Core/Display (`DisplayLinks`, `DisplayNames`), `QueryBuilder.BuildLookup`, UI `FerretGrid` (seit R5b `State/GridDisplayNames`) | 3.27.0 | 0022 |
| WP-33 | [wp-33-app-statements.md](wp-33-app-statements.md) | Statements der laufenden App: von außen an eine lokale EF-Core-App anhängen (EventPipe), Statements nach Herkunft, N+1, Nachsehen in `V$SQL` mit Plan, Zuordnung zur Verbindung über die App-Umgebung (Issue #12) | `FerretSharp.AppTrace`, Core/AppTrace (Adressen seit R5b `Connections/OracleAddress`), UI `AppStatementsPage`, `AppTraceService`; `tests/FerretSharp.FakeEfApp` | 3.28.0 | 0023 |
| WP-32 | [wp-32-installer.md](wp-32-installer.md) | Installer und Update-Hinweis mit Velopack: `Setup.exe` und Portable-Zip, Prüfung beim Start und „Jetzt prüfen“, Herunterladen und neu starten auf je einen Klick, Neustart über den `ExitGuard`; eine Instanz je Datenordner | App (`Program.Main`, `IAppUpdater`), UI `UpdateState`, `release.yml` | 3.29.0 | 0024 |
| WP-34 | [wp-34-tab-pins-settings-nav.md](wp-34-tab-pins-settings-nav.md) | Tabs anheften (links, bleiben beim Schließen anderer/rechts davon/aller offen, gespeichert), Einstellungen mit Bereichsliste und Suche über alle Einstellungen und Tastenkürzel | UI State (`ShellState.Tabs`, `SettingsCatalog`, `SettingsFilter`), `TabBar`, `SettingsPage` | 3.30.0 | – |
| WP-35 | [wp-35-connection-import.md](wp-35-connection-import.md) | Verbindungen importieren (FerretSharp, DBeaver, SQL Developer; Installationen finden, Vorschau mit Art, User, Duplikaten) und exportieren; Passwort beim Verbinden abfragen | Core/Connections/Transfer, UI Import-/Export-Dialog, `PasswordDialog` | 3.31.0 | 0025 |
| R5a | [r5a-review-3.31.md](r5a-review-3.31.md) | Fehlerbehebung nach dem Gesamt-Review 3.31.0 (LINQ-Konsole mit Factory, deaktivierte PKs, Eingaben während des Schreibens, Import-Pfade und Prod, Passwörter, Trennen bei stillem VPN, Dateien von Hand) | quer: ModelHost, Oracle, Data, Connections/Transfer, UI; `docs/review-3.31.0.md` | 3.31.1 | (0006, 0011, 0019) |
| R5b | [r5b-structure.md](r5b-structure.md) | Struktur nach dem Gesamt-Review 3.31.0 (gemeinsame Bausteine statt Kopien, Test-Fundament, Farben als Tokens, Render-Messung) | Core/IO (`VersionedJsonFile`, `CachedFileRead`), Oracle (`OracleIdentifier`, `OracleLiteral`, `OraclePlans`), Schema/`OracleTypes`, Connections (`OracleAddress`, `CommandLine`), Query (`TextSearch`, `ValueInput`), Data (`TimedQuery`, `ValueCulture`), UI State (`JsModule`, `GridDisplayNames`, `TimeText`, `PresentationWatch`), `CoreText`/`CommonText`, `ferretsharp.css`; `tests/Shared/FakeDatabase` | 3.32.0 | (0022) |

## Neues Protokoll

- Eine Datei je Paket, Name wie der Branch ohne `wp/` (`wp-NN-kurzname.md`, `rNx-….md`), Überschrift `# WP-NN Titel → Release X.Y.Z`; Zeile in der Übersicht mit Bereichen.
- Inhalt: Auftrag/Planung mit den Entscheidungen des Nutzers (beim Release aus `docs/roadmap.md` hierher), was gebaut wurde, Nachträge, Fallen. Fallen, die über das Paket hinaus gelten, zusätzlich in die passende Regel unter `.claude/rules/`.
- Keine Testzahlen (stehen im Build) und beim E2E nur, was gefunden oder nicht geprüft wurde – nicht jeden bestandenen Schritt.
