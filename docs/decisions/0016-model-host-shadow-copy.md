# ADR 0016: ModelHost läuft aus einer Schattenkopie, ein Watcher übernimmt neue Builds

- Status: akzeptiert
- Datum: 2026-10-08
- Ergänzt: ADR 0009 (ModelHost), ADR 0011 (LINQ-Konsole)

## Kontext

Der ModelHost (ADR 0009) lud die DLLs des verknüpften Projekts direkt aus dessen `bin/<Configuration>/<tfm>`. Windows sperrt geladene Assemblies; solange die LINQ-Konsole lief (dauerhafter Prozess, ADR 0011) – und für die Dauer jedes Modell-Exports –, scheiterte jeder Build des Projekts oder seiner referenzierten Projekte mit „The file is locked by: .NET Host“. Dazu kamen zwei Folgeprobleme:

- Ein neuer Build wurde nicht bemerkt: Modell und Konsole blieben auf dem alten Stand, bis der Nutzer neu verband oder die Konsole neu startete.
- Ein erster Workaround (Kopie der Build-Ausgabe bei jedem Start) kopierte große Ausgabeordner jedes Mal vollständig, und der Start der Konsole hielt die Oberfläche spürbar an.

Erwogen:

1. **Schattenkopie** der Build-Ausgabe, aus der der Host läuft.
2. **Eigenes „Halte“-Projekt** (Idee aus dem Team): ein separates Projekt/Repo, dessen Ausgabe FerretSharp referenziert, damit die echte Ausgabe nie gesperrt ist. Es müsste angelegt, auf dem Branch des Nutzers gehalten und gebaut werden – das ist eine Schattenkopie mit zusätzlichem Build und zusätzlicher Pflege. Verworfen: Die Schattenkopie mit Watcher erreicht dasselbe ohne zweites Projekt.
3. **Laden ohne Dateihandle** (`AssemblyLoadContext.LoadFromStream` aus Byte-Arrays, `MetadataLoadContext`): `MetadataLoadContext` kann keinen Code ausführen, der Modellaufbau braucht aber `OnModelCreating`, Converter und Konventionen des Projekts. `LoadFromStream` müsste jede Abhängigkeit (Entities, Satelliten-Assemblies, Pakete aus `bin`) selbst auflösen und verliert Pfade (Ressourcen, `Assembly.Location` im Code des Nutzers); NuGet-Pakete kommen ohnehin aus dem Paket-Cache. Verworfen.
4. **Kurzlebiger Host pro Export** statt dauerhafter Konsole: löst das Sperren nur für den Export; die Konsole braucht den geladenen DbContext dauerhaft (12 s Modellaufbau beim Nutzer). Verworfen.

## Entscheidung

Weg 1, mit Watcher:

- **`BuildOutputShadow`** (Core): Der Host läuft immer aus einer Kopie des Ausgabeordners (Assembly, deps.json, referenzierte Projekte, Satelliten-Assemblies), sowohl für den Modell-Export als auch für die Konsole. Ablage unter `%TEMP%\FerretSharp\shadow\<Assembly>-<Hash des Ausgabeordners>\<n>`.
  - Pro Ausgabeordner gibt es einige **Slots**. Ein freier Slot wird wiederverwendet und **inkrementell** abgeglichen: kopiert werden nur Dateien, deren Größe oder Schreibzeit abweicht, entfernte Dateien werden gelöscht. Die Schreibzeit der Quelle wird erst nach vollständigem Kopieren gesetzt – eine unterbrochene Kopie wird beim nächsten Mal wiederholt.
  - Ein Slot, aus dem ein Host läuft, wird **nie verändert**. Hosts auf demselben Build teilen sich einen Slot (Modell-Export und Konsole nach einem Build), ein neuerer Build geht in einen anderen Slot.
  - Eine zweite FerretSharp-Instanz wird über eine Lock-Datei je Slot (`<n>.lock`, exklusiv offen, solange der Slot benutzt wird) ferngehalten.
  - Schreibt ein Build noch (Datei gesperrt oder nach dem Kopieren geändert), wird nach 0,5 s erneut versucht, höchstens zehnmal; danach eine Fehlermeldung („läuft gerade ein Build?“).
