# Fix: SQL-Editor trennt nur an `;` und `/`-Zeile (`fix/sql-split-at-semicolon`) → Release 3.59.0

Branch `fix/sql-split-at-semicolon` (von `main`), 2026-11-03.

## Anlass
Meldung des Nutzers: Ein `CREATE OR REPLACE FORCE EDITIONABLE VIEW … PIVOT (…)` ließ sich im SQL-Editor nicht ausführen. Die Statement-Art war nicht die Ursache (das Statement ist DDL, `FORCE` beendet nur die Suche nach einem PL/SQL-Objekttyp, und der Schemaweg nimmt es). Ursache war eine Leerzeile mitten im View-Text: Der Splitter trennte an jeder Leerzeile außerhalb von PL/SQL-Blöcken. Ctrl+Enter im unteren Teil lief dann „PIVOT …“ und wurde abgewiesen, im oberen Teil ging eine halbe View an Oracle.

## Entscheidung des Nutzers (2026-11-03)
- **Statements trennen nur noch `;` und eine Zeile mit nur `/`** – keine Leerzeile mehr. Damit ändert sich die Entscheidung aus WP-17 (ADR 0014, Nachtrag dort). Verworfen: Leerzeile trennt nur bei geschlossenen Klammern und nur vor einem Wort, mit dem ein Statement beginnt (Heuristik mit Randfällen).
- Folge, bewusst in Kauf genommen: Statements ohne `;` untereinander laufen als eines und scheitern in Oracle am Syntaxfehler. Die Sperren prüfen weiter das ganze Stück; ein verschmolzenes Stück ist entweder ungültiges SQL oder wird abgewiesen.

## Gebaut
- Core `SqlSplitter.Split`: kein Schnitt an Leerzeilen; ob ein Block (Trigger, PL/SQL) vorliegt, entscheidet sich am ersten `;`. `StatementAt` behält die Leerzeile nur für die Lücke zwischen zwei Statements (Cursor nach `…;` oder auf einer Leerzeile dazwischen).
- Blöcke hinter einem Statement ohne `;` (nach dem Review): Das `CREATE` eines PL/SQL-Objekts beginnt ein eigenes Statement (mit den Kommentarzeilen direkt darüber), wenn das Statement davor kein Block ist. Ein `BEGIN`/`DECLARE`-Block am Zeilenanfang außerhalb von Klammern – gefolgt von einem Wort, das kein SQL fortsetzt (`ContinuesSql`), von einer PL/SQL-Zuweisung oder einem Aufruf (`offset := 0`, `keep;`) oder einem Label `<<…>>` – hält das Statement davor bis zur `/`-Zeile zusammen; `SqlStatementInfo.RunsIntoBlock` weist es ab (`SqlRejectRunsIntoBlock`, en und de).
- Core `SqlSplitter.Terminated` (über `SqlScript`): schließt das letzte Statement eines Texts ab – `;` direkt nach dem letzten Code (vor einem Kommentar dahinter), bei einem Block oder einem Statement, das in einen Block läuft, eine `/`-Zeile.
- Core `SqlSplitter.Append` (über `SqlScript`): Skript und Statement aus dem Verlauf, beide mit `Terminated` abgeschlossen, in den Zeilenenden des Skripts (ohne Zeilenumbruch die der Plattform, wie Monaco); liefert den Anfang des Statements für den Cursor. UI `SqlView.AdoptAsync` nutzt es.
- Texte in Englisch und Deutsch: Tooltip zu Ausführen, Leertext des Editors, „Kein Statement am Cursor“, `SqlRejectNotSingle`, `SqlRejectRunsIntoBlock`.

## Bekannte Grenzen (ADR 0021, Nachtrag)
- Zwei Blöcke ohne `/` dazwischen bleiben ein Statement (wie bisher).
- `BEGIN` in der Zeile des Statements davor (`UPDATE t SET a = 1 BEGIN`) wird nicht erkannt, ebenso wenig hinter einem Statement, dem auch eine `)` fehlt – der Block zerfällt dann wie vor dem Fix.
- Eine Spalte `begin` am Zeilenanfang, gefolgt von einem Alias, gilt als Block: Das Statement wird abgewiesen, nie zerschnitten.

## Offen / nicht geprüft
- Nicht in der laufenden App geprüft (E2E) und Integrationstests nicht gelaufen; ihre Skripte trennen mit `;` und `/`, hängen also nicht an der Leerzeile.

## Nachtrag: Code-Review (2026-11-03)
Entscheidung des Nutzers: „Alle, 1 mit A“; zweiter Durchgang „Alle, C nur Text“; dritter Durchgang „H beheben, G notieren“; vierter Durchgang „I beheben“.
- 1 (M) Block hinter einem Statement ohne `;` zerfiel an seinen inneren `;`, Ctrl+Enter lief ein DML-Stück seines Rumpfs – behoben (Variante A: vor einem PL/SQL-`CREATE` trennen, `BEGIN`/`DECLARE` abweisen), dcfda9c4.
- 2 (M) Verlauf hängte `;` blind an (im Kommentar, `END;;` bei Blöcken) – behoben mit `SqlScript.Append`, dcfda9c4.
- 3 (K) ADR 0021 nannte noch die Leerzeile – Nachtrag, 478e0302.
- 4 (K) Fälle für `Terminated` fehlten – ergänzt, dcfda9c4.
- 5 (K) Pivot-Werte aus der DB des Nutzers im Test – neutralisiert, dcfda9c4.
- A (M) Label `<<…>>` nach `BEGIN` nicht erkannt (Tokenizer kennt kein `<<`) – behoben, fd17f7ac.
- B (K) Spalte `begin` in Klammern (`CREATE TABLE`) schluckte den Rest des Skripts – behoben (nur Klammertiefe 0), fd17f7ac.
- C (K) „immer“ im ADR-Nachtrag – nur Text korrigiert, kein Trennen im Block, 06250647.
- D (K) Protokoll nicht nachgezogen – dieser Abschnitt.
- E (K) Cursor nach dem Anhängen aus dem Verlauf bei CRLF eine Zeile zu früh – behoben (Zeilenenden des Skripts), fd17f7ac; in Monaco nicht live geprüft.
- F (Hinweis) `BEGIN` in derselben Zeile – als Grenze notiert.
- G (K) `BEGIN` hinter einem Statement mit offener Klammer – als Grenze notiert.
- H (K) PL/SQL-Variable wie `offset` als erste Anweisung – behoben (`:=`/`;` danach), a8025f66.
- I (K) Der Fix für H nahm `begin` vor `DESC;`/`ASC;`/`END;` für einen Block und wies den Rest des Skripts ab – behoben (die drei sind in PL/SQL reserviert, nie ein Aufruf), 246585c4.
