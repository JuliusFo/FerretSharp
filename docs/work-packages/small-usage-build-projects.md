# Klein: Nicht gebaute Projekte in „Verwendung“, „Projekte bauen“ → Release offen

Branch `feat/usage-build-projects` (von `main`), 2026-10-23. Nachtrag zu WP-40 (ADR 0030).

## Anlass
Der Nutzer: Sind Projekte der Solution nicht gebaut, ist „ungenutzt“ irreführend – die Spalten sind nur ungenutzt, weil der Index ihre Assemblies nicht lesen konnte. Bisher stand das nur grau im Kopf („12 Projekte · 9 nicht gebaut“), die Markierung tat so, als wäre alles gelesen.

Entscheidungen des Nutzers:
- Ein Knopf „Projekte bauen“, der **die ganze Solution** baut (ein `dotnet build`), nicht nur die fehlenden Projekte über einen Solution-Filter.

Ohne Rückfrage entschieden (einfachste Variante, Fragen unten):
- **Nur Projekte, die das Modell erreichen können, machen „ungenutzt“ unsicher:** das Projekt, dessen Assembly einen Entity-Typ definiert, und jedes, das es über `ProjectReference` erreicht (auch über andere Projekte). Die Referenzen stehen in den `.csproj` und sind auch ohne Build bekannt. Ein Tool oder Installer ohne diese Referenz kann die Properties nicht nutzen – gebaut oder nicht. Findet sich kein Projekt mit Entity-Typen (Entities außerhalb der Solution, Entity-Projekt nicht gebaut), gelten alle als möglich. Ein Projekt mit unlesbarer Projektdatei gilt ebenfalls als möglich.
- **Hinweis statt Ausblenden:** Die Markierung „ungenutzt“ bleibt; darüber steht ein gelber Hinweis („Nicht gebaut: 1 der Projekte, die das Modell nutzen können …“, die Namen im Tooltip) mit dem Knopf, und der Tooltip der Markierung sagt „Nicht sicher“. In „Verwendung“ und im Bericht auf der Modellseite; in der Spalten-Ansicht nur der Tooltip.
- **Testprojekte** (Wunsch des Nutzers nach dem ersten Stand): Ein nicht gebautes Testprojekt löst keinen Hinweis aus – „ungenutzt“ zählt Tests ohnehin nicht. Erkannt aus der Projektdatei, weil der Scanner es erst an der Assembly sieht: `IsTestProject` gewinnt in beide Richtungen, sonst ein Paket, dessen Name mit `xunit`, `NUnit`, `MSTest`, `TUnit`, `Microsoft.Testing.Platform` oder `Microsoft.NET.Test.Sdk` beginnt. Pakete aus einer `Directory.Build.props` sieht das nicht; ein solches Testprojekt zählt wie bisher als fehlend. Der Tooltip der Projektzeile nennt es „ein Testprojekt“.
- **Veraltete Builds** (Quelle neuer als die DLL) lösen keinen Hinweis aus: Das ist beim Entwickeln der Normalfall und stand schon im Kopf.
- **Ein Build zur Zeit** mit dem „Neu bauen“ des verknüpften Projekts (`ClrModelManager.IsBuilding`); danach wird das Modell neu geladen wie dort. Das Ergebnis des Solution-Builds gehört der Verwendungsansicht (`CodeUsageService.LastSolutionBuild`), die Modellseite zeigt weiter nur den Build des Projekts.
- Nach dem Build wird der Index **auch nach einem Fehlschlag** neu gelesen: Was gebaut wurde, zählt. Der Fehler steht mit Exit-Code unter dem Hinweis, die Ausgabe hinter „Details“.
- Gleiche Schalter wie der Projekt-Build (ohne Build-Server, im Job-Objekt), Zeitgrenze 30 statt 10 Minuten.

## Gebaut
- Core: `ProjectBuild.References` (`BuildOutputLocator.FindAssembly`, volle Pfade der `ProjectReference`), `ProjectBuild.IsTestProject`, `CodeUsageProject.ReachesModel`, `CodeUsageBuilder.MarkModelReach`, `CodeUsageResult.Missing`; `IModelHostRunner.BuildSolutionAsync`, `ClrModelManager.BuildSolutionAsync` (gemeinsamer Weg mit `BuildAsync`), `CodeUsageService.BuildSolutionAsync`/`IsBuildingSolution`/`LastSolutionBuild`.
- UI: `UsageBuildNote` (Schema; in `UsageView` und `UnusedReport`), `CodeUsageRows.UnusedTitle`, Tooltip der Projektzeile nennt nicht gebaute Projekte ohne Referenz eigens; Texte en/de; CSS `.usage-build-note`.
- Tests: Core `SolutionAndBuilderTests` (Reichweite über Referenzketten, Rückfall ohne Entity-Projekt, Testprojekte aus Paket und `IsTestProject`), `CodeUsageServiceTests` (Build der gewählten Solution in der Konfiguration, Index danach neu, auch nach Exit-Code 1, `LastBuild` des Projekts unberührt); UI `CodeUsageRowsTests`.

## E2E (2026-10-23)
Eigene Instanz (`--data-dir`, eigener WebView2-Ordner, deutsch, hell), eigener Container `ferret-buildproj` mit dem Beispielschema (danach entfernt), verknüpft mit einer Kopie des Beispielmodells im Scratchpad: `Shop.slnx` mit Entities, Data, `Shop.Services` (referenziert Data, liest `Artikel.HerstellerId`, schreibt `Artikel.KategorieId`) und `Tools` (ohne Referenz); gebaut nur Data.
- ARTIKEL › Verwendung: „4 Projekte · 2 nicht gebaut“, Hinweis „1 der Projekte“, Tooltip nennt `Shop.Services` als fehlend und `Tools` als „referenziert die Entities nicht“; 6 ungenutzt.
- „Projekte bauen“: Spinner und „Solution wird gebaut…“, danach Hinweis weg, „4 Projekte“, HERSTELLER_ID 1 Lesen, KATEGORIE_ID 1 Schreiben, 4 ungenutzt; `Tools` wurde mitgebaut.
- Fehlerfall (Compilerfehler in `Shop.Services`, dessen DLL entfernt): „Build der Solution fehlgeschlagen (Exit-Code 1)“, Ausgabe mit CS1525 hinter „Details“, Hinweis bleibt. Die Modellseite zeigt den Fehler nur im Bericht, nicht als Fehler ihres eigenen Builds.
- Log ohne `[ERR]`/`[WRN]`.
- **Nicht mit echter Windows-Eingabe geprüft:** Der `SendInput`-Klick auf „Projekte bauen“ kam nicht im DOM an (kein `mousedown`, wie im Leitfaden beschrieben); der Knopf per CDP-Eingabe (Stufe 2) bedient. Er ist ein normaler `@onclick`-Knopf.

## Offen / Fragen
- Kein Abbrechen eines laufenden Solution-Builds (wie beim Projekt-Build); die Zeitgrenze sind 30 Minuten.
- Baut Visual Studio dieselbe Solution gleichzeitig, können sich beide um `obj/` streiten (wie beim Projekt-Build).
