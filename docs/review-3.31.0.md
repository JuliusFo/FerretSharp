# Gesamt-Review 3.31.0 (2026-10-13)

Auftrag des Nutzers (2026-10-13): Gesamt-Review über den ganzen Code statt nur über die Pakete seit dem letzten Refactoring – Ziel Fehler und Wiederverwendbares, dazu Doku und CLAUDE.md (stimmt es noch, frisst es unnötig Tokens). Sieben parallele Agenten nach Themen (nicht nach Ordnern), nur lesend: DB-Zugriff (DB), Zustand/Nebenläufigkeit (ST), UI/JS (UI), Prozesse/Dateien/Host (PH), Wiederverwendung (RE), Sicherheit/Tests (SE/TE), Doku (DO). Doppelte Befunde sind zusammengeführt (alle IDs genannt). **Nachgeprüft** = von Hand im Code bestätigt.

**Stand:** Teil A ist als R5a in 3.31.1 umgesetzt (Protokoll in `docs/work-packages.md`), bis auf A33–A37 (Backlog), A38 (Entscheidung: nur Option, Backlog) und A50 (CSS-Tokens, mit R5b). Bei A2 stimmte der View-Teil nicht (Views sind nur lesbar). Offen: Teil C (Doku), dann Teil B (R5b).

Schwere: **S** falsche Daten, Datenverlust, Sicherheitslücke, Hänger · **M** falsches Verhalten in realistischen Fällen oder klare Wartungslast · **K** Kleinkram.

## Teil A – Fehler und Sicherheit (Kandidaten für R5a, Patch-Release)

### Schwer

| # | IDs | Ort | Befund | Status |
|---|---|---|---|---|
| A1 | PH-1 | `ModelHost/ContextFactory.cs:22-47`, `LinqConsole.cs:125` | Die LINQ-Konsole gibt ihre Interceptors (Mitschnitt, Verbindung unterdrücken) nur im Pfad über den Options-Konstruktor mit. Mit `IDesignTimeDbContextFactory` oder parameterlosem Konstruktor (`OnConfiguring` mit echtem Connection-String) laufen `ExecuteDelete`/`SaveChanges` echt gegen die DB – ohne Transaktion von FerretSharp, ohne Prod-Sperre, sofort committet. Kein Schutz, keine Ablehnung. Projekt des Nutzers (Options-Konstruktor) nicht betroffen, Kollegen mit Factory schon. | nachgeprüft |
| A2 | DB-1 | `OracleSchemaReader.cs:153-160` (KeysSql), `OracleDataEditor.cs:380-405` | P/U-Constraints ohne Filter auf `STATUS`/`VALIDATED`: ein deaktivierter PK (oder `ENABLE NOVALIDATE` mit Duplikaten) wird Row-Key; die Zeilenzahl von UPDATE/DELETE wird verworfen → eine Grid-Änderung trifft still alle Zeilen mit dem Schlüssel. Views: Constraints immer `DISABLE NOVALIDATE` → Sortierung nicht total. Vorschlag: nur gültige Schlüssel als Row-Key, sonst ROWID; zusätzlich `Rows != 1` als Fehler (zum Savepoint zurück). | nachgeprüft |
| A3 | ST-2 | `ChangeTracker.cs:133-147, 540-541`, `WorkspaceEditing.cs:496-512` | Grid bleibt während Schreiben/Commit bearbeitbar. INSERT markiert alle *jetzt* ausstehenden Werte als geschrieben (`written ?? _pending`), auch die während der Roundtrips getippten; `Finish` leert nach Commit alle Tracker inkl. neuer Bearbeitungen; `reload` bricht einen offenen Editor ab. Über VPN realistisch. | nachgeprüft |
| A4 | PH-2, SE-1, TE-2 | `Transfer/FerretSharpConnections.cs:40-70`, `ConnectionImportPlan.cs:107-129` | FerretSharp-Import übernimmt `ClrProject`, `ScriptFolder`, `TnsAdminPath` aus der fremden Datei (neu: immer; überschreiben: wenn die eigene keine hat), Vorschau zeigt sie nicht. Beim Verbinden lädt der ModelHost die fremde DLL (Code läuft), „Neu bauen“ führt fremde MSBuild-Targets aus; `TnsAdminPath` auf eine Freigabe lenkt die Anmeldung um. Vorschlag: lokale Pfade beim Import immer verwerfen (oder ausdrücklicher „Umzug“-Modus mit Anzeige); Export behandelt `TnsAdminPath` als lokalen Pfad. | nachgeprüft |
| A5 | SE-2 | `ConnectionImportPlan.cs:111` | Überschreibt ein Import eine Prod-Verbindung, bleibt nur `ReadOnly`; die Art kommt aus der Quelle. Fast alle Prod-Schutzmaßnahmen hängen an `Kind == Prod` (Entsperren mit Namen, Prod-Bestätigung bei DML/LINQ-Write, Maskierung im Log, roter Rahmen). Test `ImportReviewTests.cs:89-90` hält das Verhalten fest. Vorschlag: Prod bleibt Prod, außer ausdrücklich in der Vorschau gewählt. | nachgeprüft |
| A6 | PH-3, PH-7 | `ConnectionStore.cs:26-55`, `ConnectionManager.cs:24-38` | Laden fängt nur `JsonException`. Adresse ohne/mit nicht erstem `"type"` (`NotSupportedException`), `"connections": null`, `IOException` laufen am R4a-Schutz vorbei (`LoadError` null, Liste leer) → die erste neue Verbindung überschreibt `connections.json` mit nur dieser einen. Ähnlich: `"shortcuts": null`, `recent.json` ohne `entries`, History ohne `entries` → Start bricht ab. | nachgeprüft (Pfad) |
| A7 | ST-1, DB-6, ST-9 | `OracleSession.cs:236-272`, `WorkspaceSessions.cs:215-226` | Nur ein hängendes Kommando schließt im Hintergrund; sonst läuft `_connection.DisposeAsync()` (ODP.NET: kein eigenes `DisposeAsync`, schließt synchron) im Aufrufer – bei Trennen/Workspace schließen auf dem UI-Thread. Stilles VPN → Fenster friert bis zum TCP-Timeout; `DetachAsync` schließt nacheinander. Wirft das Close einer toten Session, bleiben die übrigen offen (Sperren bis Prozessende). | wahrscheinlich |

