# Fix: SQL-Editor trennt nur an `;` und `/`-Zeile (`fix/sql-split-at-semicolon`) → Release offen

Branch `fix/sql-split-at-semicolon` (von `main`), 2026-11-03.

## Anlass
Meldung des Nutzers: Ein `CREATE OR REPLACE FORCE EDITIONABLE VIEW … PIVOT (…)` ließ sich im SQL-Editor nicht ausführen. Die Statement-Art war nicht die Ursache (das Statement ist DDL, `FORCE` beendet nur die Suche nach einem PL/SQL-Objekttyp, und der Schemaweg nimmt es). Ursache war eine Leerzeile mitten im View-Text: Der Splitter trennte an jeder Leerzeile außerhalb von PL/SQL-Blöcken. Ctrl+Enter im unteren Teil lief dann „PIVOT …“ und wurde abgewiesen, im oberen Teil ging eine halbe View an Oracle.

## Entscheidung des Nutzers (2026-11-03)
- **Statements trennen nur noch `;` und eine Zeile mit nur `/`** – keine Leerzeile mehr. Damit ändert sich die Entscheidung aus WP-17 (ADR 0014, Nachtrag dort). Verworfen: Leerzeile trennt nur bei geschlossenen Klammern und nur vor einem Wort, mit dem ein Statement beginnt (Heuristik mit Randfällen).
- Folge, bewusst in Kauf genommen: Statements ohne `;` untereinander laufen als eines und scheitern in Oracle am Syntaxfehler. Die Sperren prüfen weiter das ganze Stück; ein verschmolzenes Stück ist entweder ungültiges SQL oder wird abgewiesen.

## Gebaut
- Core `SqlSplitter.Split`: kein Schnitt an Leerzeilen; ob ein Block (Trigger, PL/SQL) vorliegt, entscheidet sich am ersten `;`. `StatementAt` behält die Leerzeile nur für die Lücke zwischen zwei Statements (Cursor nach `…;` oder auf einer Leerzeile dazwischen).
- Core `SqlSplitter.Terminated` (über `SqlScript`): schließt das letzte Statement eines Texts ab – `;` direkt nach dem letzten Code (vor einem Kommentar dahinter), bei einem Block eine `/`-Zeile.
- UI `SqlView.AdoptAsync` (Klick im Verlauf): hängt erst nach `Terminated` an, sonst liefe das letzte offene Statement in das eingefügte hinein.
- Texte in Englisch und Deutsch: Tooltip zu Ausführen, Leertext des Editors, „Kein Statement am Cursor“, `SqlRejectNotSingle`.

## Offen / nicht geprüft
- Nicht in der laufenden App geprüft (E2E) und Integrationstests nicht gelaufen; ihre Skripte trennen mit `;` und `/`, hängen also nicht an der Leerzeile.
