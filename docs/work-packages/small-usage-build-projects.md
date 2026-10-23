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
- **Ein Build zur Zeit** mit dem „Neu bauen“ des verknüpften Projekts (`ClrModelManager.IsBuilding`). Das Modell lädt danach der `BuildOutputWatcher` neu, wenn der Build das verknüpfte Projekt geändert hat – nicht der Build selbst (siehe Review). Das Ergebnis des Solution-Builds gehört der Verwendungsansicht (`CodeUsageService.LastSolutionBuild`), die Modellseite zeigt weiter nur den Build des Projekts.
- Nach dem Build wird der Index **auch nach einem Fehlschlag** neu gelesen: Was gebaut wurde, zählt. Der Fehler steht mit Exit-Code unter dem Hinweis, die Ausgabe hinter „Details“.
- Gleiche Schalter wie der Projekt-Build (ohne Build-Server, im Job-Objekt), Zeitgrenze 30 statt 10 Minuten.
- **Abbrechen** (Entscheidung des Nutzers nach dem ersten Stand): „Abbrechen“ neben „Solution wird gebaut…“ beendet `dotnet build` samt Kindprozessen (`DotNetCli.KillTree` über das Token), danach „Build abgebrochen.“ und der Index wird neu gelesen wie nach einem Fehlschlag. Das Abbrechen läuft auf dem Pool, weil `KillTree` alle Prozesse des Systems durchgeht. Der Projekt-Build „Neu bauen“ bleibt ohne Abbrechen (kurz, eigenes Thema).

## Gebaut
- Core: `ProjectBuild.References` (`BuildOutputLocator.FindAssembly`, volle Pfade der `ProjectReference`), `ProjectBuild.ReferencesUnknown`, `ProjectBuild.IsTestProject`, `TestFrameworks`, `CodeUsageProject.ReachesModel`, `CodeUsageBuilder.MarkModelReach`, `CodeUsageResult.Missing`; `IModelHostRunner.BuildSolutionAsync`, `ClrModelManager.BuildSolutionAsync` (gemeinsamer Weg mit `BuildAsync`), `CodeUsageService.BuildSolutionAsync`/`IsBuildingSolution`/`LastSolutionBuild`, `CancelSolutionBuild`/`IsCancellingSolutionBuild`/`SolutionBuildCancelled`.
- UI: `UsageBuildNote` (Schema; in `UsageView` und `UnusedReport`), `CodeUsageRows.UnusedTitle`, Tooltip der Projektzeile nennt nicht gebaute Projekte ohne Referenz eigens; Texte en/de; CSS `.usage-build-note`.
- Tests: Core `SolutionAndBuilderTests` (Reichweite über Referenzketten, Rückfall ohne Entity-Projekt, Testprojekte aus Paket und `IsTestProject`), `CodeUsageServiceTests` (Build der gewählten Solution in der Konfiguration, Index danach neu, auch nach Exit-Code 1, `LastBuild` des Projekts unberührt; Abbrechen über das Token, danach neu gelesen und ein weiterer Build möglich); UI `CodeUsageRowsTests`.

