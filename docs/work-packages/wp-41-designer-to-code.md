# WP-41 Designer-Änderungen ins C#-Projekt

Branch `wp/41-designer-to-code` (von `main`), 2026-11-12, noch ohne Release. ADR 0050. Baut auf WP-40 (Arbeits-Klone, ADR 0030) und WP-23 (Designer, „Entity aus Tabelle“).

## Planung (aus `docs/roadmap.md`)
- Nach einer Änderung im Tabellen-Designer die passende Änderung an Entity-Klasse und `IEntityTypeConfiguration` vorschlagen: Spalte hinzu → Property und Konfiguration im Stil des Projekts, Länge/NULL geändert → Facetten anpassen, Spalte gelöscht → Property entfernen. Neue Tabelle → neue Dateien wie „Entity aus Tabelle“.
- Roslyn-Syntax-Umbau (nur Syntax, kein Kompilieren; aktuelles Roslyn im .NET-10-Prozess), Kodierung, BOM und Zeilenenden der Datei bleiben. Vorschau als Diff, Schreiben erst nach Bestätigung und nur, wenn die Datei seit der Analyse unverändert ist; kein Build, kein Commit.
- Ziel ist der aktive Arbeits-Klon der Gruppe, nie der verknüpfte Branch-Klon; Warnung bei Branch einer Verbindung und ungespeicherten Änderungen.
- Vorher geklärt (2026-11-12): Roslyn im Core braucht eine `VersionOverride` im ModelHost (transitive Festlegung); `WorkCloneMapping.FindTypeFile` findet nur die erste Datei einer Klasse und keine Property-Zeile.

## Entscheidungen des Nutzers (2026-11-12)
- Plan bestätigt mit allen Vorschlägen: erste Stufe hinzufügen, Facetten ändern, löschen (Umbenennen und Typwechsel nur als Hinweis); Vorschau vor dem Ausführen, Schreiben erst nach dem DDL; nur konfigurierte Facetten anpassen, keine ergänzen; „Branch einer Verbindung“ = Branch eines verknüpften Klons der Gruppe; Roslyn 5.9 im Core, ModelHost per `VersionOverride` auf 4.11; ungespeicherte Änderungen per `git status`, ohne git ein Hinweis.
- **Zweite Stufe im selben Paket** (Wunsch des Nutzers): neue Tabellen als neue Entity- und Konfigurationsdatei. Vorschläge dafür bestätigt: Ordner und Namespace, wo die meisten Entities bzw. Konfigurationen liegen, in der Vorschau wählbar aus den Ordnern, die das Projekt dafür nutzt; `DbSet` nur, wenn der Kontext DbSets hat, im Stil der vorhandenen; `ApplyConfiguration` nur bei einzelner Registrierung, nichts bei `ApplyConfigurationsFromAssembly`; Gegenseite einer Navigation nur als Hinweis; altes Projektformat nur Warnung; nach dem `CREATE TABLE` schreiben, Entity-Name änderbar.

## Gebaut
- **Pakete:** `Microsoft.CodeAnalysis.CSharp` 5.9 im Core (`Directory.Packages.props`, Gruppe „Core“); ModelHost mit `VersionOverride="4.11.0"` (Scripting 4.11 braucht genau seine Version). Die App hat beide getrennt (`modelhost`-Ordner).
- **Core `ClrModel/CodeEdit/`** (Namespace `ClrModel`):
  - `CodeFile`: liest UTF-8/UTF-16 mit oder ohne BOM (anderes wird nicht angefasst), Hash der Bytes, lernt Zeilenende und Einrückung (häufigster Schritt 2–8 Leerzeichen oder Tab); `CodeFileFormat.Apply`/`Encode` für erzeugten Text.
  - `CodeScan`: alle `.cs` unter dem Arbeits-Klon ohne `bin`/`obj`/Punkt-Ordner und **ohne Links** (Junctions, symbolische Links), geparst nur, wenn ein gesuchter Bezeichner darin steht (oder `global using`). `TypesNamed` (alle `partial`-Teile, Name wie `Type.FullName`), `ConfigurationsOf` (Konfigurationsklasse, `Entity<T>(b => …)`, `Entity<T>().…`-Ketten), Namensauflösung wie C#: qualifiziert, umschließende Namespaces vor `using`/`global using`, sonst eindeutig.
  - `ColumnChanges`: aus Tabellenstand und `TableDraft` die Änderungen je Spalte (hinzu, Facetten, gelöscht, umbenannt, Typ).
  - `EntityCodeChanges` (erste Stufe) und `NewEntityCode` (zweite) bauen einen `CodeChangePlan`; Änderungen als Text-Spannen (`FileEdits`), der Rest der Datei bleibt. Eine Änderung mit mehr Syntaxfehlern als vorher oder überlappende Spannen sind ein Problem – dann wird nichts geschrieben.
  - `CodeChangeWriter`: prüft erst alle Dateien (unter dem Klon, kein Link auf dem Weg, Hash unverändert, neue Datei nicht da), schreibt dann über eine temporäre Datei (`AtomicFile.WriteInUserFolderAsync`; neue Dateien mit `File.Move(…, overwrite: false)`).
  - `CodeTarget`: Branch des Klons gegen die Branches der verknüpften Klone der Gruppe; `git -c core.fsmonitor=false status --porcelain=v1 -z` mit `GIT_OPTIONAL_LOCKS=0`, 15 s Zeitlimit, Prozess im Job Object (`ChildProcesses`).
  - `LineDiff`: Myers auf Zeilen, Hunks mit drei Zeilen Kontext (Monacos Diff-Editor braucht einen Worker, den der Build nicht hat).
  - `EntityCode.PropertyFor`: der eindeutige Name einer neuen Property, auch gegen andere neue Spalten.
