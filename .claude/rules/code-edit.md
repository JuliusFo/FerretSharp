---
paths:
  - "src/FerretSharp.Core/ClrModel/CodeEdit/**"
  - "src/FerretSharp.UI/State/Model/ProjectCodeChange*"
  - "src/FerretSharp.UI/Components/Schema/ProjectCodeChangeView*"
  - "tests/FerretSharp.Core.Tests/ClrModel/CodeEdit/**"
---

# Designer-Änderungen ins C#-Projekt (WP-41, ADR 0050)

Das C#-Modell und der ModelHost: `clr-model.md`; Arbeits-Klone und Index: `code-usage.md`.

- **Roslyn gibt es zweimal:** 5.x im Core (nur Syntax), 4.11 im ModelHost (Scripting, .NET 8). Wegen `CentralPackageTransitivePinningEnabled` hält eine `VersionOverride` im ModelHost-Projekt 4.11 fest – beim Heben der zentralen Version nie mitziehen (ab 4.12 lädt Roslyn in .NET 8 nicht).
- **Nur Text-Spannen ändern, nie Syntax neu drucken** (`FileEdits`): Roslyn findet die Stellen, der Rest der Datei bleibt Byte für Byte. Jede Änderung, die mehr Syntaxfehler hinterlässt als vorher, oder überlappende Spannen sind ein Problem des Plans – dann wird nichts geschrieben. Eingefügter Text nimmt Zeilenende und Einrückung der Datei (`CodeFileFormat`), nie `\n` fest.
- **Namen wie C# auflösen** (`CodeScan.Names`): qualifiziert über den vollen Namen; unqualifiziert zuerst die umschließenden Namespaces (ein `Kunde` in `Shop.Api` gewinnt dort gegen ein `global using Shop.Entities`), dann `using`/`global using`, sonst nur, wenn der Name im Scan eindeutig ist. Eine Konfiguration falsch zuzuordnen hieße, eine fremde Klasse zu ändern.
- **Ketten ab dem Präfix lesen** (`EntityCodeChanges.Chain(expr, prefix)`): bei `modelBuilder.Entity<T>().Property(…)` beginnt die Kette erst bei `Property`; ohne Präfix war der erste Aufruf `Entity<T>()` und keine Property wurde gefunden.
- **Schreiben nur unter dem Arbeits-Klon:** Der Scan überspringt Links (Junctions, symbolische Links), der Writer prüft jeden Ordner auf dem Weg und den Hash der Datei, eine neue Datei nie über eine vorhandene (`File.Move(…, overwrite: false)`). Alles vor dem ersten Schreiben prüfen – geht eine Datei nicht, keine.
- **`git status` nur mit `-c core.fsmonitor=false` und `GIT_OPTIONAL_LOCKS=0`:** Die Konfiguration eines Repositories kann für `fsmonitor` ein Programm nennen, und ohne die Variable schreibt `status` den Index, während der Nutzer im Klon arbeitet. Prozess über `ChildProcesses.Started`, mit Zeitlimit.
- **Nicht doppelt schreiben:** Eine geschriebene, noch nicht gebaute Property steht in der Klasse, aber nicht im Modell – `Add` prüft beides. Die Ansicht plant nach dem Schreiben nicht von selbst neu (das neu geladene Modell nach dem Build löschte sonst das Ergebnis).
- Ein kopiertes `DbSet` tauscht den Entity-Typ auch im Rumpf (`=> Set<Kunde>()`), nicht nur im Typargument.
- Tests schreiben ihre Erwartungen mit `\n` (`ReplaceLineEndings("\n")` auf Raw-Strings): Ein Checkout mit CRLF ändert sonst die Literale der Testdatei.
