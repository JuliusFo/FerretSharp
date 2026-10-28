# Klein: Gefilterte Spalten ohne Index in „Verwendung“ → Release offen

Branch `feat/usage-filter-without-index` (von `main`), 2026-10-28. Nachtrag zu WP-40 (ADR 0030).

## Anlass
Der Nutzer wollte in „Verwendung“ eine Warnung vor Full Table Scans. Die Ansicht selbst fragt die Datenbank nicht ab, sie liest nur das IL. Gemeint ist deshalb: Code filtert nach einer Spalte, mit der kein Index beginnt, und seine Abfragen lesen womöglich die ganze Tabelle.

Entscheidung des Nutzers: die genaue Variante. Der Scanner unterscheidet, wohin ein Lambda geht (`Where`/`Any`/`First…` gegenüber `Select`/`OrderBy`/`Include`). Die einfache Variante hätte jede Spalte in einem LINQ-Ausdruck gezählt.

Ohne Rückfrage entschieden (einfachste Variante, Fragen unten):
- **Was filtert:** die Bedingung von `Where`, `Any`, `Count`, `LongCount`, `First`, `FirstOrDefault`, `Single`, `SingleOrDefault`, `Last`, `LastOrDefault` aus `Queryable` und die gleichnamigen `…Async` aus `EntityFrameworkQueryableExtensions`. Verschachtelte Lambdas zählen zur äußeren Bedingung (`k => k.Auftraege.Any(a => a.Betrag > x)` filtert nach `Auftraege` und `Betrag`). `OrderBy`, `Join` und `GroupBy` zählen nicht: Ohne Index kosten sie eine Sortierung oder einen Hash-Join, keinen Scan für wenige Zeilen. `All` zählt nicht (Review): Es wird zu `NOT EXISTS (… WHERE NOT …)`, ein Index auf der Spalte hilft dort selten.
- **Wie der Scanner es sieht:** Getter aus einem Expression Tree (`ldtoken`) warten, bis der erste echte Aufruf kommt, der ein `Expression<T>` nimmt (Signatur gelesen). Ist das ein Filter-Operator, wird ein Filter daraus, sonst bleibt es „LINQ“. Aufrufe, mit denen der Compiler den Baum baut (`Expression.*`, `MethodBase`/`FieldInfo.Get…FromHandle`, `Type.GetTypeFromHandle`, `Array.Empty`), und Aufrufe ohne `Expression<T>`-Parameter (`CancellationToken.None` zwischen Baum und `FirstOrDefaultAsync`) zählen nicht. Ein Baum, der zurückgegeben, in einer Variablen oder einem Feld abgelegt (`stloc`/`stfld`/`ret` direkt nach `Expression.Lambda`) oder an eigenen Code gereicht wird (Repository, Specification, auch als `params Expression<…>[]`), bleibt „LINQ“: Wer ihn als Filter nutzt, ist dort nicht zu sehen. Ein Baum, der direkt nach dem Bau in eine Sammlung geht (lokales Dictionary mit Sortierschlüsseln, `List.Add`), ebenso. Ein Getter auf einem Wert, mit dem die Bedingung vergleicht (`a => a.Id == kunde.AdresseId`, auch über ein Feld, eine statische Property oder eine Kette von Properties und Casts wie `request.Kunde.Name`), filtert nicht: Der Compiler baut ihn auf `Expression.Constant` (der Closure) statt auf dem Parameter des Lambdas. **Grenze:** Kommt der Wert über einen Aufruf, einen Indexer oder `??` (`liste.First().No`, `liste[0].No`, `(a ?? b).No`), zählt sein Getter weiter als Filter – dafür müsste der Scanner je Teilausdruck mitführen, ob er am Wert oder an der Zeile hängt (Backlog).
- **Wann die Markierung steht:** Anwendungscode (nicht Tests, nicht Mapping) filtert nach der Property selbst, und kein sichtbarer, nutzbarer Index beginnt mit der Spalte (`IndexAdvice.HasIndexFor`; wie bei Fremdschlüsseln ohne Index zählt nur die führende Spalte, ein funktionsbasierter Index zählt nicht). Ein Filter über eine Navigation (`a => a.Kunde.Name == x`) ist für den Fremdschlüssel ein Join, kein Filter nach ihm. `KUNDEN.NAME` bekommt die Markierung dagegen schon.
- **Anzeige:** im Unter-Tab „Spalten“ ein gelber Hinweis über der Tabelle mit den Spalten („2 Spalten, nach denen der Code filtert, haben keinen Index“) und eine Markierung „kein Index“ in der Zeile (Tooltip: möglicher Full Table Scan, sicher sagt es nur der Plan). Die Art der Verwendung heißt „filtert“ statt „LINQ“ und zählt beim Filter „Lesen“ mit.
- **Indizes laden:** einmal je Tab-Version (F5, nach DDL), sobald die Ansicht sichtbar ist und der Code nach einer Spalte der Tabelle filtert (drei Abfragen, über VPN nicht umsonst); für Views nicht (sie haben keine). Ein Fehler beim Lesen steht über den Spalten (wie in den Detailansichten, mit „Details“), die Markierungen fehlen dann ganz – auch die des letzten Lesens, sie könnten von vor dem DDL sein.
- `AssemblyUses.CurrentFormatVersion` 4 (3 vor dem Review): Der Cache wird einmal neu gelesen.

