# ADR 0009: C#-Modell aus dem kompilierten DbContext, geladen in einem Hilfsprozess

- Status: akzeptiert
- Datum: 2026-10-05

## Kontext

v3 soll FerretSharp das Datenmodell des eigenen .NET-Projekts kennen lassen (WP-11 ff.): Entity- und Property-Namen, CLR-Typen, Enums mit C#-Namen, Navigations. Das Projekt des Nutzers:

- DB-first in der Praxis: erst die Datenbank ändern, dann Entity und Konfiguration von Hand nachziehen; **keine Migrations**, also kein Model-Snapshot.
- Eine Namenskonvention im Code (Tabellennamen komplett groß, `KundenId` → `KUNDEN_ID`) und **eigene Value Converter**.
- Ein DbContext in einer Klassenbibliothek, Entities in einem anderen Projekt; Konstruktor `AppDbContext(DbContextOptions options)`.
- EF Core 8 (am meisten genutzt), daneben Projekte mit EF Core 3.1 und 5, die gerade auf 8 umgestellt werden.

Erwogen wurden drei Wege:

1. **Statisch aus dem Quelltext** (Roslyn): kein Build, kein fremder Code läuft. Aber Konventionen und Converter sind Code; eigene Converter lassen sich statisch nicht auswerten – Enum-Filter würden still falsche Werte erzeugen. Außerdem ein eigener Nachbau des EF-Modellaufbaus, der jeder EF-Version folgen müsste.
2. **Kompiliert, im FerretSharp-Prozess** (`AssemblyLoadContext`): schnell, aber der Code des Nutzers läuft in der .NET-10-WPF-Runtime (fehlende Shared Frameworks, Versionskonflikte mit FerretSharps Oracle-Treiber), Abstürze oder Hänger treffen die App, Entladen nach einem Rebuild ist unzuverlässig.
3. **Kompiliert, in einem Hilfsprozess** wie `dotnet ef`.

## Entscheidung

Weg 3 (Entscheidung des Nutzers nach Abwägung, ausschlaggebend: eigene Converter):

- `FerretSharp.ModelHost` (Konsolenprogramm, net8.0, kompiliert gegen EF Core 8 und `Oracle.EntityFrameworkCore` 8 nur zur Übersetzung) wird mit `dotnet exec --depsfile <Projekt>.deps.json --runtimeconfig <erzeugt> --additionalprobingpath <NuGet-Cache>` gestartet. Er läuft damit mit Runtime, EF-Version und Provider des Projekts; EF 9/10 funktionieren über Binding an die höhere Version. **Minimum EF Core 8** (Metadaten-API ab 6 stabil; 3.1/5 = Backlog).
- Der Context wird ohne den Startcode des Nutzers erzeugt: `IDesignTimeDbContextFactory<T>`, sonst eigene Options (`UseOracle` mit einem Platzhalter-Connection-String – das Modell zu bauen verbindet nie) und der Konstruktor mit `DbContextOptions`/`DbContextOptions<T>`, sonst ein parameterloser Konstruktor. Ein Startup-Projekt (Host des Nutzers) ist erst nötig, wenn der Konstruktor weitere Abhängigkeiten hat – Backlog, bis es gebraucht wird.
- Gelesen wird das Design-Time-Modell (`IDesignTimeModel`). Für Enums und `bool` mit Convertern berechnet der Host die Wertetabelle (C#-Wert → DB-Wert) mit dem Converter des Projekts; später filtert FerretSharp damit, ohne Code des Nutzers auszuführen.
- Ausgabe: JSON in eine Datei (nicht stdout – der Code des Nutzers kann auf die Konsole schreiben). Das Format (`ModelExport`) ist eine gemeinsame Quelldatei von Core und ModelHost.
- In FerretSharp landet das Modell in einer **Annotation-Schicht** nach (Owner, Tabelle, Spalte) – `TableDetails`/`ColumnInfo` bleiben reine DB-Sicht (Abschnitt 2). Ein Abgleich mit dem Schema zeigt Drift (Properties ohne Spalte, Spalten ohne Property).
- Gelesen wird der vorhandene Build-Output; „Neu bauen“ startet `dotnet build` auf Wunsch, ein veralteter Build (Quelltext neuer als die DLL) wird angezeigt.
- Fundstellen im Quelltext (Sprung in Visual Studio) und Doc-Kommentare kommen in WP-12 statisch dazu – dafür ist keine Modell-Logik nötig.

## Konsequenzen

- Voraussetzung ist ein erfolgreicher Build und ein installiertes .NET (`dotnet` im PATH) mit der Runtime des Projekts.
- Der Modellaufbau des Nutzers (`OnModelCreating`) läuft im Hilfsprozess; Abstürze dort sind Fehlermeldungen, keine App-Abstürze.
- WP-13 (LINQ-Konsole) baut auf demselben Hilfsprozess auf; ein eigener Connection String des Projekts darf dort nie benutzt werden.