### Mittel

| # | IDs | Ort | Befund |
|---|---|---|---|
| A8 | ST-7, SE-3 | `ConnectionDialog.razor:362-381`, `WorkspaceSessions.cs:21-27, 113` | Profil einer **offenen** Verbindung ändern (Dev → Prod + schreibgeschützt) wirkt erst nach Neuverbinden: Workspaces bleiben schreibbar, Commit ohne Prod-Bestätigung, keine Maskierung. Kein Hinweis. (nachgeprüft) |
| A9 | DB-4 | `SqlClassifier.cs:51-55, 91-96` | TRUNCATE ist gesperrt, aber `ALTER TABLE … TRUNCATE/DROP PARTITION`, `FLASHBACK TABLE … TO …`, `PURGE` laufen als normales DDL. (nachgeprüft) **Entscheidung des Nutzers.** |
| A10 | SE-4, DB-16, TE-3 | `StatementRunner.cs:141, 243, 257-260`, `QueryErrorLog.cs:12` | `IDENTIFIED BY …` (ALTER/CREATE USER, DB-Link) steht im Klartext im Verlauf und bei Fehler im Log, auch auf Prod. Vorschlag: vor Verlauf/Log schwärzen, mit Tests. |
| A11 | ST-5 | `PasswordDialog.razor:39, 69`, `ActiveConnection.cs:109-112` | Passwortabfrage (WP-35) speichert standardmäßig im Credential Manager **vor** dem Verbinden; bei ORA-01017 wird nur das Sitzungs-Passwort vergessen → falsches Passwort dauerhaft gespeichert, keine erneute Abfrage. |
| A12 | ST-6, SE-6 | `SchemaCompareService.cs:33, 125`, `SchemaCompareLoader.cs:63` | Schema-Vergleich hat eigenen Passwortspeicher; falsches Passwort bleibt bis Neustart, danach kein Eingabefeld mehr. Vorschlag: Sitzungs-Passwörter aus WP-35 nutzen. |
| A13 | ST-3 | `WorkspaceEditing.cs:355-375, 437` | „Überschreiben“ bleibt nach Abbrechen oder gescheitertem Flush in `_overwrite` → späteres Schreiben der Zeile ohne Konfliktprüfung. |
| A14 | ST-4 | `WorkspaceEditing.cs:281-284, 375` | Scheitert ein Redo, schreiben „Erneut versuchen/Überschreiben/Verwerfen“ per `FlushAsync` alles Ausstehende aller Tabs statt nur das Redo; übrige Redos gehen verloren. |
| A15 | ST-8 | `FerretGrid.razor:126-127, 254-295`, `WorkspaceLifecycle.Schema.cs:99-123` | Anzeigespalten (WP-31) × Schema neu laden: Links werden nicht neu geplant → nach Umbenennen der Anzeigespalte ORA-00904 und Namen abgeschaltet; neuer FK ohne Kennzeichen bis Tab neu geöffnet. |
| A16 | DB-2 | `OracleParameters.cs:43-48`, `DmlBuilder.cs:138` | Row-Key mit `BigNumber` (NUMBER > 28 Stellen) wird als `Decimal` gebunden → vermutlich `InvalidCastException` roh durch Flush/UI; „Änderungen als SQL“ zeigt `BigNumber { … }`. |
| A17 | DB-3 | `OracleSchemaReader.cs:88-95` | INVISIBLE-Spalten fehlen (`hidden_column = 'NO'`), kommen aber als PK-Spalte aus `KeysSql` → Tabelle lässt sich nicht öffnen; Vergleich/Designer sehen die Spalte nicht. |
| A18 | DB-5, RE-10, RE-11 | `QueryBuilder.cs:373-400`, `OracleTypeMapper.cs:152-166` | Filter/FK-Sprung/SQL-Variablen binden NVARCHAR2/NCHAR als VARCHAR2/CHAR (Schreiben korrekt national) → bei Nicht-Unicode-DB findet ein Filter mit `Łódź` nichts. Typwissen weiter verteilt statt in `OracleTypes`. |
| A19 | UI-1, ST-13 | `AppStatementsPage.razor:426-460` | Nachschlagen mit Passwortabfrage: Abbrechen → 60 s Spinner, Knopf gesperrt. Verbindung während des Nachschlagens geschlossen → „Zeitüberschreitung“. |
| A20 | UI-3, RE-1 | `SqlResultGrid.razor:164-185` | Ctrl+C im SQL-Ergebnis verwirft Export-Warnungen (CLOB nur Vorschau, still); `SqlResultGrid` ist kein `FerretComponent`, `[JSInvokable]` ohne Guard. |
| A21 | UI-2 | `FerretGrid.razor:1061-1100`, `RowFormPanel.razor:507` | Grid kopiert den geladenen Wert, Formular den ausstehenden; Grid zeigt den ausstehenden. **Frage an den Nutzer:** was soll Ctrl+C/INSERT kopieren? |
| A22 | UI-4, RE-5 | `ConnectionActionsMenu.razor`, `LinqMenu.razor`, `WorkspaceBar.razor:67-90` | Handgebaute Menüs ohne Esc, Fokus, Fensterbegrenzung → am unteren Rand abgeschnitten („Löschen…“ unerreichbar). `MoreMenu`/`PopoverMenu` nutzen. |
| A23 | UI-5 | `Shell.razor:317-503` | Dienst-Ereignisse der Shell als verworfene `InvokeAsync`-Tasks, keine ErrorBoundary über der Shell, `[JSInvokable] OnShortcut` ungeschützt → Ausnahmen unbeobachtet. |
| A24 | PH-4 | `EventPipeTraceSource.cs:59-64, 196-217` | Anhängen an eine App am Breakpoint: Start synchron ohne Abbruch, `StopAsync` wartet unter Sperre → Seite hängt. (Verdacht) |
| A25 | PH-5 | `LinqConsoleService.cs:458-473`, `LinqConsoleHost.cs:167-269` | Dispose ohne Gate während eines LINQ-Laufs → `ObjectDisposedException` auf dem entsorgten Semaphor → Fehlerbalken. |
| A26 | DB-7 | `OracleSession.Transactions.cs:33-46` | `SET TRANSACTION READ ONLY` hält autonome Transaktionen nicht auf: `SELECT pkg.f() FROM dual` kann aus gesperrtem Prod schreiben. Doku (CLAUDE.md, ADR 0006, README) ergänzen. |