## Gebaut
- Core: `CodeUseKind.Filter`; `IlUsageScanner` (wartende Getter, `TakesTree`, `BuildsTrees`, `IsFilter`); `IndexAdvice.HasIndexFor`.
- UI: `ColumnUsageRow.Filtered`, `CodeUsageRows.FilteredWithoutIndex`, Art „filtert“; `UsageView` lädt die Indizes (`Version` von `TabView`), Hinweis und Markierung; Texte en/de; CSS `.fs-badge.no-index`.
- Tests: Core `IlUsageScannerTests` (Beispiele `Ticket`/`TicketService` mit `Where`…`OrderBy`…`Select`, `FirstOrDefaultAsync` mit `CancellationToken.None`, verschachteltes `Any`, zurückgegebener Baum, Baum an eigene Methode, verglichene Werte aus Parameter, Feld und Kette, Sortierschlüssel per `switch` und in Variablen, `params`-Array, `All`), `ObjectDetailsTests` (unsichtbarer, unbrauchbarer und funktionsbasierter Index, Spalte weiter hinten); UI `CodeUsageRowsTests`.

## Prüfung
- **Gegen echtes EF Core 8:** ein Projekt im Scratchpad (`Microsoft.EntityFrameworkCore.Relational` 8.0.11) mit typischen Abfragen, gelesen mit dem neuen Scanner. `FirstOrDefaultAsync(k => k.Email == email, ct)`, `AnyAsync(…, CancellationToken.None)`, `CountAsync(k => k.Auftraege.Any(a => a.Betrag > min))`, `SingleOrDefaultAsync(a => a.Nummer == nummer.ToUpper())` und `Where(k => k.Geloescht == null || …)` ergaben Filter. `OrderBy`/`Select`, `Include(a => a.Kunde)` und ein zurückgegebener `Expression<Func<…>>` blieben „LINQ“.
- **Nicht in der App geprüft:** In dieser Sitzung war kein Docker verfügbar, also kein eigener Container für einen E2E-Lauf. Offen sind Hinweis und Markierung in hell und dunkel (Kontrast der Markierung) und das Neuladen der Indizes nach F5 bzw. nach „Index anlegen“.

## Fragen an den Nutzer
- Soll die Markierung auch in die Spalten-Ansicht (wie „ungenutzt“) und in den Hinweis des Designers?
- Soll der Hinweis einen Knopf „Index anlegen“ bekommen (Entwurf in der Index-Ansicht wie bei Fremdschlüsseln ohne Index)?

## Nachtrag: Code-Review (2026-10-28)
Review mit eigenem Reviewer-Agenten; Entscheidung des Nutzers: „Alle beheben“ (bei Nr. 5 `All` streichen).
1. M – Getter auf einem erfassten Wert zählte als Filter (`a => a.Id == kunde.AdresseId` markierte `KUNDEN.ADRESSE_ID`): behoben, der Scanner verfolgt, ob der Baum auf `Expression.Constant` oder auf dem Parameter aufsetzt.
2. M – Baum aus Variable, `switch` oder `params`-Array ging an das nächste `Where`: behoben, `stloc`/`stfld`/`ret` nach `Expression.Lambda` schließen ihn als „LINQ“ ab, `TakesTree` erkennt `Expression<T>[]`.
3. K – Fehler beim Lesen der Indizes unsichtbar, nach F5 blieb der alte Stand: behoben, Hinweis über den Spalten (`DetailStatus`), Markierungen weg.
4. K – Indizes auch ohne gefilterte Spalte gelesen: behoben, erst wenn der Code-Index steht und eine Spalte gefiltert wird.
5. K – `All` als Filter: gestrichen.
Zweiter Durchgang des Reviewers über die Fixes: alle fünf gedeckt, vier Restpunkte. Entscheidung des Nutzers: „B–D beheben, A notieren“.
- A. K – Wert über Aufruf, Indexer oder `??` zählt weiter als Filter: nicht behoben, als Grenze oben und im Backlog notiert.
- B. K – Baum in eine Sammlung (lokales Dictionary, `List.Add`) ging an das nächste `Where`: behoben, der Aufruf direkt nach `Expression.Lambda`, der den Baum ohne `Expression<T>`-Parameter nimmt, schließt ihn als „LINQ“ ab.
- C. K – kein Test für eine Konstante vor dem Getter des Parameters (`"open" == t.Status`) und einen statischen Wert: Test ergänzt (sichert bestehendes Verhalten, schlug deshalb vorher nicht fehl).
- D. K – alter Lesefehler blieb stehen, wenn ein Build die letzte gefilterte Spalte entfernte: behoben, der Hinweis erscheint nur, solange der Code filtert (beim Rendern abgeleitet; der erste Versuch löschte ihn im Laden und griff erst nach F5).

