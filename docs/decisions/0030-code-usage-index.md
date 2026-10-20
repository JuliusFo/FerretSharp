# ADR 0030: Verwendung im Code – IL-Index im Core, Arbeits-Klon je Gruppe, Sprung in die passende Visual-Studio-Instanz

- Status: akzeptiert
- Datum: 2026-10-20
- Paket: WP-40

## Kontext

Das C#-Modell kommt bisher aus einer Quelle: dem ModelHost (ADR 0009), der das EF-Modell des verknüpften Projekts im .NET-8-Prozess baut und exportiert. Es sagt, welche Property auf welche Spalte zeigt, aber nicht, wer sie benutzt. WP-40 will je Spalte die Methoden der Solution, die die Property lesen oder schreiben, und die Abfragen auf die Tabelle, mit Datei und Zeile; daraus „im Code ungenutzt“, ein Bericht und die Verwendungen in der Löschbestätigung des Designers. WP-41 (Designer-Änderungen ins C#-Projekt) und WP-45 (Quellzeile im Mitschnitt) bauen auf diesem Index auf.

Der Nutzer arbeitet mit einem Klon je Datenbank auf dem passenden Branch (verknüpft an der Verbindung) und entwickelt in einem weiteren Klon, den er oft wechselt; mehrere Arbeits-Klone und Worktrees gleichzeitig sind üblich. In Visual Studio sind meist zwei Instanzen offen, eine mit der Server- und eine mit der Client-Solution. Entscheidungen des Nutzers (2026-10-20): `docs/roadmap.md`, WP-40.

## Entscheidung

**Zweite Modellquelle: IL und portable PDBs, gelesen im Core.** `System.Reflection.Metadata` (Teil von .NET 10, kein neues Paket) liest die Build-Ausgaben, ohne sie zu laden: Aufrufe der Getter/Setter von Entity-Properties (`call`/`callvirt`), `ldtoken` auf einen Getter (Expression Trees, damit LINQ mitzählt), Zugriffe auf `DbSet<T>`-Properties und `Set<T>()`. Die Sequence Points der portablen PDB (daneben liegend oder eingebettet) bilden den IL-Offset auf Datei und Zeile ab. Zugeordnet wird über den Typnamen aus dem Modell-Export (`EntityExport.ClrType`) und den Property-Namen; eine in einer Basisklasse deklarierte Property gilt für alle Entities, die von ihr erben. Gelesen wird mit geteiltem Zugriff (`FileShare.ReadWrite | Delete`), ohne Schattenkopie: Nichts wird geladen, ein Build wird nicht gesperrt.

- **Umfang:** alle Projekte der Solution, die das verknüpfte Projekt enthält – vom Projektordner aufwärts die erste `.sln`/`.slnx` mit diesem Projekt; bei mehreren wählt der Nutzer. Beide Formate liest FerretSharp selbst (`.sln`: `Project(...)`-Zeilen, `.slnx`: `<Project Path>`), ohne MSBuild. Testprojekte werden mitgelesen und an ihren Assembly-Referenzen erkannt (xunit, NUnit, MSTest, `Microsoft.Testing.Platform`): „ungenutzt“ zählt sie nicht, die Löschbestätigung schon.
- **Aktualität:** neu aufbauen nach einem Build (`BuildOutputWatcher`), Ergebnis je Assembly über die MVID cachen; nur geänderte Assemblies werden neu gelesen. Ein Projekt ohne Ausgabe oder mit Quellen neuer als der Build heißt „nicht gebaut“ bzw. „veraltet“, nicht „ungenutzt“.
- **Grenzen** (die Ansicht nennt sie): Zugriffe per String (`EF.Property<T>(x, "Email")`, `Include("…")`), Reflection, rohes SQL und dynamische Aufrufe findet der IL-Weg nicht; ohne PDB nur Methodennamen (Windows-PDBs werden nicht gelesen).
- **Inhalt:** nur Namen (Typ, Methode, Property), Pfade und Zeilen; keine Werte, nichts verlässt den Rechner.