### Klein

| # | IDs | Befund |
|---|---|---|
| A27 | ST-10 | Laufender Write/Commit: Verlassen-Dialog „Committen/Verwerfen“ schließt ohne Erklärung. |
| A28 | ST-11 | Tab nur mit geschriebenen Änderungen schließt ohne Frage; Undo macht sie im unsichtbaren Tab „wieder ausstehend“; B6-Prüfung (R4a) sieht nur offene Tabs. |
| A29 | ST-12 | Trennen und sofort neu verbinden: altes `ClearWorkspaces` leert die Tabs der neuen Instanz. |
| A30 | ST-15 | `StructureStale` bleibt nach Verwerfen ohne Commit/Rollback → Bearbeitung gelöschter Spalte scheitert erst mit ORA-00904. |
| A31 | ST-14 | `KeepRedoable` nur in `UndoToAsync` → veraltete Einträge in `_undone`. |
| A32 | DB-8, DB-9 | „Endet mit“ auf CHAR trifft nichts (Padding); FK-Sprung CHAR → VARCHAR2 ohne Trim (Anzeigespalten lösen das schon). |
| A33 | DB-10, DB-11 | TIMESTAMP(8/9)-Keys verlieren Nanosekunden (`RowGoneException`); Filter auf TSTZ ohne Offset. |
| A34 | DB-12, DB-13, DB-17 | SQL_ID per MD5 über UTF-8 (Nicht-AL32UTF8-DB); `GATHER_PLAN_STATISTICS` landet im Kommentar (`-- SELECT …`); `V$SQL` statt `GV$SQL` (RAC). |
| A35 | DB-14 | LONG im SQL-Editor bei 32.767 Zeichen still gekürzt. |
| A36 | DB-15 | Indexname nur gegen die eigene Tabelle geprüft → ORA-00955. |
| A37 | SE-7 | EXPLAIN in eine eigene Heap-`PLAN_TABLE` des Schemas schreibt und committet (auch Prod). (Verdacht) |
| A38 | SE-5 | CSV/Zwischenablage: `=…`-Werte werden in Excel als Formel ausgewertet. |
| A39 | SE-8 | WebView: kein `UrlLoading`-Filter, Drag&Drop einer Datei öffnet sie per Shell. (Verdacht) |
| A40 | SE-9 | Prod: Verlauf ohne Variablenwerte, aber `workspaces/*.json` enthält Filter- und Variablenwerte. **Frage an den Nutzer.** |
| A41 | PH-8 | Workspace-Laden meldet Fehler fremder Verbindungen; `UnauthorizedAccessException` einer Datei verhindert jedes Verbinden. |
| A42 | PH-9 | `JdbcUrl.ForDisplay` zeigt Teile des Passworts (`p@ss`) bzw. die ganze OCI-URL in der Vorschau. |
| A43 | PH-10 | Credential-Manager-Ziele global je Windows-User, Daten je `--data-dir` → kopierter Datenordner überschreibt/löscht echte Passwörter. |
| A44 | PH-11, PH-12 | Relatives `--data-dir` nach Update-Neustart (Verdacht); `async void OnExit` – Dispose läuft praktisch nie, Deadlock-Risiko. |
| A45 | PH-13, PH-14 | `.dbp` mit untypischem Root bricht Import ab; `Directory.Exists` auf VPN-Freigabe im UI-Thread vor „Speichern unter“. |
| A46 | UI-9 | tooltip.js entfernt `title` → nach Blazor-Update bleibt alter Tooltip. |
| A47 | UI-10 | grid.js: `ValidateEdit` ohne `catch`; Fehlerverhalten der drei Editoren uneinheitlich (Text-Editor gibt leeres Feld frei). |
| A48 | UI-15, UI-16 | `OnScrolled` ohne Guard, `MonacoEditor` kein `FerretComponent`; `WriteCredential`-Fehler im PasswordDialog ungefangen. |
| A49 | UI-17 | Update-Download rendert die Einstellungen je Prozent neu und verwirft die Suche. |
| A50 | UI-12 | Warn-/Ausstehend-/Geschrieben-Farben fest codiert, ohne Dunkel-Variante; Plakette der Anzeigespalte im Dunkelmodus ~3:1 Kontrast. |

