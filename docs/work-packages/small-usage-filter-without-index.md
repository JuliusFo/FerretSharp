# Klein: Gefilterte Spalten ohne Index in „Verwendung“ → Release offen

Branch `feat/usage-filter-without-index` (von `main`), 2026-10-28. Nachtrag zu WP-40 (ADR 0030).

## Anlass
Der Nutzer wollte in „Verwendung“ eine Warnung vor Full Table Scans. Die Ansicht selbst fragt die Datenbank nicht ab, sie liest nur das IL. Gemeint ist deshalb: Code filtert nach einer Spalte, mit der kein Index beginnt, und seine Abfragen lesen womöglich die ganze Tabelle.

Entscheidung des Nutzers: die genaue Variante. Der Scanner unterscheidet, wohin ein Lambda geht (`Where`/`Any`/`First…` gegenüber `Select`/`OrderBy`/`Include`). Die einfache Variante hätte jede Spalte in einem LINQ-Ausdruck gezählt.

Ohne Rückfrage entschieden (einfachste Variante, Fragen unten):
- **Was filtert:** die Bedingung von `Where`, `Any`, `All`, `Count`, `LongCount`, `First`, `FirstOrDefault`, `Single`, `SingleOrDefault`, `Last`, `LastOrDefault` aus `Queryable` und die gleichnamigen `…Async` aus `EntityFrameworkQueryableExtensions`. Verschachtelte Lambdas zählen zur äußeren Bedingung (`k => k.Auftraege.Any(a => a.Betrag > x)` filtert nach `Auftraege` und `Betrag`). `OrderBy`, `Join` und `GroupBy` zählen nicht: Ohne Index kosten sie eine Sortierung oder einen Hash-Join, keinen Scan für wenige Zeilen.
- **Wie der Scanner es sieht:** Getter aus einem Expression Tree (`ldtoken`) warten, bis der erste echte Aufruf kommt, der ein `Expression<T>` nimmt (Signatur gelesen). Ist das ein Filter-Operator, wird ein Filter daraus, sonst bleibt es „LINQ“. Aufrufe, mit denen der Compiler den Baum baut (`Expression.*`, `MethodBase`/`FieldInfo.Get…FromHandle`, `Type.GetTypeFromHandle`, `Array.Empty`), und Aufrufe ohne `Expression<T>`-Parameter (`CancellationToken.None` zwischen Baum und `FirstOrDefaultAsync`) zählen nicht. Ein Baum, der zurückgegeben oder an eigenen Code gereicht wird (Repository, Specification), bleibt „LINQ“: Wer ihn als Filter nutzt, ist dort nicht zu sehen.
- **Wann die Markierung steht:** Anwendungscode (nicht Tests, nicht Mapping) filtert nach der Property selbst, und kein sichtbarer, nutzbarer Index beginnt mit der Spalte (`IndexAdvice.HasIndexFor`; wie bei Fremdschlüsseln ohne Index zählt nur die führende Spalte, ein funktionsbasierter Index zählt nicht). Ein Filter über eine Navigation (`a => a.Kunde.Name == x`) ist für den Fremdschlüssel ein Join, kein Filter nach ihm. `KUNDEN.NAME` bekommt die Markierung dagegen schon.
- **Anzeige:** im Unter-Tab „Spalten“ ein gelber Hinweis über der Tabelle mit den Spalten („2 Spalten, nach denen der Code filtert, haben keinen Index“) und eine Markierung „kein Index“ in der Zeile (Tooltip: möglicher Full Table Scan, sicher sagt es nur der Plan). Die Art der Verwendung heißt „filtert“ statt „LINQ“ und zählt beim Filter „Lesen“ mit.
- **Indizes laden:** einmal je Tab-Version (F5, nach DDL), sobald die Ansicht sichtbar ist und die Tabelle eine Entity hat; für Views nicht (sie haben keine). Ein Fehler beim Lesen wird gemeldet wie jeder Lesefehler, dann fehlt nur die Markierung.
- `AssemblyUses.CurrentFormatVersion` 3: Der Cache wird einmal neu gelesen.

## Gebaut
- Core: `CodeUseKind.Filter`; `IlUsageScanner` (wartende Getter, `TakesTree`, `BuildsTrees`, `IsFilter`); `IndexAdvice.HasIndexFor`.
- UI: `ColumnUsageRow.Filtered`, `CodeUsageRows.FilteredWithoutIndex`, Art „filtert“; `UsageView` lädt die Indizes (`Version` von `TabView`), Hinweis und Markierung; Texte en/de; CSS `.fs-badge.no-index`.
- Tests: Core `IlUsageScannerTests` (Beispiele `Ticket`/`TicketService` mit `Where`…`OrderBy`…`Select`, `FirstOrDefaultAsync` mit `CancellationToken.None`, verschachteltes `Any`, zurückgegebener Baum, Baum an eigene Methode), `ObjectDetailsTests` (unsichtbarer, unbrauchbarer und funktionsbasierter Index, Spalte weiter hinten); UI `CodeUsageRowsTests`.

## Prüfung
- **Gegen echtes EF Core 8:** ein Projekt im Scratchpad (`Microsoft.EntityFrameworkCore.Relational` 8.0.11) mit typischen Abfragen, gelesen mit dem neuen Scanner. `FirstOrDefaultAsync(k => k.Email == email, ct)`, `AnyAsync(…, CancellationToken.None)`, `CountAsync(k => k.Auftraege.Any(a => a.Betrag > min))`, `SingleOrDefaultAsync(a => a.Nummer == nummer.ToUpper())` und `Where(k => k.Geloescht == null || …)` ergaben Filter. `OrderBy`/`Select`, `Include(a => a.Kunde)` und ein zurückgegebener `Expression<Func<…>>` blieben „LINQ“.
- **Nicht in der App geprüft:** In dieser Sitzung war kein Docker verfügbar, also kein eigener Container für einen E2E-Lauf. Offen sind Hinweis und Markierung in hell und dunkel (Kontrast der Markierung) und das Neuladen der Indizes nach F5 bzw. nach „Index anlegen“.

## Fragen an den Nutzer
- Soll die Markierung auch in die Spalten-Ansicht (wie „ungenutzt“) und in den Hinweis des Designers?
- Soll der Hinweis einen Knopf „Index anlegen“ bekommen (Entwurf in der Index-Ansicht wie bei Fremdschlüsseln ohne Index)?
