# R8 Fehler, Struktur, Tests und Doku nach dem Gesamt-Review 3.54.0 → Release offen
Teil A bis D des Gesamt-Reviews (`docs/review-3.54.0.md`) in **einem** Paket und einem Release (Entscheidung 1 des Nutzers, 2026-10-29). Branch `wp/r8-review-3.54` mit dem Review-Commit vorne, gebaut in einer Session ohne weitere Agenten. Reihenfolge wie in R7: erst die reinen Verschiebungen (B6, B7), dann Teil A mit Tests, dann Struktur (B), Tests (C), Doku (D), zuletzt E2E.

## Ordner (B6, B7)
- `ColumnDesigner` → `TableDesignerDraft`, ein Typ je Datei (`DesignColumn`, `DesignConstraint`, `ExistingConstraint`, `DesignerSection`); die Audit-Regeln des Designers (`AuditAtStart`, `CanAudit`, `HasAuditTable`) als `AuditDesignRules` mit eigenen Tests (auch die Audit-Zeile ruft sie).
- `MenuFilter` nach `State/Shell`, `BackgroundRuns` in eine eigene Datei, `DiagramStyle` mit dem Namespace der Helfer (`FerretSharp.UI.Components`, `ui.md`).

## Teil A – Fehler und Sicherheit
- **A1:** `ConnectionHub.CloseAsync(scopes)` nimmt alle Verbindungen einer Gruppe aus `Open`, bevor die erste schließt, und schließt sie parallel (wie `DisposeAsync`); „Alle trennen“ und das Trennen einer Verbindung laufen darüber. Test: die erste `Changed`-Meldung hat keine der beiden mehr offen (ohne den Fix schlägt er an – nachgestellt).
- **A2:** Der Kopie-Dialog zeigt beim Schreiben `RunProgress` der **Ziel**-Session (kaskadierte `WorkspaceManager`/`ActiveConnection` der Zielverbindung): Zeit, Abbrechen, nach 5 s „Session trennen“ (Entscheidung 3). `WorkspaceEditing.CopyAsync` reicht das Token durch; abgebrochen rollt die Kopie auf ihren Savepoint zurück. Test mit abgebrochenem Token (ohne Durchreichen schlägt er an – nachgestellt). Ein Schließen während des Schreibens bricht nicht ab: die Kopie ist eine Aktion der Zieltransaktion wie jedes Schreiben.
- **A3:** Das Lesen besitzt seine CTS und gibt sie erst nach seinem Ende frei; `Dispose` bricht ab (auch ohne „Schließen“, etwa beim Neuverbinden). Abbrechen auf dem Pool-Thread über den neuen Baustein `CancelOffThread` (B10).
- **A4:** `GridAuditChanges.Clear` bricht die laufende Abfrage ab; jede Abfrage hat 10 s (`TimeLimit`, `TimeProvider`), danach ist die Hervorhebung für den Tab aus mit dem Hinweis auf den fehlenden Index (Entscheidung 4). Tests mit `FakeTimeProvider`.
- **A5:** Nicht geladene Zeilen einer Markierung liest das Kopieren als Hintergrundlauf (Zeilen und Abbrechen in der Statusleiste) und kopiert danach über den Host; das Token geht bis `ReadPageAsync`/`ReadSqlAsync`, die Grids brechen beim Schließen des Tabs ab (auch „Wert setzen“/Ctrl+D). Test: Abbrechen kopiert nichts.
- **A6:** `ModelHostRunner.OneBuildAtATimeAsync`: app-weit ein `dotnet build` zur Zeit, der zweite wartet (Entscheidung 5) – einfacher und sicherer als eine Sperre je Pfad, weil sich Projekt- und Solution-Build derselben Ausgabe nicht am Pfad erkennen. Test der Sperre; die Kollision zweier echter Builds wurde nicht nachgestellt.
- **A7:** `release.yml`: `persist-credentials: false` am Checkout.
- **A8:** `MenuFilter` vergleicht über `TextSearch` (Akzente egal), FK-Sprünge zusätzlich über `NameRank` (Unterstriche weglassbar). **A22:** `MenuFilter.KeepsHeading` – eine Überschrift steht genau so lange wie eine ihrer Zeilen. Tests `MenuFilterTests`.
- **A9:** `WorkCloneMapping.Resolve` nimmt einen absoluten PDB-Pfad nur unter der Repository-Wurzel, ohne Wurzel nur lokal (`GitFolder.IsShare`, jetzt öffentlich); `SourceFiles` lehnt auch `//host/share` ab. Tests (der Fall mit UNC wird bewusst nicht ausgeführt: er würde sich anmelden).
- **A10:** `MermaidText.Plain` für beide Diagramme: Zeilenumbrüche zu Leerzeichen, `%%` aufgebrochen; der `%%`-Kommentar des Beziehungsdiagramms ebenso.
- **A11:** `DdlScript.Render` kommentiert jede physische Zeile einer Kopfzeile. Nachgestellt (Unit-Test mit Zeilenumbruch im Tabellennamen schlägt ohne den Fix an).
- **A12:** `CachedFileRead` liest Anzeigespalten-Datei und Audit-Vorlage nicht über 4 MB (`MaxBytes`, eigene Meldung „größer als 4 MB“).
- **A13:** `ALTER DATABASE LINK` ist wieder DDL (Entscheidung 6), `ALTER DATABASE`/`PLUGGABLE` bleiben gesperrt; Fälle in `SqlScriptTests` und `ReadOnlyTests`.
- **A14:** `ISchemaReader.GetNamesElsewhereAsync` (ein Roundtrip: Constraint-Namen der anderen Tabellen des Owners, ohne von Oracle erzeugte; alle Objektnamen außer den eigenen Indizes) → `DesignContext.Elsewhere`; Umbenennen, neue Constraints und Index-Umbenennen prüfen dagegen mit eigenen Meldungen (ORA-02264/ORA-00955). Unit- und Integrationstest; `ReadOnlyTests` kennt die Methode.
- **A15:** `OracleDdl.ColumnDefinition` schreibt `GENERATED ALWAYS AS IDENTITY` für `IdentityAlways` – erledigt den Backlog-Punkt „Identity ALWAYS/BY DEFAULT im Snapshot“.
- **A16:** `CopyTarget.SchemaNow` fragt das Schema der Zielverbindung bei jedem Lesen.
- **A17:** `AutoReconnect` meldet „Holding“ nur beim Eintritt (Test: kein `Changed` beim nächsten Nachsehen; ohne den Fix schlägt er an – nachgestellt).
- **A18–A21:** `Shell.Run` fängt `OperationCanceledException` still; der Zwischenablage-Thread gibt jede Ausnahme an den Aufrufer; ein Build in der Zeitgrenze zeigt Grund und Ausgabe statt „Exit-Code -1“ (`DotNetRun.Error`, Test); `Microsoft.Extensions.Caching.Memory` 8.0.1 als `VersionOverride` im ModelHost (nur zum Kompilieren, `dotnet list package --vulnerable` jetzt ohne Treffer).
- **A23–A29:** CSS: Audit-Tönungen vor den eigenen Zuständen, gleiche Gewichte, Markierung über allem (`.ag-row.ag-row`); `RelationsView`/`RecordDiagramView` mit `ShouldRender => Shown`; `ValuePicker` scrollt die Markierung (`scrollIntoViewById`) und hält ↑ ↓ vom Cursor des Suchfelds fern (`dom.js arrowsMoveList`); `grid.js destroy` beendet ein laufendes Ziehen (`endDrag`); `dragWidth` endet wie `dragToDrop` (verlorener Capture, Taste unbemerkt los); `--warn-text` (hell 5,3:1 auf der 14-%-Fläche, berechnet); `QuickAction.Primary` für „Verbinden“ ohne `aria-pressed`.