## Teil B – Struktur und Wiederverwendung (Kandidaten für R5b)

| # | IDs | Vorschlag | Aufwand |
|---|---|---|---|
| B1 | UI-7 | `State/GridDisplayNames` aus FerretGrid (Plan-Sequenz, Generationen, Warteschlange, Retry, ~260 Zeilen ohne Tests) + Tests; nebenbei `PlanNamesAsync` nach Dispose, `_lookupCancel` entsorgen. Zusammen mit A15. | M |
| B2 | RE-8, PH-6, PH-7 | `Core/IO/VersionedJsonFile<TDoc>`: Missing/Ok/Broken/Newer, Normalisieren von null, Regel je Store ausdrücklich. Acht Kopien mit sechs Regeln; `RecentConnections` prüft keine Version. Behebt A6 an der Wurzel. Meldungen einmal in CoreText. | M–L |
| B3 | RE-2, RE-3 | `OracleIdentifier.MaxBytes/Problem/Cut`: Namensprüfung sechsmal, Konstante dreimal; Vergleich kürzt nach Zeichen statt Bytes (ORA-00972) und nutzt `IndexDesign.SuggestName` nicht. | S–M |
| B4 | RE-1, UI-3, RE-13 | Kopieren: `ExportActions.CopyRowsAsync/CopyCellAsync` für beide Grids, alles über `DomModule.CopyTextAsync` (ErrorDialog umgeht es), eine Meldung statt vier Keys. | S |
| B5 | RE-4 | `Core/TextSearch` (Regel der Einstellungssuche: Wörter, Akzente/Groß-klein egal) für alle sechs Suchen; zwei eigene Inputs → `SearchBox`. | S–M |
| B6 | RE-6 | `OracleAddress.SameAs` + ein Parser für EZConnect/Descriptor (WP-33 und WP-35 vergleichen und lesen Adressen je selbst, schon verschieden). | M |
| B7 | RE-7, RE-9 | `CachedFileRead<T>` (Audit-Vorlage, Anzeigespalten-Datei) und `TimedQuery.RunAsync` (Anzeigespalten, FK-Zählung). | S–M |
| B8 | RE-11 | `OracleTypes` ergänzen (`IsFixedChar`, `IsNational`, `IsExactNumber`, `IsTimestamp`, …) und Typentscheidungen dorthin ziehen. Zusammen mit A18. | S |
| B9 | UI-11, RE-12 | `JsModule(js, path)` als Basis von `DomModule`; shortcuts.js/tooltip.js/monaco darüber. | S |
| B10 | UI-8 | AppStatementsPage: Sortieren/Filtern/Match nach `AppTraceService.ShownStatements` (gecacht je Version) – alle 250 ms Sortierung von bis zu 2000 Statements. | S–M |
| B11 | UI-6 | Erst messen (wie R4b): jede Zellbearbeitung rendert alle Tab-Ansichten aller Workspaces; ggf. `ShouldRender` je Tab-Schlüssel. | M |
| B12 | RE-14 … RE-24, UI-13, UI-14 | Kleinkram: Dauer-Format (fünf Varianten), Fußzeilen, DDL-Bestätigung zweimal, de-DE-Kultur siebenmal, SQL-Literal viermal, Plan lesen zweimal, `OneLine` dreimal (LinqView: erstes Wort bei `TagWith` = „--“), TitleEditor im Workspace-Chip, `--x=`-Argumente fünfmal, App-Version viermal, `JsonFiles.Options` in der SQL-Historie, `MasksValues` (Prod-Regel dreimal), `PresentationWatch` viermal, Tab-Anzeigename dreimal. | je S |
| B13 | RE-25 | Toter Code: `RemoveTabsWhere`, `CanUndoEdit/CanRedoEdit`, `DisabledReason`, `IsWaiting`, `ConnectionHub.OpenAsync`, `NameTyped`, monaco `focus`. | S |
| B14 | RE-26, RE-27 | Benennung: `DisplayName` dreifach belegt, `FilterRules` ist der allgemeine Eingabe-Parser; gleiche Texte in mehreren resx → `CommonText`. | S |

