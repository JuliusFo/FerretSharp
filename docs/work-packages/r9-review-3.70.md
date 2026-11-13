# R9 Fehler, Struktur, Tests und Doku nach dem Gesamt-Review 3.70.0
Teil A bis D des Gesamt-Reviews (`docs/review-3.70.0.md`) in **einem** Paket und einem Release (Entscheidung 1 des Nutzers, 2026-11-12). Branch `wp/r9-review-3.70` mit dem Review-Commit vorne, 2026-11-12, Release 3.71.0. Gebaut in einer Session ohne weitere Agenten. Reihenfolge: reine Verschiebungen, Teil A mit Tests, B, C, D, E2E. Die Entscheidungen des Nutzers stehen im Review.

## Verschiebungen
- `AuditTemplateSource.GroupEntry` → `ConnectionSearch.GroupEntry` (B9); `SqlBindListsTests` zur Quelle nach `Query/SqlEditor` (C14).
- `TableKinds.ObjectType()` ersetzt fünf Switches (B7); `QueryDesignPanel` bricht über `CancelOffThread` ab (B11), `ConditionBar` nutzt `SqlScript.OneLine` (B8); `.mismatch-toolbar` weg, Font-Stack als Token `--sans`, `ScriptPaths.RelativeOf`/`ReadSession.HasSession` `internal` (B10).