- **`BuildOutputWatcher`** (Core): `FileSystemWatcher` auf den Ausgabeordner (rekursiv), entprellt – gemeldet wird 1,5 s nach der letzten Änderung, weil ein Build viele Dateien schreibt. Ohne Build beobachtet er den Projektordner und reagiert nur auf eine `*.deps.json` (`obj` schreibt jeder Design-Time-Build von Visual Studio). Ein Pufferüberlauf zählt als Änderung.
- **`ClrModelManager`** hält den Watcher für den Build, den er gerade gelesen hat. Meldet der Watcher etwas und unterscheiden sich die Dateien (Größe, Schreibzeit) vom gelesenen Stand, lädt er das Modell neu (Cache wie beim Verbinden, WP-16: der Fingerabdruck enthält die Build-Ausgabe, ein geänderter Build verfehlt ihn von selbst). Das bisherige Modell bleibt bis dahin gültig; die Statusleiste zeigt „Build geändert – Modell wird neu geladen“. Damit erledigt sich auch der Hinweis „Build veraltet“ nach einem Build von selbst.
- **`LinqConsoleService`** startet nach einem gemeldeten Build – sobald das Modell neu geladen ist („wartet auf das Modell“) – einen neuen Host **neben** dem laufenden und tauscht erst, wenn er bereit ist; Ausführungen gehen bis dahin an den alten. Lässt sich der neue Build nicht laden, bleibt der alte (mit Hinweis). Als Rückfall, falls der Watcher nichts gemeldet hat, vergleicht jede Ausführung die Dateien mit denen beim Start und stößt den Neustart im Hintergrund an.
- **Nicht auf dem UI-Thread:** Start der Konsole und Laden des Modells laufen vollständig im Thread-Pool (vorher liefen Teile davon und die Fortsetzungen auf dem UI-Thread). Der LINQ-Tab öffnet sofort und zeigt die Schritte („Kopiere die Build-Ausgabe“, „Starte den Hilfsprozess“, „Baue das Modell“); der Start lässt sich abbrechen.

## Konsequenzen

- Bauen des verknüpften Projekts (und seiner referenzierten Projekte) funktioniert, während FerretSharp verbunden ist und eine LINQ-Konsole läuft – auch „Neu bauen“ aus FerretSharp selbst. Integrationstests: `LinqConsoleTests.The_running_console_does_not_lock_the_projects_build_output`, `ModelReloadTests` (Build bei laufender Konsole, Modell und Konsole sehen die neue Property ohne Neuverbinden).
- Plattenplatz: pro verknüpftem Ausgabeordner meist zwei Slots (laufende Konsole + nächster Build). Nach dem ersten Start kostet ein Build nur die geänderten Dateien. Slots werden nicht aufgeräumt; `%TEMP%` darf sie jederzeit löschen, sie entstehen neu.
- Nach einem Build bauen zwei Hosts das Modell (Modell-Export und neue Konsole) – **nacheinander**: Die Konsole startet erst, wenn das Modell neu geladen ist. Gleichzeitig ließen sie die Oberfläche beim Nutzer stocken (Eingaben im LINQ-Editor hingen bis zum Ende). Ein gemeinsamer Host für beides wäre eine spätere Optimierung (Backlog).
- Die Hilfsprozesse laufen mit **niedrigerer Priorität** (`BelowNormal`); `dotnet build` auf Wunsch des Nutzers nicht.
- Ladeschritte des Modells rendern nicht mehr die ganze Shell (mit allen offenen Tabs aller Workspaces), nur Statusleiste und Modellseite; Grids holen ihre Zeilen nach einem neuen Modell nur neu, wenn sich die Darstellung ihrer Tabelle wirklich geändert hat. Das neue Modell selbst meldet `PresentationService` tabellengenau (`ModelTableSignatures`: Entities, Properties, Werte, Beziehungen und zugeordnete Spalten je Tabelle im Vergleich): Nur die Ansichten betroffener Tabellen rendern neu, der Explorer nur bei geänderten Entity-Namen, die Shell gar nicht mehr.
- Hilfsprozesse werden allein beendet, nie mit `Process.Kill(entireProcessTree: true)`: Das sucht Kindprozesse unter allen Prozessen des Systems und wirft für jeden geschützten eine Ausnahme. Unter dem VS-Debugger hält jede den ganzen Prozess an – nach jedem Konsolen-Neustart fror die Oberfläche bis zu 12 s ein. Nur `dotnet build` (mit echten Kindprozessen) wird beim Timeout als Baum beendet (`DotNetCli.KillTree`); Hilfsprozesse mit `DotNetCli.Kill`.
- Diagnose: `UiStallMonitor` schreibt ins Log, wenn der UI-Thread länger als 300 ms nicht reagiert – gemessen mit Priorität `Normal` (die von Blazor; `Input` hält WPF auch im Leerlauf bis zu einer halben Sekunde zurück), mit der Zeit in Dispatcher-Operationen (Blazor verarbeitet Nachrichten der WebView außerhalb davon), GC-Pausen, Speicherlast und den dotnet-Prozessen. Ab 1,5 s ruft er `dotnet-stack` auf (falls als globales Tool installiert) und schreibt die Stacks aller Threads als `stall-*.txt` ins Log-Verzeichnis – so wurde der Baum-Kill gefunden. `ClrActivityLog` protokolliert die Schritte von Modell und LINQ-Konsole, damit sich Stockungen zuordnen lassen.
- NuGet-Pakete werden weiter aus dem Paket-Cache geladen (`--additionalprobingpath`), nicht kopiert.