Dritter Durchgang über B–D: B und C in Ordnung; zwei Fehler der eigenen Fixes, als Teil der freigegebenen Punkte behoben:
- D'. K – der erste Fix für D griff nur nach F5 (Versionsprüfung davor): s. o.
- E. K – B beendete den Baum auch bei einem Konstruktor ohne Argumente direkt nach dem Lambda (`FirstOrDefault(…, new Ticket())` verlor den Filter): `newobj` ausgenommen, Test.

Geprüft gegen echtes EF Core 8 (Release, auch in async-Methoden) und das Probe-Projekt des Reviewers (Debug und Release gleich). Fehleranzeige und spätes Laden der Indizes nicht in der App geprüft (kein Docker).

## Nachtrag: E2E (2026-10-28)
Vor dem Merge, Stand mit `main` (3.53.0 + Audit-Fixes + Werteauswahl): eigener Container `ferret-e2e-enum` (Port 1547, Beispielschema), Build in den Scratchpad, eigene Instanz mit `--data-dir`, eigenem WebView2-Ordner, Debug-Port, `--lang=en`, Passwort ohne Speichern. Als Anwendungscode eine Kopie der Beispielmodelle im Scratchpad mit eigener `.slnx` und einem Projekt `SampleApp`: `Where(k => k.Name == name)`, `FirstOrDefaultAsync(k => k.Email == email, ct)`, `FirstOrDefault(k => k.KundeId == id)`, `OrderBy(k => k.Umsatz).Select(k => k.Name)`. Bedienung per CDP (Stufe 2):
- KUNDEN › Verwendung: Hinweis „1 column the code filters by has no index: NAME“, NAME mit „no index“; KUNDE_ID (PK) und UMSATZ (nur `OrderBy`) ohne. `Email` hat keine Spalte in KUNDEN (Drift des Beispielmodells), also keine Markierung. NAME aufgeklappt: `ByName` als „filters“, `ByTurnover` (`Select`) als „LINQ“.
- Hell und dunkel (Screenshot): Hinweis und Markierung gut lesbar.
- „Index anlegen“ in der Index-Ansicht (`IX_KUNDEN_NAME`, nach Bestätigung): Zurück in „Verwendung“ sind Hinweis und Markierung weg, ohne F5.
- Index außerhalb der App gelöscht (sqlplus), dann F5 in „Verwendung“: Hinweis und Markierung wieder da.
- Sessions der Instanz im Container beendet (`KILL SESSION`), dann F5: über den Spalten „The connection to the database was lost. Without the indexes, columns filtered without one are not marked.“ mit „Details“, keine Markierung, kein Hinweis. Nach „Reconnect“: Hinweis und Markierung wieder da.
- Log: nur die `[WRN]` „Query failed: ORA-12537“ des absichtlichen Abbruchs.
- Aufgefallen: „no index“ hatte fast dieselbe Farbe wie „unused“ (`--warn` gegenüber `--yellow`) in derselben Spalte, und der englische Hinweis sagte bei einer Spalte „Queries filtering by them“. Entscheidung des Nutzers: „Rahmen in Warnfarbe“ und „Neutral formulieren“ – „no index“ hat jetzt einen Rahmen in `--warn` (`box-shadow: inset`, die Zeile behält ihre Höhe), der Hinweis heißt „A query filtering by such a column may read the whole table …“ (Deutsch unverändert). Hell und dunkel im Screenshot nachgeprüft.
