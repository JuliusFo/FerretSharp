# Klein: Herkunft im Code mit Hinweis auf nicht gebaute Projekte, Fortschritt von „Projekte bauen“

Branch `feat/code-origin-build-note` (von `main`), 2026-11-09. Baut auf WP-40 (Verwendung im Code, „Projekte bauen“) und WP-76 (Herkunft im Code).

## Anlass (Ideen des Nutzers, 2026-11-09)
- In der Serverlast fand „Herkunft im Code“ überall 0 Stellen – viele Projekte der Solution waren nicht gebaut, die Seite sagte das nicht. Wunsch: eine Meldung auf der Server-Load-Seite, wenn die Projekte noch nicht gebaut sind.
- „Projekte bauen“ zeigte nur „Solution wird gebaut…“. Wunsch: eine bessere Ansicht, eventuell ein Fortschritt, wie weit der Build über alle Projekte ist.

## Entscheidungen des Nutzers
- Plan freigegeben, „die einfache Variante reicht erstmal“ (2026-11-09): „n von m Projekten“ mit dem zuletzt fertigen Projekt, keine Liste aller Projekte mit Stand.

## Gebaut
- **Core:**
  - `SolutionBuildProgress` (Fertig, Gesamt, zuletzt fertig, `Fraction`) und `SolutionBuildTracker`: liest die Ausgabe von `dotnet build -v minimal`, in der jedes Projekt nach dem Schreiben seiner Ausgabe „`Name -> Pfad`“ meldet (auch wenn es aktuell war). Es zählen nur Namen der Projekte der Solution, jeder einmal (mehrere Ziel-Frameworks melden mehrere Zeilen); Shared Projects (`.shproj`) bauen nie und zählen nicht mit.
  - `ModelHostRunner.BuildSolutionAsync` baut die Solution mit `-v minimal` statt `-v q` (nur den Solution-Build; der Build des verknüpften Projekts bleibt still) und meldet den Fortschritt über `IProgress<SolutionBuildProgress>`; die Projekte liest es aus `SolutionFile.Projects` (unlesbar: ohne Gesamtzahl, ohne Balken). `ClrModelManager.BuildSolutionAsync` reicht ihn durch.
  - `CodeUsageService.BuildProgress`: Stand des laufenden Builds, `Changed` bei jedem Schritt, null davor und danach; Meldungen eines beendeten Builds werden verworfen.
- **UI:**
  - `UsageBuildNote`: während des Builds ein Balken, „12 von 40 Projekten“ und „zuletzt: Name“ – überall, wo der Hinweis steht (Verwendung im Code, Bericht „Ungenutzt“, Herkunft im Code). Neuer Parameter `MissingNote` für den Satz zu den fehlenden Projekten.
  - `CodeOrigins`: im Kopf neben der Zahl ein Warnsymbol mit Tooltip, wenn Projekte fehlen (sichtbar auch zugeklappt); aufgeklappt der Hinweis mit „Projekte bauen“ und eigenem Satz („Ihre Methoden fehlen hier – die Liste kann unvollständig sein.“).
  - Texte `ModelViewText.Usage_BuildProgress`, `Usage_BuildLast`, `CodeOrigin_MissingNote` (Englisch, Deutsch); CSS `.build-progress`, `.code-origin-missing`.
- **Tests:** Core `SolutionBuildTrackerTests` (einmal je Projekt, Groß-/Kleinschreibung, Leerzeichen im Namen, Fehler-/Warnzeilen und fremde Projekte zählen nicht, ohne Projekte kein Anteil), `CodeUsageServiceTests.Building_the_solution_…` (Fortschritt während des Builds, danach weg); Integration `SolutionBuildTests` (echter `dotnet build` einer Solution mit zwei Projekten, ohne Pakete).