### Verdachtsfälle
- **A3:** Die Mechanik ist am Code belegt (CTS vor dem Abbruch freigegeben), das Zeitfenster nicht gemessen – die Änderung ist ohne Nebenwirkung.
- **A6:** Dass zwei Builds gleichzeitig laufen, ist am Code belegt; die Kollision in `obj/` wurde nicht mit zwei echten Builds nachgestellt.
- **A10:** CR/LF in Namen ist am Code belegt; ob Mermaid `%%{init}%%` mitten in einem Label auswertet, wurde nicht geprüft (dafür hätte ein Wert in einen fremden Dienst gemusst). `%%` aufzubrechen ändert nichts Sichtbares.
- **A11:** im Unit-Test nachgestellt; ein Objekt mit Zeilenumbruch im Namen in einer echten DB nicht.
- **A18:** kein Auslöser gefunden – die Regel in `ui.md` verbietet den Durchgang, `GuardAsync` fängt seit 3.50.2 genauso.
- **A20:** eine OOM-Ausnahme beim Kopieren nicht nachgestellt; dass eine unbehandelte Ausnahme auf einem eigenen Thread den Prozess beendet, ist .NET-Verhalten.
- **A26, A27:** nicht mit echter Maus nachgestellt – die Härtung entspricht dem, was `dragToDrop` seit WP-66 tut; geprüft im E2E (unten).

