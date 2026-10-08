# ADR 0011: LINQ-Konsole – EF übersetzt im Hilfsprozess, FerretSharp führt aus

- Status: akzeptiert
- Datum: 2026-10-05

## Kontext

WP-13: LINQ-Queries gegen den DbContext des verknüpften Projekts ausführen, vor allem Queries, die der Nutzer direkt aus seinem Code kopiert (mit `_context`, Parametern wie `customerId`, `request.From`, `ct`). Der Code muss dort laufen, wo DbContext, Converter, Enums und Extension-Methoden des Projekts geladen sind: im Hilfsprozess aus ADR 0009. Zugleich sollen die eigenen, uncommitteten Änderungen des Workspaces sichtbar sein und der Prod-Schutz (READ ONLY, Freischaltung) gelten. Beides hängt an der Oracle-Session des Workspaces im FerretSharp-Prozess, die sich nicht in einen anderen Prozess reichen lässt.

Erwogen:

1. **Hilfsprozess führt mit eigener Connection aus:** echte C#-Objekte, Include-Graphen, `SaveChanges`. Aber eine zweite Session mit eigener Transaktion: keine Sicht auf Workspace-Änderungen, Prod-Schutz und Commit/Rollback doppelt.
2. **Hilfsprozess übersetzt nur, FerretSharp führt aus.**

## Entscheidung

Weg 2 (Entscheidung des Nutzers):

- Der Hilfsprozess läuft die Query per **Roslyn-Scripting** (`Microsoft.CodeAnalysis.CSharp.Scripting`) gegen den DbContext. EF-Interceptors unterdrücken das Öffnen der Verbindung und jedes Kommando und schreiben SQL und Parameter (mit `OracleDbType`) mit; EF bekommt ein leeres Ergebnis. So entsteht genau das SQL des Projekts, auch für `FirstOrDefaultAsync`, `CountAsync`, `ExecuteUpdateAsync`. Im Spike mit dem Beispielprojekt belegt: Der Oracle-Provider braucht dafür keine Datenbank.
- FerretSharp führt das SQL auf der **Session des Workspaces** aus: Lesesperre (`IsReadOnlyStatement`), READ-ONLY-Snapshots gesperrter Workspaces und eigene uncommittete Änderungen gelten wie im Grid. `ExecuteUpdate`/`ExecuteDelete` laufen nach Bestätigung nur auf schreibbaren Workspaces, in deren Transaktion (Commit/Rollback wie beim Editieren).
- **Grenzen:** Ergebnisse sind die Zeilen des SQL, keine C#-Objekte. Kommandos, die von Ergebnissen abhängen (Split-Queries, `SaveChanges` geladener Entities), entstehen nicht – nur das erste Kommando. Spätere Option: Der Hilfsprozess reicht Kommandos live an FerretSharp durch und bekommt echte Zeilen zurück (Backlog).
- **Roslyn 4.11**, nicht neuer: Ab 4.12 bringt Roslyn `System.Reflection.Metadata` 9.0 mit, das in einer .NET-8-Runtime (EF-8-Projekte) nicht neben der Framework-Version 8.0 geladen werden kann (Spike: `FileLoadException`). 4.11 versteht C# 12. Roslyn liegt neben dem Host; dessen Resolver lädt es aus seinem Ordner, weil die deps.json des Projekts es nicht kennt.
- Der Hilfsprozess läuft für die Konsole **dauerhaft** (ein Prozess je verknüpftem Projekt, Modell und DbContext-Typ einmal geladen), Anfragen über eine Named Pipe (stdout gehört dem Code des Nutzers).
- **Editor: Monaco 0.57.0** (Entscheidung des Nutzers), lokal unter `FerretSharp.UI/wwwroot/lib/monaco/` wie AG Grid, AMD-Build (`min/vs`) ohne Bundler, ohne die Sprachdienste für TypeScript/CSS/HTML/JSON (6 MB statt 25 MB). Upstream ist der AMD-Build „deprecated“; die vendorte Version bleibt davon unberührt. IntelliSense für C# bräuchte einen Sprachserver und kommt später (Diagnosen von Roslyn werden als Markierungen gezeigt).
- Kopierte Queries: Unbekannte Namen (CS0103) werden erkannt; Namen, die wie der Kontext benutzt werden (`_context.Kunden`), werden auf den DbContext abgebildet, `ct`/`cancellationToken` auf einen Token; für die übrigen schlägt FerretSharp Deklarationen vor (Typ aus dem Vergleich, wo möglich).
- Die Konsole ist ein **Tab im Workspace**; Code und Variablen werden mit dem Workspace gespeichert.

## Konsequenzen

- Monaco ist eine neue Abhängigkeit des UI-Projekts (CLAUDE.md, Abschnitt 3); der eigene SQL-Highlighter bleibt für die SQL-Anzeige.
- Das abgefangene SQL wird ohne Umbau ausgeführt; Seiten nach der ersten werden durch erneutes Ausführen und Überspringen gelesen (ein `SELECT * FROM (…)` scheitert an doppelten Spaltennamen der EF-Joins, ORA-00918).
- `SaveChanges` und Objektgraphen sind nicht Teil von WP-13.
- Nachtrag (ADR 0016): Die Konsole läuft aus einer Schattenkopie des Build-Outputs. Nach einem Build startet ein neuer Hilfsprozess im Hintergrund; der alte beantwortet Ausführungen, bis der neue bereit ist. Der Start läuft nicht auf dem UI-Thread und lässt sich abbrechen.

## Nachtrag WP-28 (2026-10-08)

Monaco zeigt jetzt auch den PL/SQL-Quelltext gespeicherter Objekte an, **nur lesend** (`readOnly`, `renderValidationDecorations: 'on'`, damit die Kompilierfehler als Marker sichtbar bleiben). Entscheidung des Nutzers: Monaco statt des eigenen Highlighters `SqlCode`, weil Packages Tausende Zeilen haben (Monaco virtualisiert, `SqlCode` erzeugt ein Element je Token) und Zeilennummern, Ctrl+F und Sprung zur Fehlerzeile gebraucht werden. Monacos eingebaute Sprache `sql` folgt T-SQL; für PL/SQL registriert `monaco.js` eine eigene kleine Monarch-Grammatik `plsql` (Schlüsselwörter, Typen, Kommentare, Strings inkl. `q'[…]'`) – reine Darstellung, keine Logik. Keine neue Abhängigkeit.