## E2E
2026-11-09, eigener Container (`New-SampleDb.ps1 -Name ferret-e2e-buildnote -Port 1541`, `SELECT_CATALOG_ROLE` per sysdba im Container), Testinstanz deutsch/dunkel, Verbindung als `connections.json`, Passwort nicht gespeichert; Klicks mit echter Windows-Eingabe (Probe vorher bestanden). C#-Projekt: Kopie des Beispielmodells im Scratchpad mit `Shop.slnx` und zwei nicht gebauten Projekten (`Shop.Services` mit `Where(k => k.Name.StartsWith(…))`, `Shop.Jobs` mit `Count(k => k.Gesperrt)`). Last per SQL*Plus im Container.
- Statement `SELECT K.* FROM KUNDEN K WHERE K.NAME LIKE 'M%'` im Dialog: „Herkunft im Code 0“ mit Warnsymbol, aufgeklappt „Nicht gebaut: 2 der Projekte …“ mit „Projekte bauen“.
- „Projekte bauen“ (Mitschrift per MutationObserver): 0 → 1 → 2 → 3 → 4 von 4 Projekten, Balken 0/25/50/75/100 %, „zuletzt:“ Entities, Data, Services, Jobs; danach Hinweis weg, „Herkunft im Code 2“ mit `KundenService.Suche` (voll) vor `SperrJob.Gesperrte`.
- Log ohne `[ERR]`/`[WRN]`. Beobachtet: Im Dialog bricht „Abbrechen“ neben dem langen Satz, Balken und Zähler in eine zweite Zeile um (behoben im Review, siehe unten).

## Grenzen
- Der Fortschritt ist ungefähr: Ein Projekt mit mehreren Ziel-Frameworks zählt mit dem ersten fertigen Framework; ein Projekt der Solution, das keine Ausgabe schreibt (etwa ein SQL-Projekt), zählt nie – der Balken erreicht dann nicht das Ende, der Build endet trotzdem normal.
- Projekte, die die Solution-Konfiguration nicht baut (in `.sln` ohne `.Build.0`, in `.slnx` mit `<Build Project="false" />`, oft Deploy-, Docker- oder Testprojekte), zählen in die Gesamtzahl und melden sich nie fertig: Der Balken endet dann z. B. bei 38 von 40. `SolutionFile.Projects` liest diese Schalter nicht (Review).
- Bei 0 Kandidaten bleibt der Satz „Keine Methode im Code passt …“ unverändert; der Hinweis darüber nennt die fehlenden Projekte (anders als im Plan, wo der Satz selbst ergänzt werden sollte – doppelt gesagt).

## Nachtrag: Code-Review (2026-11-09)
Review per `/branch-code-review` (eigener Reviewer-Agent): keine S/M-Befunde, vier K. Entscheidung des Nutzers: „Alle + Umbruch“.
1. K Ein von der Umgebung erzwungener Terminal-Logger (`MSBUILDTERMINALLOGGER=on`, vom Kind geerbt) ersetzte die Zeilen „`Name -> Pfad`“ durch farbige Live-Zeilen – der Balken stand bei 0. **Behoben:** `DotNetCli.StartInfo` setzt `MSBUILDTERMINALLOGGER=off` für jeden dotnet-Start (nicht `-tl:off`: ein SDK 7 lehnt den Schalter ab); Test `Dotnet_runs_without_the_terminal_logger_…`.
2. K Die erste Meldung „0 von N“ kam, bevor der Build die app-weite Sperre hatte – wartete er auf den einer anderen Verbindung, stand der Balken schon da. **Behoben:** erste Meldung erst beim Start (`onStart` in `OneBuildAtATimeAsync`); Test `A_build_of_the_solution_waiting_for_another_reports_no_progress`.
3. K Grenze „Projekte ohne Build in der Solution-Konfiguration“ und der beobachtete Umbruch fehlten in der Doku. **Behoben:** unter „Grenzen“; den Umbruch dazu im Code behoben: Fortschritt und „Abbrechen“ stehen in einer eigenen Zeile unter dem Hinweis (`.usage-build-run`).
4. K Kein Test, dass Meldungen eines beendeten Builds verworfen werden. **Behoben:** `CodeUsageServiceTests.Building_the_solution_…` meldet nach dem Ende und prüft, dass nichts ankommt.
- Gegenprobe: Ohne die drei Fixes im Core scheitern genau die vier neuen Prüfungen.
- Vorher schon da: ein nach einem gescheiterten Test stehengebliebener `FakeModelHost` (`hangs-in-run`), der `tests/FerretSharp.FakeModelHost/bin` sperrte – in den Backlog; den Prozess (aus dem eigenen Testlauf dieser Session) beendet.