### Tests

| # | IDs | Vorschlag |
|---|---|---|
| B15 | TE-1 | `TestApp`: Prod standardmäßig schreibgeschützt, Fake bildet gesperrte Sessions ab; Tests für Write/DDL/LINQ-Write/`RunDdlAsync` auf gesperrtem Workspace. |
| B16 | TE-4, TE-5 | Timing: `FakeTimeProvider` für `BuildOutputWatcher`/`ClrModelManager`; nicht thread-sichere Listen in `TestApp`/Fakes. |
| B17 | TE-6 | Integration: jeder Container-Startfehler → Skip mit Grund; .NET 8/FakeEfApp per `Assert.SkipUnless`. |
| B18 | TE-8, TE-9, SE-10 | Fakes nach `tests/Shared`, `FakeConnector`-Builder; `ReadOnlyTests` mit Allow-List aller öffentlichen Methoden und Internal-Prüfung für alle ausführenden Methoden. |
| B19 | TE-7 | Der wackelige Test aus R4b ist seit `f8e6397` behoben (Ursache in UI-CLAUDE.md) – Nachtrag im R4b-Protokoll. |

## Teil C – Doku und CLAUDE.md

Grundlast heute ~55 KB (~15,5k Tokens) je Session mit Core und UI; `work-packages.md` (217 KB, ~60k Tokens) passt in kein einzelnes Read.

