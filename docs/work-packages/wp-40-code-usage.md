# WP-40 Verwendung im Code

Branch `wp/40-code-usage`, begonnen am 2026-10-20. ADR 0030. Fundament für WP-41 (Designer-Änderungen ins C#-Projekt) und WP-45 (Quellzeile im Mitschnitt).

## Planung (aus `docs/roadmap.md`)
- **Ziel:** Ansicht „Verwendung“ im Tabellen-Tab: je Spalte, welche Methoden der Solution die Property lesen oder schreiben und welche Queries die Tabelle anfassen, mit Datei und Zeile. Daraus: Markierung „im Code ungenutzt“ in der Spalten-Ansicht und ein Bericht der ungenutzten gemappten Properties; im Designer stehen beim Löschen einer Spalte ihre Verwendungen in der Bestätigung.
- **Technik:** die Build-Ausgaben der Projekte der Solution per `System.Reflection.Metadata` lesen – Getter/Setter von Entity-Properties (`call`/`callvirt`, in Expression Trees `ldtoken`), Zugriffe auf `DbSet<T>` – und über die portablen PDBs auf Datei und Zeile abbilden. Läuft in .NET 10 im Core, ohne MSBuild, ohne Roslyn-Workspace, ohne ModelHost. Neu aufbauen nach einem Build, Ergebnis je Assembly (MVID) cachen.
- **Grenzen:** Zugriffe per String, Reflection und rohes SQL findet der IL-Weg nicht; Projekte ohne PDB nur mit Methodennamen.

## Entscheidungen des Nutzers (2026-10-20)
- **Arbeits-Klon:** Der Nutzer hat je Datenbank einen Klon auf dem passenden Branch (verknüpft an der Verbindung) und entwickelt in einem weiteren Klon, den er oft wechselt; mehrere Arbeits-Klone und Worktrees gleichzeitig sind üblich.
  - Der Arbeits-Klon ist **Zustand, keine Konfiguration**: nicht im Verbindungsdialog, nicht in den Einstellungen, sondern ein Umschalter (Statusleiste, Befehlspalette).
  - **Je Verbindungsgruppe** ein aktiver Klon; Verbindungen ohne Gruppe teilen sich einen.
  - **Eingetragen vom Nutzer, nicht gefunden:** kein Abgleich über das Git-Remote, weil verschiedene Gruppen verschiedene Arbeits-Repos haben, die nicht zueinander passen müssen. Geprüft wird nur, ob das verknüpfte Projekt im Arbeits-Klon am selben relativen Pfad liegt.
  - Gehört in dieses Paket (nicht in ein eigenes davor), weil „Fundstelle öffnen“ ihn als Erstes braucht.
- **Eingelesene Projekte:** alle Projekte der Solution, die das verknüpfte Projekt enthält. Testprojekte werden mitgelesen und markiert: „ungenutzt“ zählt sie nicht, die Löschbestätigung im Designer schon (der Build bricht auch dort).
- **Klick auf eine Fundstelle** öffnet die Datei an der Zeile im Editor; Kopieren (Pfad, Pfad:Zeile, Name) im Menü. Visual Studio ist Standard, daneben VS Code und das Standardprogramm.
- **Die richtige Visual-Studio-Instanz:** Der Nutzer hat meist zwei offen (Server- und Client-Solution), DB-Änderungen betreffen die Server-Solution. `devenv /Edit` nähme die zuletzt aktive. Gewählt wird deshalb über die Solution (Running Object Table, DTE), nicht über die Reihenfolge. Test in der laufenden Instanz des Nutzers erlaubt.
- Nicht in diesem Paket: XML-Doku als Spaltenbeschreibung (bleibt im Backlog).

## Entscheidungen in der Umsetzung (einfachste Variante)
- **Mapping-Code zählt nicht als Verwendung:** `builder.Property(x => x.Email)` ist auch ein `ldtoken`. Code in Typen, die `IEntityTypeConfiguration<T>` implementieren, in `OnModelCreating`/`ConfigureConventions` eines DbContext und in seinen DbSet-Properties ist „Mapping“: angezeigt, aber nicht gezählt. WP-41 findet die Konfiguration einer Property darüber.
- **Fremdschlüssel** gelten auch über ihre Navigations als verwendet (`auftrag.Kunde` braucht `KundeId`), auch über die Gegenseite. **Schlüssel** (Primärschlüssel, von Fremdschlüsseln referenzierte Schlüssel) und Schatten-Properties sind nie „ungenutzt“ (im E2E gefunden: jeder Primärschlüssel war markiert).
- **Vererbung:** Eine Verwendung von `EntityBase.Id` oder `IHasName.Name` zählt für jede Entity, die davon erbt; die Ansicht nennt den Typ („über EntityBase“). Die Typhierarchie kommt aus den gelesenen Assemblies selbst.
- **Der Index wird erst gebaut, wenn ihn etwas braucht** (Ansicht „Verwendung“, Spalten-Ansicht einer gemappten Tabelle, Designer, Bericht), nicht beim Verbinden. Vor jeder Nutzung prüft er, ob sich das Modell oder eine Assembly der Solution geändert hat (Schreibzeit), und liest dann nur die geänderten Assemblies neu.
- **Gewählte Solution** (bei mehreren) als `ConnectionProfile.CodeSolution`, neben dem Link und nicht in ihm: Viele Stellen vergleichen den Link (Modell, LINQ-Konsole), eine geänderte Solution hätte das Modell neu geladen und die Konsole neu gestartet. Lokaler Pfad wie das Projekt (Export/Import nur auf Wunsch).
- **„Verwendung“ erscheint nur mit verknüpftem C#-Projekt.** Ein Link mit dieser Ansicht öffnet nur lesend; ein älteres FerretSharp lehnt ihn als unbekannte Ansicht ab statt ihn falsch zu lesen.
- **Ordner wählen ohne Ordner-Dialog:** Der Host hat nur einen Datei-Dialog. Gewählt wird eine Solution oder ein Projekt im Klon, der Klon ist dessen Git-Wurzel (sonst der Ordner).
- **Befehl „Arbeits-Klon wechseln“** ohne Standardtaste (Regel des Projekts), in der Gruppe „SQL und LINQ“ der Tastenkürzel.
- **Fundstelle nicht am selben Pfad** (Datei im Arbeits-Klon verschoben): die Datei, die den Typ deklariert, ohne Zeile, mit Hinweis. Fehlt auch die, gilt die Datei im verknüpften Klon.
- **Pfade deterministischer Builds** (`/_/src/…`, PathMap) gelten relativ zur Wurzel des verknüpften Klons.

## Gebaut
- **Core, Index** (`ClrModel/Usage`, Namespace `ClrModel`): `SolutionFile` (`.sln`/`.slnx` ohne MSBuild, nächste Solution mit dem Projekt), `BuildOutputLocator.FindAssembly`/`ProjectBuild` (jede Projektart, gebaut/veraltet/nicht gebaut), `IlUsageScanner` (IL ohne Laden, eigene Opcode-Tabelle, Typnamen aus Signaturen, Aufrufer aus Lambdas, lokalen Funktionen und Zustandsautomaten aufgelöst, dateilokale Typen mit ihrem geschriebenen Namen, Zeilen aus der portablen PDB, Testprojekte an Assembly-Referenzen), `CodeUses` (`CodeUse`, `AssemblyUses`, `TypeLink`), `CodeUsageIndex` (Zuordnung über den Modell-Export, Vererbung, Mapping, „ungenutzt“), `CodeUsageBuilder` (parallel, Cache `cache/code-usage/<MVID>.json`, 30 Tage), `CodeUsageService` (je Verbindungs-Scope, ein Lauf für alle Ansichten, Lebensdauer des Scopes).
- **Core, Arbeits-Klon:** `GitFolder` (Wurzel, Branch aus `.git/HEAD`, Worktree über `gitdir:`), `WorkClones` (`work-clones.json`: aktiver Klon je Gruppe, zuletzt gewählte Ordner), `WorkCloneMapping` (Prüfung, Pfad und Solution abbilden, Typsuche). `ConnectionProfile.CodeSolution`, `ConnectionManager.SetCodeSolutionAsync`, `AppSettings.CodeEditor`, `ShortcutAction.SwitchWorkClone`, `TabMode.Usage`.
- **App:** `CodeEditorLauncher` – VS-Instanzen aus der Running Object Table, Auswahl über die Solution (sonst über den Solution-Ordner), `OpenFile`, `GotoLine`, `Activate` per Late Binding mit Wiederholung bei `RPC_E_CALL_REJECTED`/`RPC_E_SERVERCALL_RETRYLATER`; Rückfall `devenv /Edit` über `vswhere`; VS Code über `code.cmd -g`; Standardprogramm.
- **UI:** `UsageView` (Ansicht „Verwendung“: Abfragen der Tabelle, Spalten mit aufklappbaren Fundstellen, Kopier-Menü, Solution-Auswahl, Projektstatus, „Neu einlesen“), Markierung „ungenutzt“ in `ColumnsView`, `UnusedReport` auf der Modellseite, Hinweise im Designer und in `DdlConfirmDialog` (gelöschte und umbenannte Spalten), `WorkCloneStatus` und `WorkCloneDialog`, Einstellung „Code öffnen in“. State: `CodeUsageRows`, `CodeUsageOpener`, `WorkCloneChoices`, `ICodeEditorLauncher`.
- **Tests:** Core `IlUsageScannerTests` (gegen die eigene Test-Assembly mit `UsageSamples.cs` und EF-Stand-ins, Zeilen über Marker-Kommentare), `CodeUsageIndexTests`, `SolutionAndBuilderTests`, `CodeUsageServiceTests`, `WorkCloneTests`; UI `CodeUsageRowsTests`, `WorkCloneChoicesTests`.

## Spike (2026-10-20)
In der laufenden Instanz des Nutzers (VS 18, `.slnx`): Instanz über die Running Object Table gefunden, Datei geöffnet, Zeile angesprungen (rund 0,9 s). Direkt nach `OpenFile` lehnte VS den nächsten Aufruf ab (`0x8001010A`); eine Wiederholung nach 100 ms ging durch. Der Binder von `dynamic` verpackt das HRESULT in eine `MissingMemberException`.

## E2E (2026-10-20)
Eigene Instanz (`--data-dir`, eigener WebView2-Ordner, deutsch, hell), eigener Container `ferret-wp40` mit dem Beispielschema, zwei Verbindungen in den Gruppen ERP und Kasse, beide verknüpft mit einem Branch-Klon des Beispielmodells im Scratchpad (`.git/HEAD` von Hand, dazu ein Projekt mit Zugriffen), ein zweiter Klon als Arbeits-Klon. Bedient per CDP.
- **Gefunden und behoben:** Jeder Primärschlüssel war als „ungenutzt“ markiert (siehe oben).
- Geprüft: Ansicht „Verwendung“ (Abfragen, Spalten, Lesen/Schreiben/LINQ, Mapping), Markierung in der Spalten-Ansicht, Bericht auf der Modellseite (über eine Basisklasse geerbte Properties), Warnung im Designer und in der Bestätigung (abgebrochen, keine DDL), Arbeits-Klon wählen (Datei geschrieben, übersteht den Neustart), Trennung nach Gruppen, Branch-Wechsel in der Statusleiste, Befehlspalette und eigene Taste. Klick auf eine Fundstelle: kein VS hatte die Solution offen, der Rückfall öffnete die Datei **aus dem Arbeits-Klon** in der laufenden Instanz des Nutzers, mit Hinweis.
- Log ohne `[ERR]`. Eine von Hand geschriebene `settings.json` im falschen Format legte die App wie vorgesehen als `.bad-…` beiseite; Ctrl+Alt-Tasten lehnt sie ab (AltGr) – beides kein Fehler.
- **Nicht geprüft:** der Sprung in eine Instanz, die die Solution offen hat (nur im Spike), VS Code, der native Dateidialog „Solution oder Projekt wählen“ (Regel: nicht bedienen), die Auswahl zwischen mehreren Solutions, die Laufzeit gegen die große Solution des Nutzers (425 Entities, mehrere Projekte) und ein echtes Testprojekt dort.

## Unter-Tabs (Wunsch des Nutzers nach dem eigenen Test, 2026-10-20)
- Die Ansicht „Verwendung“ hat zwei Unter-Tabs statt Abfragen und Spalten untereinander: **Spalten** (Standard) und **Abfragen**, je mit Zähler im Kopf. Spalten zählen die ungenutzten („4 ungenutzt“, gelb wie die Markierung), Abfragen die im Anwendungscode (ohne Tests und DbSet-Property).
- Der gewählte Unter-Tab bleibt, solange der Tab offen ist; gespeichert wird er nicht.
- Der Hinweis zu den Grenzen (Strings, Reflection, rohes SQL) steht als Tooltip am Kopf („ⓘ“) statt als Absatz.

## Code-Review (2026-10-20)
Review-Lauf über den Branch, zehn Funde, alle behoben:
- **Verschwundene DLL:** Ein Build, der die Assembly zwischen Finden und Lesen löscht, ergab eine `NullReferenceException` statt eines IO-Fehlers (`IlUsageScanner.OpenExisting`). Der Builder fängt jetzt jeden Fehler je Assembly: Ein seltsames Projekt kostet nur sich selbst.
- **Hängender Index:** Ein unerwarteter Fehler ließ den Dienst auf „Building“ stehen und erreichte die Error-Boundary jedes wartenden Tabs. Jetzt endet jeder Fehler in „Failed“ mit Meldung.
- **Modellwechsel während des Baus:** Das Ergebnis aus dem alten Modell stand als „Ready“ da, und nichts baute neu. Jetzt bleibt es als „NotBuilt“ stehen, und eine sichtbare Ansicht baut nach.
- **Visual Studio:** Scheitert nach dem Öffnen nur der Sprung zur Zeile (Zeile hinter dem Ende einer kürzeren Datei im Arbeits-Klon) oder das Aktivieren, bleibt es bei einer Warnung im Log. Vorher öffnete der Rückfall die Datei ein zweites Mal.
- **VS Code ohne cmd.exe:** `code.cmd` ist eine Batch-Datei; ein Pfad mit `&`, `^` oder `%` wurde von cmd zerlegt. Gestartet wird jetzt `Code.exe` neben dem `bin`-Ordner (`VsCodeCommand`), ohne sie wird ein solcher Pfad abgelehnt.
- **Keine Dateizugriffe beim Rendern:** Die Statusleiste liest Ordner, Branch und Passung im Hintergrund (bei Änderungen und alle 15 s), der Dialog einmal beim Öffnen; `Solutions` liest nichts mehr (gefunden beim Bau), `EnsureAsync` prüft auf dem Pool, „Pfad kopieren“ sucht eine verschobene Datei im Hintergrund.
- **Nur sichtbare Ansichten bauen:** Spalten- und Verwendungsansicht bleiben nach dem ersten Besuch gemountet und bauten nach jedem Build des Projekts neu. Jetzt nur, wenn sie sichtbar sind (`Shown` aus `TabView`: Modus, aktiver Tab, Explorer). Im E2E geprüft: Neu laden des Modells ohne sichtbare Ansicht baut nicht, das Anzeigen der Spalten danach schon.
- **Bericht:** einmal je Index berechnet statt zweimal je Rendern; die Schlüssel stehen im Index als Menge statt einer Suche über alle Fremdschlüssel je Property.
- **Links** lassen die Ansicht „Verwendung“ weg wie die Datenansicht: Ein älteres FerretSharp lehnte den ganzen Link wegen der unbekannten Ansicht ab.
- **Dialog „Arbeits-Klon“** schließt nicht mehr bei Klick daneben (`.claude/rules/ui.md`: nur reine Anzeige-Dialoge).

## Nachtrag: Fremdschlüssel über Navigations (2026-10-20, `fix/fk-usage-via-navigation`, 3.43.1)
Gefunden vom Nutzer im eigenen Test: Eine FK-Spalte, die der Code nur über die Navigation nutzt (`virtual Kunde` statt `KundeId`), zeigte in „Verwendung“ nur die Konfiguration. Die Markierung „ungenutzt“ zählte die Navigations schon (`UsesCountingFor`), die Liste und der Designer nicht – beide widersprachen sich.
- **Spalte:** `CodeUsageIndex.ForColumn` liefert die Verwendungen der Property und die ihrer Navigations, auch der Gegenseite (`Kunde.Auftraege`); `IsUnused` baut darauf, damit Markierung und Liste nicht mehr auseinanderlaufen. `CodeUsage.Navigation` nennt die Navigation (`Auftrag.Kunde`), die Ansicht zeigt „über Navigation …“ gemischt mit den direkten Verwendungen nach Datei und Zeile, gezählt wie diese (Entscheidung des Nutzers: gemischt, Gegenseite mit). Ein Schatten-FK hat nur diese Verwendungen.
- **Abfragen (Entscheidung des Nutzers: gleich mitbauen):** `CodeUsageIndex.NavigationsTo` – Navigations, die Zeilen der Tabelle laden (`auftrag.Kunde` für `KUNDEN`, `kunde.Auftraege` für `AUFTRAEGE`), als eigener Block „Über Navigations“ unter den Abfragen, im Zähler des Unter-Tabs mitgezählt (ohne Tests und Mapping).
- **Designer:** Beim Löschen einer FK-Spalte stehen die Navigations eigens in der Warnung: Über sie bricht der Build nicht, ihre Abfragen scheitern zur Laufzeit. Umbenennen bleibt bei einem Text (das Mapping muss mitgehen).
- „Namen kopieren“ kopiert bei einer Verwendung über eine Navigation deren Namen (`Auftrag.Kunde`).
- Version: Patch 3.43.1 (Entscheidung des Nutzers), obwohl der Block unter „Abfragen“ neu ist.

## Nachtrag: Filter Lesen/Schreiben (2026-10-20, `feat/usage-read-write-filter`)
Wunsch des Nutzers: in „Verwendung“ Lese- und Schreibzugriffe filtern. Entscheidung des Nutzers: Variante A (Chips plus getrennte Zählspalten) und `ExecuteUpdate` als Schreiben erkennen.
- **Filter:** `segmented` „Alle · Lesen · Schreiben“ neben den Unter-Tabs, nur bei „Spalten“; bleibt wie der Unter-Tab, solange der Tab offen ist, nicht gespeichert. Mit „Lesen“/„Schreiben“ stehen nur Spalten mit einem solchen Zugriff da (Tests eingeschlossen), aufgeklappt nur diese Zugriffe. Logik in `CodeUsageRows.Matches`/`Filter`, `ColumnUsageRow.Matching`/`Count`.
- **Einordnung:** Lesen = Getter und LINQ (`Expression`), Schreiben = Setter, Objektinitialisierer, `SetProperty` eines `ExecuteUpdate`. Mapping-Code ist weder noch und steht nur unter „Alle“. Eine Collection-Navigation (`kunde.Auftraege.Add(…)`) ist im IL ein Getter und zählt als Lesen, obwohl sie den FK der Gegenseite setzen kann (Tooltip an „L“).
- **Spalten L/S** statt „Verwendungen“: Zugriffe im Anwendungscode, in Klammern die in Tests; Mapping zählt in keiner der beiden (vorher in der Klammer).
- **`ExecuteUpdate` (EF Core 7–9):** `s => s.SetProperty(x => x.Email, …)` ist selbst ein Expression Tree. Der Compiler nennt `SetPropertyCalls<T>.SetProperty` per `ldtoken`, bevor er die Argumente baut; der nächste Getter per `ldtoken` ist die gesetzte Property → `Write`, der Wert-Ausdruck bleibt Lesen. Reihenfolge im Test gegen echten Compiler-Output geprüft (`UsageSamples.cs`, EF-Stand-ins `SetPropertyCalls<T>`, `ExecuteUpdate`). Cache-Format 2: Alle Assemblies werden einmal neu gelesen.
- **Nicht erkannt:** EF Core 10 (`ExecuteUpdate(s => s.SetProperty(…))` als `Action<UpdateSettersBuilder<T>>`, kein Expression Tree außen) und Complex Types (`x => x.Adresse.Strasse` markiert `Adresse`) – Backlog.

## Offen
- Im Rückfall über `devenv /Edit` springt Visual Studio nicht zur Zeile (Backlog: danach die Instanz mit der aktiven Datei suchen und `GotoLine`).
- Ein Branch-Wechsel im Arbeits-Klon zeigt sich in der Statusleiste nach höchstens 15 s.
- Laufzeit und Größe des Caches gegen die Solution des Nutzers messen.