## Teil A – Fehler und Sicherheit
- **A1:** `ReadSession.Reset()` gibt nur die Lese-Session auf (im Hintergrund geschlossen); `RunProgress` bekommt das Ziel des Trennens (`OnResetReads`), das Grid setzt es im Zeitreise-Tab, ohne Verlust-Rückfrage und ohne `SessionWatch` der Workspace-Session; der Diff-Tab bietet nach „Abbrechen“ „Lese-Session trennen“. `ReadSession.ReadAsync` endet mit dem Abbruch auch, wenn der Treiber synchron blockiert (`WaitAsync` außerhalb des `Task.Run`). Die Abfrage eines Zeitpunkts hat 30 s (`PointLimit`), danach wird die Lese-Session aufgegeben und gemeldet. `WorkspaceLifecycle` bekommt einen `TimeProvider` (C11). Tests: Core (Lesen ignoriert den Abbruch, `Reset`), UI (Zeitpunkt über die Grenze).
- **A2:** `WebViewCrashGuard` (App) behandelt `ProcessFailed` (Entscheidung 3): einmal neu laden, ein zweiter Absturz binnen 2 min oder ein beendeter Browser-Prozess fragt nativ nach dem Neustart; beim Schließen mit toter Seite native Frage Commit/Rollback/Abbrechen über `ExitGuard.FinishAll` → `WorkspaceLifecycle.FinishAllAsync`. Test: `FinishAllAsync` über zwei Verbindungen; E2E siehe unten. Ein Neuladen behält Workspaces, Sessions und Geschriebenes (Scopes am Singleton `ConnectionHub`), verwirft aber ungeschriebene Grid-Änderungen – deshalb fragt der Host vorher (Nachtrag Code-Review).
- **A3:** Gemessen gegen Oracle 23 (eigener Container): `MOVE INCLUDING ROWS WHERE` 10 → 5 Zeilen, kein Papierkorb; `DROP TYPE … FORCE` lässt die Spalte verschwinden (ohne `FORCE` ORA-02303); `ALTER TYPE … DROP ATTRIBUTE` mit `CASCADE` **und** mit `INVALIDATE` verliert die Werte (ohne beides ORA-22312). Alle drei auf der Nie-Liste (`SqlClassifier`), `INVALIDATE` zusätzlich zur Frage, weil gemessen gleich – **bitte bestätigen**. ADR 0019, `ddl.md`.
- **A4/B2:** `UserFolderPaths.SourceFiles` (ohne Links) für `WorkCloneMapping` und `CodeScan`; Test mit Junction. Die Tests legen Junctions über `TestFolder.Junction` an.
- **A5/B1:** `OwnSession.OpenAsync` für Lese-Session, Workspaces, Serverlast und Schema-Vergleich (dieser jetzt mit `SessionOpening`); ORA-01017 vergisst ein nur für den Lauf eingegebenes Passwort, `MissingPasswordException` mit `ConnectionText.NoPasswordFor`. Test: Serverlast fragt nach ORA-01017 statt erneut zu senden (Gegenprobe: ohne das Vergessen scheitert er). `ActiveConnection` behält ihren Weg (Schritte, Wiederholungen).
- **A6:** `ShouldRender` nur für den aktiven Grant- und Synonym-Tab.
- **A7:** Der DDL-Dialog des Vergleichs fragt während eines Laufs nicht erneut und nennt „Läuft auf …“. **A8:** Schreiben erst nach der Planungspause; Abbruch nur bis zur ersten geschriebenen Datei. **A9:** `OneLine` über `ReplaceLineEndings`, auch für Parameternamen; Theory mit `\n`, `\r\n`, U+2028, NEL (vorher alle rot).
- **A10:** **Nicht nachgestellt.** `V$SQL` hielt im Container weder `CREATE USER`/`ALTER USER … IDENTIFIED BY` noch `CREATE DATABASE LINK` noch andere DDL (nur das `LOCK TABLE`). Ein PL/SQL-Block mit dem Passwort im Literal (`EXECUTE IMMEDIATE '…'`) stünde dort, fängt `SqlRedaction` (Token-basiert) aber nicht – daher keine Änderung.
- **A11/A12:** `--untracked-files=all`, Umbenennungen richtig gelesen (`CodeTarget.StatusPaths`), Ausgabe als UTF-8. A12 im Testprozess nicht nachgestellt (der Codepage-Provider ist dort nicht registriert); der Schalter ist trotzdem gesetzt wie bei `DotNetCli`.
- **A13:** „Neu bauen“ zeigt „Wartet, bis ein anderer Build … fertig ist“ und lässt sich abbrechen (`IModelHostRunner.BuildAsync(onStart)`, `ClrModelManager.CancelBuild`). Test mit einem Runner, der wartet.
- **A14:** Target `CheckBundledRuntime` in `Directory.Build.props` (Entscheidung 6): self-contained Publish unter `MinimumBundledRuntime` 10.0.14 bricht ab; lokal mit SDK 10.0.401 (Runtime 10.0.12) geprüft. **Lokale Release-Builds brauchen jetzt ein SDK-Update.**
- **A15/A16:** Die Hänger-Wächter stoppen als Erstes in `OnExit`; vswhere mit Grenze auch beim Lesen.
- **A17:** Gemessen: `CREATE INDEX … ONLINE` wartete 164 s auf eine fremde offene Transaktion, auch über den Abbruch hinweg; ohne `ONLINE` sofort ORA-00054. Der Schemaweg nimmt die Tabelle jetzt auch vor `CREATE INDEX` und vor `ALTER INDEX … ONLINE` (Tabelle aus `ALL_INDEXES`). Integrationstest erweitert.
- **A18:** IN-Liste der Statement-Info auf `LookupSizes` aufgefüllt. **A19:** ORA-22924 als Undo-Fehler – **nicht nachgestellt** (LOB-Versionen ließen sich im Container nicht verdrängen), nach Oracles Fehlertext („snapshot too old“). **A20, A21, A24:** Format, gemerkte Schnellwahl, Uhrzeit in der Kultur des Nutzers. **A22:** `ProjectCodeChangeView` über `Fire`; `TabBar` ist keine `FerretComponent`, ihr `InvokeAsync` setzt nur ein Feld – gelassen.
- **A23/B4:** `TextFileFormat.LineBreakOf` (Mehrheit) für Skripte und C#-Dateien, `AtomicFile.CreateInUserFolderAsync` für neue Skripte und neue C#-Dateien. BOM/Kodierung bleiben getrennt: Skripte nehmen ANSI an, C#-Dateien lehnen es ab.
- **A25:** `Invoke-RealClick.ps1` verlangt `-Root` und prüft den Pfad des Prozesses.

