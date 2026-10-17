---
paths:
  - "src/FerretSharp.Core/ClrModel/**"
  - "src/FerretSharp.ModelHost/**"
  - "tests/FerretSharp.FakeModelHost/**"
  - "tests/FerretSharp.FakeEfModel/**"
  - "samples/**"
---

# C#-Modell und ModelHost

Schicht und Begründung: ADR 0009/0010/0011/0016; Invariante „C#-Modell als eigene Schicht“ in der Wurzel-`CLAUDE.md`.

## Das Projekt des Nutzers
- DB-first von Hand: erst die DB ändern, dann Entity/Konfiguration; keine Migrations. Namenskonvention im Code (Tabellen groß, `KundenId` → `KUNDEN_ID`), **eigene Value Converter** (bool ↔ J/N, Enum-Kürzel), Enum-Member mit `[Display(ResourceType = …, Name = …)]`. Ein DbContext in einer Klassenbibliothek (Konstruktor `DbContextOptions`), Entities in einem anderen Projekt, EF Core 8.
- Pro Datenbank ein eigener Klon des DbContext-Repos auf dem passenden Branch (Prod-DB → release, Test-DB → test, Dev-DB → dev), jeweils als C#-Projekt der Verbindung verknüpft; entwickelt wird in einem weiteren Klon.
- Größenordnung: 425 Entities, ~4000 Properties; ohne Cache braucht der ModelHost ~12 s (meist `OnModelCreating`). Beziehungen ohne FK-Constraint sind häufig.
- Der Oracle-EF-Provider quotet alle Namen: Spalten mit gemischter Schreibweise im Modell (`Chargenr` gegen `CHARGENR`) ergeben ORA-00904.
- Kollegen nutzen auch `IDesignTimeDbContextFactory` oder `OnConfiguring` mit echtem Connection-String: Die LINQ-Konsole baut deren Optionen mit ihren Interceptors neu und prüft nach jedem Erzeugen, dass beide aktiv sind – sonst liefe `SaveChanges` echt gegen die DB (R5a, A1, ADR 0011; Test gegen `tests/FerretSharp.FakeEfModel`).

## Abgleich und Code
- C#-Modell gegen die DB (WP-27) nur mit **konfigurierten** Facetten vergleichen: Die Provider-Vorgaben (`NVARCHAR2(2000)`, `NUMBER(10)` für `int`) beschreiben nicht die Absicht des Projekts und erzeugen Massen an Abweichungen. Für NULL `ColumnNullable` (EFs Spaltensicht) statt `Nullable` der Property (TPH, Owned).

## ModelHost (Hilfsprozess, .NET 8)
- Roslyn im Projektprozess nur bis 4.11 (ab 4.12 `System.Reflection.Metadata` 9.0, nicht ladbar in .NET 8); `AppContext.BaseDirectory` ist unter `dotnet exec --depsfile` nicht verlässlich (`typeof(Program).Assembly.Location`); Satelliten-Assemblies stehen nicht in der deps.json; ein Interceptor-Exemplar für die ganze Lebensdauer. Roslyn sieht eine Position am Textende als hinter einem unfertigen Lambda.
- Den Prozessweg (Start, Anfragen, Export) prüfen die Tests gegen einen Stand-in, `tests/FerretSharp.FakeModelHost` (Szenario per `--scenario`, beim Export per `--context`); `LinqConsoleHost.StartAsync` nimmt dafür auch die Laufzeitgrenze. Der Endestatus eines Prozesses muss gelesen werden, bevor der Host ihn entsorgt. Bekannt: Ein Host, der beim Start endet, meldet sich erst nach dem Start-Timeout (Backlog).
- Der Neustart für einen neuen Build liegt in `LinqConsoleRestart` (wartet auf den Modell-Reload, einer zur Zeit, keine zweite Runde für einen gescheiterten Build, nichts mehr nach dem Ende). Sein Zustand wird unter seiner Sperre zusammen mit der Prüfung „nicht überholt“ gesetzt; der Zustand der Konsole selbst nur unter `_stateLock`.
- Der Host läuft nie direkt aus dem `bin` des Projekts, sondern aus einer Schattenkopie (`BuildOutputShadow`, ADR 0016) – sonst sperrt er die DLLs gegen jeden Build. `BuildOutputWatcher` lädt das Modell nach einem Build neu, die LINQ-Konsole startet danach neben der alten neu. Schattenkopien, die 7 Tage niemand benutzt hat, entfernt `BuildOutputShadow.DeleteUnusedAsync` beim Start (nur über `SafeDelete`).
- Jeder `dotnet`-Prozess startet über `DotNetCli` und geht danach an `ChildProcesses.Adopt`: Die App steckt ihn in ein Job Object mit `KILL_ON_JOB_CLOSE` (`App/Services/ChildJob`), damit er mit FerretSharp endet – auch nach Absturz oder Kill (Review 3.40.0, A18). Neue Prozessstarts nur über `DotNetCli`.
- Hilfsprozesse (Modell-Export, LINQ-Konsole) nie mit `Process.Kill(entireProcessTree: true)` bzw. `DotNetCli.KillTree` beenden, sondern mit `DotNetCli.Kill`: Der Baum-Kill geht alle Prozesse des Systems durch und wirft für jeden geschützten eine Ausnahme – unter dem VS-Debugger hält jede den Prozess an (UI bis zu 12 s eingefroren). `KillTree` nur für `dotnet build`. Solche Hänger findet `UiStallMonitor` (App; Einstellung „Hänger der Oberfläche protokollieren“, standardmäßig aus; ruft bei Hängern über 1,5 s `dotnet-stack` auf und schreibt `stall-*.txt` ins Log).
