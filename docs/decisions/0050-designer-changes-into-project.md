# ADR 0050: Designer-Änderungen ins C#-Projekt – Roslyn im Core, Schreiben nur in den Arbeits-Klon

- Status: akzeptiert
- Datum: 2026-11-12
- Paket: WP-41 (`docs/work-packages/wp-41-designer-to-code.md`)

## Kontext

Das Projekt des Nutzers ist DB-first von Hand: erst die Tabelle ändern, dann Entity und `IEntityTypeConfiguration` nachziehen. Der Designer (WP-23) zeigte für neue Spalten bisher Zeilen zum Kopieren, „Entity aus Tabelle“ eine ganze Klasse. Mit WP-41 schreibt FerretSharp zum ersten Mal in das Repository des Nutzers. Dafür muss es die Klasse, die Property und die Konfiguration im Quelltext finden – auch wenn der Build gerade kaputt ist – und darf nichts außer den gewollten Stellen verändern.

## Entscheidung (des Nutzers, 2026-11-12, Plan bestätigt)

- **Roslyn im Core, nur Syntax.** `Microsoft.CodeAnalysis.CSharp` (5.9) parst die Dateien des Arbeits-Klons. Kein Kompilieren, kein `MSBuildWorkspace`: Der braucht MSBuild und Design-Time-Builds der ganzen Solution (Minuten bei 425 Entities), und ein kaputter Build störte die Analyse. Dateien werden über ihre Bezeichner gefunden (Wörter wie der Entity-Name), nicht über ihren Namen und nicht über den Index aus WP-40 – der ist höchstens ein Hinweis.
- **Text-Änderungen statt neu gedruckter Syntax.** Roslyn liefert die Stellen, geändert werden nur diese Spannen (`TextChange`); Einrückung, Kommentare und Formatierung der Datei bleiben, wie sie sind. Kodierung (UTF-8/UTF-16, mit oder ohne BOM), Zeilenenden und Einrückungseinheit werden aus der Datei gelernt; eine Datei, die nicht UTF-8 oder UTF-16 ist, wird nicht angefasst. Ergäbe eine Änderung mehr Syntaxfehler als vorher, wird nichts geschrieben.
- **ModelHost bleibt bei Roslyn 4.11** über eine `VersionOverride`: Die transitive Festlegung (`CentralPackageTransitivePinningEnabled`) zöge ihn sonst auf 5.9, und ab 4.12 lädt Roslyn in .NET 8 nicht (`System.Reflection.Metadata` 9.0). Die App liefert beide aus, getrennt im Ordner `modelhost`.
- **Ziel ist nur der aktive Arbeits-Klon der Gruppe** (WP-40), und nur einer, der das verknüpfte Projekt am selben relativen Pfad hält. Nie der verknüpfte Branch-Klon einer Verbindung: Der muss dem Stand der DB entsprechen und wird gepullt. Warnung (nicht blockierend), wenn der Arbeits-Klon auf dem Branch eines verknüpften Klons der Gruppe steht, und für Dateien mit nicht committeten Änderungen (`git status`).
- **Erst DB, dann Code.** Die Vorschau zeigt der Designer-Reiter „C#“ vor dem Ausführen; geschrieben wird erst, wenn das DDL gelaufen ist (Spaltenansicht, „Ins Projekt schreiben“).
- **Nur, was das Projekt konfiguriert.** Facetten (Länge, Stellen, NULL) werden angepasst, wo sie stehen (`HasMaxLength`, `HasPrecision`, `HasColumnType`, `IsRequired`, `[MaxLength]`, `[StringLength]`, `[Precision]`, `[Column(TypeName)]`, `[Required]`, `?` am Typ); fehlt eine, kommt keine dazu – wie der Abgleich in WP-27. Umbenennen und Typwechsel werden in der ersten Stufe nur genannt.
- **Neue Tabellen (zweite Stufe, auf Wunsch des Nutzers im selben Paket):** zwei neue Dateien dort, wo die meisten Entities bzw. Konfigurationen liegen, im Stil der Dateien dort; `DbSet` nur, wenn der Kontext DbSets hat (als Kopie des letzten); Registrierung mit `ApplyConfiguration` nur, wo der Kontext einzeln registriert, nichts bei `ApplyConfigurationsFromAssembly`. Die Gegenseite einer Navigation wird nur als Hinweis genannt. Alte Projektformate (`Compile Include`) bekommen eine Warnung, die `.csproj` bleibt unverändert.

## Folgen

- Schreiben: alle Dateien werden vorher geprüft – strikt unter dem Arbeits-Klon, kein Link (Junction, symbolischer Link) auf dem Weg, eine geänderte Datei noch mit dem Hash der Analyse, eine neue noch nicht vorhanden; geht eine nicht, wird keine geschrieben. Dann je Datei über eine temporäre Datei daneben und Verschieben; eine neue Datei nie über eine, die inzwischen entstanden ist. Kein Build, kein Commit.
- `git status` läuft mit `core.fsmonitor=false` (die Konfiguration eines Repositories könnte dafür ein Programm nennen), `GIT_OPTIONAL_LOCKS=0` und Zeitlimit; fehlt git, sagt die Vorschau, dass diese Prüfung entfällt.
- Namen aus der Datenbank gelangen nur über `CSharpCode.String`/`CSharpCode.Identifier` bzw. die Prüfung der Modelldatei (`ModelFileCheck`) in den Code; der Syntax-Check nach der Änderung ist eine zweite Sicherung.
- Die App wird um die Roslyn-Assemblies größer (Satelliten nur en/de).