Verworfen:
- **ModelHost** (Roslyn oder Reflection im Projektprozess): an .NET 8 und Roslyn 4.11 gebunden (ADR 0011), ein Prozess je Abfrage des Index, und die Solution besteht aus mehr als dem DbContext-Projekt.
- **Roslyn-Workspace (`MSBuildWorkspace`)**: braucht MSBuild und Design-Time-Builds der ganzen Solution; bei 425 Entities und mehreren Projekten Minuten statt Sekunden, und Fehler im Build des Nutzers würden die Analyse stören. Für WP-41 (Syntax-Umbau einzelner Dateien) bleibt Roslyn ohne Workspace der Weg.
- **Mono.Cecil**: kann dasselbe, ist aber eine Abhängigkeit mehr; `System.Reflection.Metadata` reicht.
- **Textsuche** im Quellcode: findet `Email` auch dort, wo es nicht die Property der Entity ist.

**Arbeits-Klon: Zustand je Verbindungsgruppe, kein Teil der Konfiguration.** Fundstellen kommen aus dem Branch-Klon der Verbindung (sein Build passt zur DB), geöffnet wird die Datei im Arbeits-Klon. Der Nutzer wählt den aktiven Arbeits-Klon über einen Umschalter in der Statusleiste oder die Befehlspalette; er gilt für die Gruppe der sichtbaren Verbindung, Verbindungen ohne Gruppe teilen sich einen. Die Liste zum Wechseln entsteht aus den zuletzt gewählten Ordnern (lokale Pfade, nicht in Export/Import). FerretSharp sucht keine Klone und gleicht keine Git-Remotes ab – verschiedene Gruppen haben verschiedene Repos –, es prüft nur, ob das verknüpfte Projekt im Arbeits-Klon unter demselben relativen Pfad liegt wie im Branch-Klon. Den Branch liest es bei Bedarf aus `.git/HEAD` (Worktree: `.git` ist eine Datei mit `gitdir:`), ohne Git-Bibliothek. Ohne passenden Arbeits-Klon gilt der Branch-Klon.

**Sprung in Visual Studio: Instanz über die Solution, per COM (Running Object Table).** `devenv /Edit` nimmt die zuletzt aktive Instanz und träfe bei Server- und Client-Solution oft die falsche; auch der Sprung zur Zeile ist darüber unzuverlässig. Stattdessen:

- Laufende Instanzen melden sich in der Running Object Table als `!VisualStudio.DTE.<Version>:<PID>`. FerretSharp liest je Instanz `DTE.Solution.FullName` und wählt die Instanz mit der indizierten Solution am selben relativen Pfad im aktiven Arbeits-Klon; öffnet per `DTE.ItemOperations.OpenFile`, springt per `ActiveDocument.Selection.GotoLine` und holt das Fenster nach vorn.
- Rückfall: eine Instanz, deren Solution-Ordner die Datei enthält; sonst `devenv /Edit` (Pfad über `vswhere`) mit dem Hinweis „keine passende Visual-Studio-Instanz“.
- Late Binding (`dynamic`) auf die IDispatch-Schnittstelle, ohne EnvDTE-Paket und ohne Interop-Assembly. Nur im App-Projekt (Windows-only, CA1416); UI und Core kennen ein Interface (`ICodeEditorLauncher`). Daneben VS Code (`code -g datei:zeile`) und das Standardprogramm des Systems; Kopieren von Pfad, Pfad:Zeile und Namen im Kontextmenü.
- **Spike (2026-10-20):** Aus einer .NET-10-Konsole (MTA, Late Binding) wurde die laufende Instanz `!VisualStudio.DTE.18.0` mit ihrer `.slnx` gefunden und gelesen. Öffnen und `GotoLine` prüft das Paket an einer eigenen Test-Instanz.

## Konsequenzen

- Der Index ist eine zweite Quelle neben dem ModelHost und hängt von dessen Export ab (Typ- und Property-Namen). Ohne geladenes Modell gibt es keine Zuordnung zu Spalten.
- COM-Aufrufe an Visual Studio können abgelehnt werden, solange VS beschäftigt ist (`RPC_E_CALL_REJECTED`): kurze Wiederholung mit Grenze, dann Rückfall. Eine als Administrator gestartete Instanz ist für eine nicht erhöhte App in der Running Object Table nicht sichtbar – dann greift der Rückfall.
- Kein Schreiben in Repos des Nutzers (das kommt mit WP-41 und eigenem ADR); der Arbeits-Klon wird hier nur gelesen.
- Kein neues NuGet-Paket.