## Teil B – Struktur
- **B1:** `DiagramCanvas` (Größe, Bühne, Kanten; Knoten als Inhalt) für Beziehungs-, Datensatz-Diagramm und Kopiervorschau, `DiagramZoomBar`; die Zustände geben ihr `DiagramZoom` heraus statt fünf Member; die Kante ohne Beziehung heißt in der Kopiervorschau jetzt auch `group` (`other` hatte keinen Stil); `RelationDiagram.MaxDepth`.
- **B2:** Die Knotenmenüs beider Diagramme sind `ContextMenuHost` (↑ ↓ inklusive). **Nicht** umgestellt: das Menü der Spaltenansicht (Filterfeld, das beim Schließen zurückgesetzt wird) und das der Tab-Leiste (seine Aktionen lesen den offenen Tab aus mehreren Methoden) – `ContextMenuHost` bräuchte dafür einen Zugriff aufs Element und einen Hook beim Schließen (Backlog).
- **B3:** `BackgroundRuns.ReadAllRowsAsync` für die Werte einer Spalte und ganze markierte Spalten.
- **B4:** `BuildLookup` nimmt seine IN-Liste aus `KeyIn`; `KeyGroups` bindet, gruppiert und ordnet Schlüssel für die Traversierung und die Prüfung vorhandener Zeilen der Kopie. Die Prüfung übersprungener Eltern (`CheckSkippedParentAsync`) behält ihre Schleife: Sie hält Schlüssel ohne Zeilen.
- **B5:** `ConstraintTypes` (Core) und `ConstraintBadges` (UI) statt drei Kopien; `FkCounts.Tooltip` auf `ColumnFacts.Title`; `ConnectionKindInfo.CssClass` statt Inline-Klassen; `ConnectionLastUsed.Text(lastUsed, id, today)`; `ColumnFacts.CopyNameAsync` für beide Spaltenmenüs.
- **B8:** weg: `.copy-parents`, `TableDesigner.Describe`, zwei `ShortcutService`-Injects, `SchemaCompareService.Choices`, `TableOrder.HasCycles`; `ConnectionGroupState.SetCollapsedAsync`/`SetOrderAsync(list)` und `TraversalPlan.ParentsOnly` sind `internal` (nur Tests). Der B6-Rest aus 3.44: `DdlDraftPanel` fragt `DesignPlan.Runnable` – die Regel des Plans gilt für die zusammengesetzten Schritte des Designers mit Audit.
- **B9:** Kommentare `GridOptions`, `SqlResultGrid.OnHeaderContextMenu`, `SqlClassifier` berichtigt.
- **B10:** `CancelOffThread` für das Abbrechen auf dem Pool-Thread; benutzt vom Kopie-Dialog, `GridAuditChanges`, `FerretGrid`. Die übrigen Kopien (`ActiveConnection`, `RecordDiagramView`, `OracleSession` – Core kennt UI nicht) bleiben, sie halten die CTS schon richtig.

