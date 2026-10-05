# ADR 0013: Code-Generierung – LINQ, Objektinitialisierer und Seeds aus der Annotation-Schicht

- Status: akzeptiert
- Datum: 2026-10-05

## Kontext

WP-15, Abschluss von v3: aus dem, was FerretSharp anzeigt, C#-Code für das eigene Projekt erzeugen – die Filter eines Tabs als LINQ, markierte Zeilen als Objekte. Grundlage ist die Annotation-Schicht aus WP-11/WP-12 (`ClrModelMapping`, `TablePresentation`, `ValueTable`): Property-Namen, C#-Typen und die mit den Convertern des Projekts berechneten Wertetabellen für Enums und konvertierte Bools. Filter tragen DB-Werte (ADR 0010); für LINQ müssen sie zurückübersetzt werden.

Entscheidungen des Nutzers (2026-10-05):

- Zeilen: eine Zeile als `var kunde = new Kunde { … };`, mehrere als `List<Kunde> kunden = [ new() { … }, … ];`; NULL-Werte weglassen.
- LINQ findet **dieselben Zeilen wie das Grid**, nicht die idiomatischste Schreibweise: Textsuche ohne Groß-/Kleinschreibung über `ToUpper()`, ein Datum ohne Uhrzeit als Bereich über den ganzen Tag.
- Lücken (Spalte ohne Property, DB-Wert ohne Member) als **Kommentar im Code plus Hinweis**, nicht weglassen und nicht absichtlich nicht kompilierbar.
- Umfang: LINQ kopieren und in einer neuen LINQ-Konsole öffnen, Objektinitialisierer, `HasData`-Seed, einzelne Zelle als C#-Wert. **Kein Bogus** (wenig Nutzen gegenüber dem Aufwand).

## Entscheidung

- Der Code entsteht **im Core aus der Annotation-Schicht**, nicht im Hilfsprozess: `CSharpCode` (Literal je Property-Typ), `LinqFilter` (Filter → `.Where(x => …)`), `CSharpRows` (Initializer, `HasData`, einzelne Zelle). Ohne Projekt-Code und ohne Roslyn testbar; Integrationstests kompilieren die Ausgabe in der LINQ-Konsole gegen das Beispielprojekt und prüfen das SQL, das EF daraus macht.
- Werte: Enums und konvertierte Bools über die `ValueTable` (Member-Name, Flags-Kombination `A | B`, als Zahl gespeichertes Enum ohne Member als Cast `(Kundenart)7`), alles andere nach den Standard-Mappings von EF Core (`decimal` mit `m`, `DateTime`-Konstruktor nur so genau wie nötig, `Guid` aus RAW(16) in .NET-Byte-Reihenfolge wie beim Oracle-Provider, `byte[]` als `Convert.FromHexString`). Andere eigene Converter lassen sich ohne Projekt-Code nicht umrechnen → Kommentar mit DB-Wert.
- LINQ-Semantik wie `QueryBuilder`: „≠“ als `!=` (EF übersetzt das für nullable Properties mit `IS NULL`, wie FerretSharp), Vergleiche auf Strings über `string.Compare`, „in“ als `new T[] { … }.Contains(x.P)`, Shadow-Properties über `EF.Property<T>(x, "…")`. Übersetzt werden die gültigen bearbeiteten Filter, sonst die angewendeten (wie die SQL-Vorschau).
- Der Modell-Export trägt jetzt den **DbSet-Namen** je Entity (optionales Feld, Formatversion bleibt): `db.Kunden.Where(…)` für die Konsole, `kunden` als Listenname; ohne DbSet `db.Set<Kunde>()` bzw. `kundeList`.
- Seeds als `builder.HasData(…)` für eine `IEntityTypeConfiguration`; Zeilen mit Werten von Shadow-Properties werden anonyme Objekte (nur so nimmt EF sie an). Keyless Entities, Entities nur auf Views und Owned-Typen bekommen keinen Seed (Menüeintrag deaktiviert mit Grund).
- Properties anderer Entity-Typen derselben Tabelle (Owned-Typen, Table-Splitting) setzt der Generator nicht, sie werden kommentiert.

## Konsequenzen

- Die Ausgabe hat keine `using`-Zeilen und kurze Typnamen; sie passt in Code, der die Namespaces des Modells schon kennt (und in die LINQ-Konsole, die sie importiert).
- Erzeugter Code nutzt Konstruktoren ab .NET 7 (`DateTime` mit Mikrosekunden) – Projekte mit EF Core 8 laufen auf .NET 8.
- Owned-Typen als verschachtelte Initializer und „Alle Zeilen des Filters als C#“ sind spätere Erweiterungen (Backlog).