1. **Korrekturen** (führen heute falsch): CLAUDE.md „einfaches Zip“ (DO-1), SQL-Ort ohne `Core/Ddl` (DO-2), Struktur-Baum (DO-3); architecture.md `InitialLONGFetchSize` 4000 → 32767 (DO-22), `FlushAsync`-Signatur (DO-23), `RowState`-Skizze (DO-24), `Shortcuts.With` (DO-25), „Beim Start prüfen“ (DO-26), Kürzel-Tabelle unvollständig (DO-27), Kopf/`ALL_TAB_COLUMNS`/UI-Skizze (DO-28); Core-CLAUDE.md v2-Planungstexte (DO-11 … DO-14); localization.md `SchemaText`, `With`, Glossar, Beispielkeys (DO-45 … DO-48); README „Settings › General“, Update-Prüfung (DO-49, DO-50); backlog erledigte Einträge (DO-36, DO-37); e2e-testing CDP vs. SendInput, Skripte ins Repo, `New-SampleDb` ohne `-Name` (DO-41 … DO-44).
2. **`work-packages.md` aufteilen** (DO-29, DO-30): `docs/work-packages/README.md` als Index (Übersicht + Versionstabelle, Spalten Datei und Bereiche) und ein Protokoll je Paket (`wp-NN-kurzname.md`, `rN-….md`, `v1.md`); Planungsabschnitte released Pakete aus `roadmap.md` dorthin (DO-34). Künftige Protokolle ohne Testzahlen, E2E nur Gefundenes/Ungeprüftes (DO-31).
3. **CLAUDE.md-Dateien schlanker** (DO-4 … DO-10, DO-15 … DO-21): Wurzel ~20 → ~11 KB (Versionstabelle in den Index, CI/Release nach `docs/release.md`, Abschnitt 8 auf 2–3 Zeilen, Nutzerkontext zum C#-Modell); Core-CLAUDE.md nach Ordnern (`Core/Oracle/`, `Core/ClrModel/`, `Connections/Transfer/`, DDL), neue `tests/CLAUDE.md`; UI-CLAUDE.md ohne Doppelungen und Geschichten. Ergebnis ~7,5k statt ~15,5k Tokens Grundlast bei UI-Arbeit.
4. **roadmap.md/backlog.md**: Roadmap auf Labels, Geplantes, offene UX-Fragen (~2 KB); Backlog nach Bereichen gliedern (DO-35, DO-38 … DO-40).

## Entscheidungen des Nutzers (2026-10-13)

Reihenfolge: erst R5a (Fehler, Patch 3.31.1), dann die Doku (Teil C), dann R5b (Struktur, 3.32.0).

1. **A9** – `ALTER TABLE … TRUNCATE/DROP (SUB)PARTITION`, `FLASHBACK TABLE … TO …` und `PURGE` werden wie TRUNCATE behandelt (nie ausgeführt).
2. **A21** – Kopieren im Grid (Ctrl+C, Wert kopieren, Als Tabelle/INSERT/C#) nimmt den ausstehenden Wert, wie angezeigt und wie das Formular.
3. **A8** – Profiländerung einer offenen Verbindung: Neuverbinden anbieten.
4. **A40** – Filter-, Variablenwerte und SQL-Text von Prod bleiben in den Workspace-Dateien (Arbeitsstand wie ein offenes Dokument; der SQL-Text enthält Literale ohnehin). Dokumentieren in `architecture.md` und README: Logs und Verlauf enthalten auf Prod nie Werte, Workspace-Dateien schon.
5. **A38** – CSV und „Als Tabelle kopieren“ bleiben exakt; Option „Formeln entschärfen“ steht im Backlog.
6. **A4** – Import verwirft lokale Pfade (C#-Projekt, Skriptordner, TNS_ADMIN) standardmäßig. Enthält die Datei welche, zeigt der Dialog eine Checkbox „Lokale Pfade übernehmen“ (Standard aus), die Pfade stehen in der Vorschau je Zeile. Ohne Pfad fällt TNS_ADMIN auf die Umgebungsvariable zurück (gibt es schon). Der Export zählt `TnsAdminPath` künftig zu den lokalen Pfaden.