## Teil B – Struktur
- B1, B2, B4 (Teil), B7–B11 siehe oben. **B3:** `QuietTimer` und `FileWatch.Create` (64 KB Puffer für alle) in den drei Watchern; `BuildOutputWatcher` behält den Namen (seine `deps.json`-Regel gehört zum Build). **B5:** `CodeProject` (`Of`, `IsSdk` über XML, `Nullable`). **B6:** `GrantActions` mit Tests. **B12:** `CoreText.ErrorInLine`, `ConnectionText.ReadSessionNotConnected`; die „weitere“-Texte bleiben getrennt (deutsch „weitere“/„weiterer“). B8 für `ReportPage`/`PlanTable` nicht nötig: beide formatieren schon in der Kultur.

## Teil C – Tests
- **C1** `CodeChangeGuardTests` (Überlappung, mehr Syntaxfehler, kaputte neue Datei, Plan mit Problemen, Datei als Link, UTF-16, binär, zu groß). **C2** `EntityCodeChangeAttributeTests` und ein Test zum Kontext (using, DbSet-Name, Hinweis). **C3** Struktur geändert (Gegenprobe rot ohne die Prüfung). **C4** Grenzen der Skriptbibliothek (`FileLimit`/`EntryLimit`), Timer über `TimeProvider`. **C5** `LoadSinceLoadAsync` samt Grenze. **C6** (Teil) Zeitreise-Tab lädt nach einem Schreiben nicht neu. **C7** Plan eines fremden Cursors nur, solange Oracle ihn hält. **C8** vier Schleifen → `Wait.UntilAsync`; die drei Integrationstests behalten ihre (DB-Abfragen, längere Grenzen). **C9** `WaitAsync(Wait.Limit, …)`. **C10** `SafeDeleteTests` liest `.js`/`.mjs`.
- **Überflüssig (C16/C17):** `Theme_round_trips` entfernt (Gegenprobe: `[JsonIgnore]` auf `Theme` → `Every_setting_round_trips` und `File_is_versioned_and_readable` rot); vier Gruppen zu `[Theory]` (Gegenprobe Pins vor den Schlüssel → Fall `PREIS,ARTIKEL` rot). Learnings in `tests.md` › „Redundante Tests“. Core-Tests vor dem Trimmen 3496, danach 3495.
- Nicht umgesetzt, im Backlog: C6 (Rest: `ExplainActualAsync` mit `AS OF`, Abbruch des Zurücksetzens), C9 (`--long-running` in der CI, Option nicht geprüft), C12, C13.

## Teil D – Doku
- D1/D9 Release-Stand im Index und in sechs Protokollen, Reihenfolge, v4-Zeile; Schritt in Release-Skill und `release.md` (Entscheidung 7, dazu „erst `main` ziehen“). D2/D3 neue Regeln `flashback.md`, `server-load.md`, `app-host.md`, toter Glob in `ddl.md` (`oracle.md` 16,9 → 11,9 KB, `ui.md` 17,9 → 13,4 KB). D4 `CLAUDE.md`, D5 Tastentabelle, D6, D7 Backlog, D8 README-Sätze, D10 Checkliste, D11 44 Kommentare und ein Test, D12 ADR 0038/0039 bestätigt (Entscheidung 4), 0040 aus dem Protokoll von WP-69, D13 neutral.
- Nicht umgesetzt: Regeln für `Core/Relations`, `Core/AppTrace`, `Core/Settings` (D2-Rest), neue README-Bilder (D8, Backlog).

## E2E
Eigene Testinstanz (`tools/e2e`, `--data-dir` im Scratchpad, `--lang=de`), eigener Container `ferret-r9` (Beispielschema, Port 1543, danach entfernt); das Passwort nur für den Lauf, nicht im Credential Manager. `Start-TestInstance.ps1` lief wieder in die Falle mit `Get-NetTCPConnection` (`e2e-testing.md`); Port und Prozess per Pfad geprüft.
- **A2:** `Page.crash` per CDP → im Log „WebView process failed: RenderProcessExited, reason Crashed“ und „Reloading the page once after a crash“, die Seite war danach wieder da. Der zweite Absturz (native Frage) nur im Code geprüft – die Frage blockiert die Instanz.
- **C15:** Griff des Abfrage-Panels mit echter Maus (`Invoke-RealClick.ps1 -Root …`, das neue `-Root` aus A25) 460 → 580 px, in den Einstellungen gespeichert. Das Wege-Panel ließ sich per CDP nicht öffnen (die Auswahl in „Weg zu …“ nahm die Eingabe nicht an) – Backlog.
- Im Log der Instanz sonst keine Warnung und kein Fehler.