- **UI:** `ProjectCodeChange` (State: Planen und Schreiben im Hintergrund, Stufen), `ProjectCodeChangeView` (Ziel mit Branch, „Arbeits-Klon …“, Probleme, Warnungen, Hinweise, bei neuen Dateien „Entity nach“/„Konfiguration nach“ aus `CodeChangePlan.Places`, Diff je Datei, „Ins Projekt schreiben“, Ergebnis je Datei); im Designer-Reiter „C#“ als Vorschau (jetzt bei jeder Spaltenänderung einer gemappten Tabelle, nicht nur bei neuen Spalten), nach dem DDL über `TableTab.CodeChange` in der Spaltenansicht, unter „Entity aus Tabelle“ für neue Dateien; nach „Neue Tabelle“ öffnet der Tab mit „Entity aus Tabelle“, wenn ein Modell da ist.
- Schutz gegen doppeltes Schreiben: Eine neue Spalte, die das Modell schon abbildet oder deren Property die Klasse schon deklariert (geschrieben, noch nicht gebaut), wird nicht noch einmal angelegt; eine geschriebene Änderung plant erst mit „Arbeits-Klon neu ansehen“ neu, nicht beim Neuladen des Modells.

## E2E (2026-11-12)
Eigener Container `ferret-wp41`, das Beispielprojekt als zwei Git-Klone im Scratchpad (verknüpft auf `dev`, Arbeits-Klon auf `feature/wp41-e2e`), Klicks mit echter Windows-Eingabe.
- **Gefunden und behoben:** Nach dem DDL gleicht `ClrModelManager.RemapAsync` das Modell neu ab; eine gelöschte Spalte ist dann nicht mehr gemappt, und `mapping.PropertyOf` fand ihre Property nicht – die Vorschau vor dem Lauf zeigte das Entfernen, die nach dem Lauf nicht mehr. Jetzt zusätzlich über die Spalte im Modell (`EntityCodeChanges.PropertyOf`, Test mit neu abgeglichenem Modell).
- Geprüft: Vorschau im Reiter „C#“ (Länge/NULL, Stellen, Löschen, neue Spalte im Stil `k => k.`), nach dem Lauf „Ins Projekt schreiben“, nur die zwei Dateien im Arbeits-Klon geändert, CRLF und alle anderen Zeilen unverändert, verknüpfter Klon unberührt; Warnung „nicht committete Änderungen“; eine von außen geänderte Datei wird nicht geschrieben, nach „Arbeits-Klon neu ansehen“ schon (die getippte Zeile bleibt); „Entity anlegen“ für eine Tabelle ohne Entity mit `DbSet` im Stil `=> Set<T>()` und ohne Registrierung (`ApplyConfigurationsFromAssembly`); nach `CREATE TABLE` öffnet der Tab mit der Vorschau. Der Arbeits-Klon baut danach. Hell und dunkel angesehen, Log ohne `[ERR]`/`[WRN]`.
- Nicht geprüft: die Ordnerauswahl mit mehreren Ordnern in der App (das Beispielprojekt hat je einen; Unit-Test), Warnung „Branch einer Verbindung“ in der App (Unit-Test mit echtem git), ein Projekt ohne nullable reference types.

## Offen (Fragen an den Nutzer)
- Ordnerauswahl: nur Ordner, in denen das Projekt schon Entities bzw. Konfigurationen hat. Reicht das, oder soll auch ein neuer Ordner gehen?
- Umbenennen (`HasColumnName`, Property-Name) und Typwechsel als nächste Stufe (Backlog)?
