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

## Grenzen
- Der Fortschritt ist ungefähr: Ein Projekt mit mehreren Ziel-Frameworks zählt mit dem ersten fertigen Framework; ein Projekt der Solution, das keine Ausgabe schreibt (etwa ein SQL-Projekt), zählt nie – der Balken erreicht dann nicht das Ende, der Build endet trotzdem normal.
- Bei 0 Kandidaten bleibt der Satz „Keine Methode im Code passt …“ unverändert; der Hinweis darüber nennt die fehlenden Projekte (anders als im Plan, wo der Satz selbst ergänzt werden sollte – doppelt gesagt).