## Tests
Vor R9 (Snapshot `213bd507`): 4848 (Core 3438, UI 1044 inkl. JS, Integration 366).
Nach R9 samt Code-Review: 4925 (Core 3497, UI 1062 inkl. JS, Integration 366), `dotnet build -warnaserror` ohne Warnung. Der Integrationstest `Index_on_a_table_another_session_has_written_to_fails_at_once` erwartet jetzt `TableBusyException` statt ORA-00054 (Folge von A17; der Designer zeigt die Sperrhalter für beide).

## Nachtrag: Code-Review (2026-11-12)
Zweiter Durchgang mit `/branch-code-review` (eigener Agent), jeder Befund am Code nachgeprüft. Entscheidung des Nutzers: „Alle, 1 nativ fragen“.
1. **M** Das automatische Neuladen nach einem Absturz verwarf ungeschriebene Grid-Änderungen und die Undo-Historie still: Sie hängen am `ShellState` der Seite, nicht am `ConnectionHub` (die Annahme aus Entscheidung 3 stimmte nur für Sessions, Transaktionen und geschriebene Änderungen). Behoben: Mit ungeschriebenen Änderungen fragt der Host nativ – schreiben (ohne Commit) und neu laden, verwerfen und neu laden, oder die Seite lassen (das Schließen fragt dann). Ein Renderer, der nur nicht antwortet, wird nicht mehr neu geladen. `CrashReload` in UI mit Tests.
2. **M** Ein zweites ✕ während des nativen Commits schloss mitten im Commit. Behoben: `WebViewCrashGuard.Busy` hält jedes weitere Schließen an, auch während vor dem Neuladen geschrieben wird.
3. **M** Der Hinweis „Wartet auf anderen Build“ erschien nie (ohne `Changed`). Behoben, Test ohne den Fix rot.
4. **K** „Abbrechen“ auch beim Solution-Build der Code-Verwendung, dort wirkungslos. Behoben (`IsBuildingProject`), mit Test.
5. **K** `INVALIDATE` stand als Entscheidung des Nutzers in Regel und Code – jetzt „Vorschlag (nicht bestätigt)“.
6.–9. **K** Kommentar in `FinishAllAsync`, `ExitGuard`-Rückrufe beim Entsorgen der Shell, Uhr von `DisplayColumnSource`, Testzahlen – behoben.

Kontrolle der Fixes durch denselben Agenten, Entscheidung des Nutzers „Ja, alle sechs“ (alle K): Mehrzahl der neuen Frage, `FlushAllAsync` meldet Änderungen einer verlorenen Verbindung als nicht geschrieben, Ausnahmen der Abläufe nach dem Ereignis ins Log (`Logged`), eine Sperre im Wächter statt in `MainWindow`, die Entscheidung vor dem Neuladen testbar (`CrashReload`), Test „Solution-Build ohne Abbrechen“, Testzahlen.

Dritter Durchgang derselben Kontrolle, Entscheidung des Nutzers „Ja, beide“ (K): Das Neuladen respektiert die Sperre `Busy` eines laufenden nativen Commits; `CrashReload.DecideAsync` gibt Wahl und Anzahl zurück, das Log nennt wieder, ob ungeschriebene Änderungen verworfen wurden.

Vorher schon da (Backlog): Der Schemaweg hält die Tabelle nur bis zum impliziten Commit vor dem DDL. Ein Renderer, der dauerhaft nicht antwortet, wird nicht behandelt; mit offener Arbeit lässt sich das Fenster dann nicht schließen.