## E2E (2026-10-23)
Eigene Instanz (`--data-dir`, eigener WebView2-Ordner, deutsch, hell), eigener Container `ferret-buildproj` mit dem Beispielschema (danach entfernt), verknüpft mit einer Kopie des Beispielmodells im Scratchpad: `Shop.slnx` mit Entities, Data, `Shop.Services` (referenziert Data, liest `Artikel.HerstellerId`, schreibt `Artikel.KategorieId`) und `Tools` (ohne Referenz); gebaut nur Data.
- ARTIKEL › Verwendung: „4 Projekte · 2 nicht gebaut“, Hinweis „1 der Projekte“, Tooltip nennt `Shop.Services` als fehlend und `Tools` als „referenziert die Entities nicht“; 6 ungenutzt.
- „Projekte bauen“: Spinner und „Solution wird gebaut…“, danach Hinweis weg, „4 Projekte“, HERSTELLER_ID 1 Lesen, KATEGORIE_ID 1 Schreiben, 4 ungenutzt; `Tools` wurde mitgebaut.
- Fehlerfall (Compilerfehler in `Shop.Services`, dessen DLL entfernt): „Build der Solution fehlgeschlagen (Exit-Code 1)“, Ausgabe mit CS1525 hinter „Details“, Hinweis bleibt. Die Modellseite zeigt den Fehler nur im Bericht, nicht als Fehler ihres eigenen Builds.
- Log ohne `[ERR]`/`[WRN]`.
- **Abbrechen** (zweiter Lauf, gleicher Aufbau, `Shop.Services` mit einem Target, das 2 Minuten wartet): „Solution wird gebaut… Abbrechen“; nach dem Klick „Build abgebrochen.“ mit dem Knopf, Index neu gelesen. `dotnet build` und das wartende `PING.EXE` waren beendet, kein MSBuild-Knoten des Builds blieb übrig (übrig waren nur Knoten mit `nodeReuse:true` von eigenen Builds davor). Danach ohne das Target erneut gebaut: Hinweis weg, 4 ungenutzt. Log ohne `[ERR]`/`[WRN]`.
- **Nicht mit echter Windows-Eingabe geprüft:** Der `SendInput`-Klick auf „Projekte bauen“ kam nicht im DOM an (kein `mousedown`, wie im Leitfaden beschrieben); der Knopf per CDP-Eingabe (Stufe 2) bedient. Er ist ein normaler `@onclick`-Knopf.

## Code-Review (2026-10-23)
Review-Lauf über den PR, neun Funde, alle behoben:
- **Referenzen, die die Projektdatei nicht verrät:** Ein Pfad mit MSBuild-Eigenschaft (`$(SolutionDir)…`) wurde wörtlich genommen, Referenzen aus einer `Directory.Build.props/targets` gar nicht gesehen – das Projekt galt als „kann das Modell nicht nutzen“, der Hinweis verschwand zu Unrecht. Jetzt `ProjectBuild.ReferencesUnknown` (Eigenschaft oder Item im Pfad, Datei fehlt, `ProjectReference` in der nächsten `Directory.Build.props`/`.targets`); ein solches Projekt gilt als erreichend.
- **Projekte außerhalb der Solution:** Eine Kette über ein Projekt, das nicht in der Solution steht, endete dort. Eine Referenz auf ein solches Projekt gilt jetzt als erreichend (seine Referenzen werden nicht gelesen). Beides geht in die vorsichtige Richtung: Der Hinweis steht höchstens, bis das Projekt gebaut ist.
- **„Wird abgebrochen…“ konnte hängen bleiben:** Lief das Abbrechen zwischen dem Ende des Builds und seinem Aufräumen, blieb der Zustand gesetzt, spätere Builds ließen sich nicht abbrechen. Laufender Build und Abbruch stehen jetzt zusammen unter der Sperre, `IsBuildingSolution` ergibt sich aus dem laufenden Build statt aus einem zweiten Feld.
- **Abbrechen während des Modell-Neuladens:** Der Solution-Build lud das Modell danach ohne Cache neu (beim Nutzer bis 12 s), währenddessen stand „Abbrechen“ da und wirkte nicht. Er lädt es nicht mehr selbst; hat er das verknüpfte Projekt geändert, tut es der `BuildOutputWatcher` wie nach einem Build in Visual Studio.
- **`IsTestProject` unter einer Bedingung** oder mit Eigenschaft (`$(IsUnitTest)`) entscheidet nicht mehr; dann zählen die Pakete.
- **Eine Liste der Test-Frameworks** (`TestFrameworks`, Assembly- und Paketnamen) für Scanner und Projektdatei, damit ein gebautes und dasselbe nicht gebaute Projekt übereinstimmen.
- **Tooltip „ungenutzt“** wird einmal je Rendern berechnet statt je Zeile; der Hinweis rendert nur, solange seine Ansicht sichtbar ist (`Shown`, wie die Ansicht selbst seit A35).

## Offen / Fragen
- Baut Visual Studio dieselbe Solution gleichzeitig, können sich beide um `obj/` streiten (wie beim Projekt-Build).