## Teil C – Tests
- **C1:** Wächter des Schreibwegs: Unit-Test ohne DB (Batch mit DDL/PL/SQL/zwei Statements, Batch ohne Parameter, Lock-Reader mit einfachem SELECT, Savepoint-Name mit `;`) und Integrationstest (Batch und Lock nur in einer schreibenden Transaktion, eine zweite Transaktion abgelehnt, Sperren mit offener Schreibtransaktion abgelehnt).
- **C2:** Importgrenzen: ein Eintrag über 16 MB (gepackt wenige KB), Einträge über 64 MB zusammen (über `DbeaverConnections.ReadExport`), eine Datei über 16 MB auf der Platte. Ein Header, der die Größe falsch angibt, ist nicht getestet: Ein solches Zip lässt sich mit `ZipArchive` nicht bauen.
- **C3:** „Alle trennen“ fragt einmal (verwerfen/abbrechen), Löschen einer offenen Verbindung fragt (mit A1).
- **C4:** Eine Session, die sich nicht wieder sperren lässt, wird verworfen; die nächste Nutzung öffnet eine gesperrte.
- **C5:** `WorkspaceEditing.FateOf` für „rückgängig“ und „mit der Transaktion beendet“. **Nicht** ergänzt (Backlog): `StoppedByForm`, `AutoReconnect` beim Abschalten während des Wartens, `StopIdleLinqHostsAsync` (die Konsole des Fakes zeigt ihr Ende nicht), `ConnectionGroups.RenameAsync`-Fehlerzweige, die Sonderfälle des `SubgraphCopyPlanner` und der Zielwechsel des Entwurfs.
- **C6:** `PinAction` (die Regel stand zweimal im Markup), `ConnectionLastUsed`.
- **C7:** `ReadOnlyTests` schneidet am äußeren `FROM`.
- **C8:** Umbenennen eines Workspaces und Snapshot nach `CREATE TABLE` werden gepollt; der OOB-Test misst einen warmen Lauf. **Nicht** geändert: die Sekunde in `SessionActivityTests` – ohne die V$-Sichten, die der Test prüft, gibt es kein Signal, dass das Statement läuft; die Reserve ist 1 s gegen 3 s.
- **C9:** `CellCopiesTests` baut die Zahl mit der Kultur der Maschine, `LinqFilterTests` prüft den Grund.
- **C10:** Verlaufsgrenze ab voller Datei (6 s → unter 1 s), angehaltene App mit 0,5 s Stopp-Grenze (`EventPipeTraceSource.StopAfter`). `ModelHostProcessTests` und `BoundedSql` unverändert (die Grenzen sind Teil dessen, was sie prüfen).
- **C11:** `PresentationServiceAuditTests` in eigener Datei; `tests/Shared/Wait.UntilAsync` statt acht Kopien.
- **C12:** im E2E (unten).
- **C13/C14 – überflüssige Tests:** `SettingsStoreTests` zehn → zwei (eine alte Datei ergibt für jede ungenannte Einstellung den Standard; ein Round-Trip setzt jede Einstellung per Reflexion vom Standard weg – geprüft: eine Einstellung mit `[JsonIgnore]` lässt ihn scheitern); `SqlResultOrderTests` sechs Facts → eine Theory mit denselben Fällen; `ColumnValuesTests` zwei Facts → Theory. Learnings in `.claude/rules/tests.md` › „Redundante Tests“. Nicht zusammengelegt: `SqlResultOriginsTests` (die Fälle prüfen je etwas anderes am Ergebnis) und `ColumnValueCopiesTests.A_result_counts_its_null_rows` (hält den Fix aus WP-57).
- Testzahl: vorher 3814 (Core 2742, UI 776 mit JS, Integration 296), nachher 3842 (Core 2750, UI 794, Integration 298) – 35 neue, 7 weniger durch das Zusammenlegen. `dotnet build -warnaserror` ohne Warnung, alle Tests grün (Integration mit Docker).

## Teil D – Doku
- **D1:** README mit Verbindungsgruppen, automatischem Neuverbinden, Zellen/Spalten markieren, Sortieren von Ergebnissen, beiden Diagrammen, Audit-Ansicht, Kopie in eine andere Verbindung und „kein Index“; `CLAUDE.md` §6 „Fertig heißt“ nennt das README. Ein Bild eines Diagramms fehlt noch (Backlog).
- **D2:** `.claude/rules/connections.md` (Öffnen, Halten, Schließen, Neuverbinden; Globs auf `Core/Connections`, `OracleSession*`, `WorkspaceSessions*`, `UI/State/Connections`, `App.xaml.cs`), `oracle.md` 13,7 → 10,1 KB, der doppelte Satz zum synchronen `Cancel` aufgelöst.
- **D3:** Namen aus dem Projekt des Nutzers in `ClrModelMappingTests`, ADR 0022 und den Display-Tests neutral ersetzt (Entscheidung 2); die Reihenfolge der Schlüssel in der Datei bleibt, wie die Tests sie erwarten.
- **D4:** `CLAUDE.md` 13,4 → 12,6 KB (Stand im Kopf, §5 auf einen Satz mit Verweis, §6 E2E auf das, was immer gilt); `.claude/rules/code-usage.md` aus `clr-model.md` (5,5 KB statt 8,8 KB; erreicht jetzt `UsageView`, `CodeUsage*`, `CodeEditorLauncher`). `ui.md:29` nicht gestrafft – die Zeile ist die Liste der Menübausteine, die jede neue Menüzeile braucht.
- **D5–D12:** Stände in `CLAUDE.md`, `roadmap.md`, `architecture.md`, `e2e-testing.md`; Releases 3.45.1/3.43.1 berichtigt; Index v4 bis WP-66, Formatversion 4, sieben Protokoll-Überschriften mit Release; `ClrModelFile` in den lokalen Pfaden (Architektur, Regel, Kommentar); `Relations/` und `tests/js/` in §4, `State/Copy` in `ui-state.md`; Release-Skill verweist auf `release.md`; zwei offene Fragen in der Roadmap, Überschrift ohne Bereich; zwei Nachträge in ADR 0030.
- **D13, D14:** NSubstitute-Falle und `Wait.UntilAsync` in `tests.md`; 27 resx-Kommentare für Platzhalter.

## E2E
(folgt)

## Zweites Review
(folgt)
