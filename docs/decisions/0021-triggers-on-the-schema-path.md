# ADR 0021: Trigger über den Schemaweg – eine enge Ausnahme für PL/SQL

- Status: akzeptiert
- Datum: 2026-10-12

## Kontext

WP-26 legt zu einer Tabelle eine Audit-Tabelle und einen Trigger aus einer Vorlage an und zieht beide nach, wenn sich die Tabelle ändert. Der Schemaweg (ADR 0019) wies bisher alle PL/SQL-Objekte ab (`CREATE [OR REPLACE] PROCEDURE/FUNCTION/PACKAGE/TRIGGER/TYPE`, Entscheidung des Nutzers 2026-10-10: eigene Stufe). Ein Trigger-Rumpf enthält `;` und `:new`/`:old` – beides hat die Schranke bisher als „mehrere Statements“ bzw. „Bind-Variable“ abgewiesen, und der Splitter des SQL-Editors schnitt ihn am ersten inneren `;`.

Ein Trigger mit Kompilierfehler lässt jede DML auf seiner Tabelle scheitern (ORA-04098), für alle Benutzer. ODP.NET meldet einen Kompilierfehler als ORA-24344 („success with compilation error“); die Fehler selbst stehen in `ALL_ERRORS` (in WP-28 schon gelesen, `ISchemaReader.GetErrorsAsync`).

Entscheidungen des Nutzers (2026-10-12):
- **Trigger allgemein:** Jedes `CREATE [OR REPLACE] TRIGGER` darf über den Schemaweg laufen, auch von Hand im SQL-Editor – nicht nur Trigger aus der Vorlage. Ein Skript des Designers lässt sich im SQL-Editor öffnen und dort ausführen, und die Schranke sieht nur den Text, nicht seine Herkunft.
- **Erst DISABLE, dann ENABLE:** Ein Trigger wird ausgeschaltet angelegt, auf Kompilierfehler geprüft und nur eingeschaltet, wenn er gültig ist.
- Procedures, Functions, Packages und Types bleiben abgewiesen (Backlog „PL/SQL … Bearbeiten/Kompilieren“).

## Entscheidung

- **Eigene Arten im Classifier:** `SqlStatementKind.Trigger` für Trigger auf einer Tabelle oder View (auch `INSTEAD OF`, Compound-Trigger), `SqlStatementKind.SystemTrigger` für Trigger `ON DATABASE`, `ON PLUGGABLE DATABASE` und `ON [owner.]SCHEMA`; die übrigen PL/SQL-Objekte bleiben `PlSqlObject`. Entscheidend ist das erste `ON` nach dem Namen des Triggers; ein gequotetes `"SCHEMA"` ist eine Tabelle.
- **System-Trigger bleiben abgewiesen** (eigene Meldung): Ein Logon- oder DDL-Trigger feuert bei jeder Anmeldung bzw. jedem DDL im Schema oder in der Datenbank – ein Fehler darin sperrt im schlimmsten Fall alle aus. Das geht über WP-26 hinaus und war nicht gemeint.
- **Die Schranke** (`StatementGuard.IsDdl`): Ein Trigger ist ein Statement, auch mit `;` im Rumpf – Oracle übersetzt alles nach `CREATE` als den Trigger, Text hinter seinem `END` ist ein Kompilierfehler, nie ein zweites Statement. Die Prüfung auf Bind-Variablen entfällt für Trigger: `:new`, `:old` und die Namen aus `REFERENCING` sind seine Zeilen. Für alles andere DDL gilt die Schranke unverändert (ein Statement, keine Binds).
- **Die Analyse** (`SqlAnalysis`): Ein Trigger zählt als DDL (`IsDdl`: Bestätigung, Rollback vorher, Schemaweg, Schema neu laden) und hat keine Bind-Variablen, nach denen der Editor fragen müsste.
- **Blockmodus im Splitter:** Ein Trigger, ein anderes PL/SQL-Objekt oder ein `BEGIN`/`DECLARE`-Block reicht bis zu einer Zeile mit nur `/` oder bis zum Ende des Textes; `;` und Leerzeilen trennen ihn nicht (wie SQL*Plus). Ob ein Statement ein Block ist, entscheidet der Classifier beim ersten `;` bzw. der ersten Leerzeile – bis dahin steht der Kopf. Der Text des Statements behält das `;` nach dem `END`. Abgewiesene Blöcke (Procedure, `BEGIN … END;`) sind damit ein Statement mit einer Meldung statt mehrerer Bruchstücke.
- **Anlegen in zwei Schritten** (Umsetzung im nächsten Schritt von WP-26): `CREATE … TRIGGER` läuft mit `DISABLE` (vor `WHEN`/`DECLARE`/`BEGIN`/`COMPOUND`/`CALL` eingesetzt), ORA-24344 wird abgefangen und die Fehler aus `ALL_ERRORS` gemeldet (im SQL-Editor als Marker); nur ein gültiger Trigger wird mit `ALTER TRIGGER … ENABLE` eingeschaltet. Schreibt der Nutzer selbst `ENABLE` oder `DISABLE`, bleibt das unangetastet.

## Konsequenzen

- Ein Trigger läuft danach bei jeder DML auf seiner Tabelle – sein Rumpf ist Code des Nutzers. Ein DML-Trigger kann nicht committen (ORA-04092), außer mit `PRAGMA AUTONOMOUS_TRANSACTION`; das liegt beim Nutzer wie jeder andere Code in der Datenbank.
- Beim Ersetzen eines bestehenden Triggers gibt es zwischen `CREATE OR REPLACE … DISABLE` und `ENABLE` eine Lücke von Millisekunden, in der er nicht feuert (bei Audit-Triggern: Änderungen ohne Audit-Zeile). Bewusst in Kauf genommen: Die Alternative, ein ungültiger eingeschalteter Trigger, legt die Tabelle für alle lahm. Bei einem Kompilierfehler bleibt der neue Trigger aus; der alte ist dann schon ersetzt.
- Ein Skript ohne `/` nach einem Block wird bis zum Ende ein Statement; der Rest landet im Rumpf und ist ein Kompilierfehler (der Trigger bleibt aus).
- Per Integrationstest zu belegen (WP-26): Text hinter dem `END` eines Triggers wird nicht ausgeführt; ob `CREATE OR REPLACE TRIGGER` auf eine offene Transaktion einer anderen Session wartet wie `ALTER TABLE … ADD` (dann braucht es die Sperre vorher wie bei `ALTER TABLE`).
- `ReadOnlyTests` decken die neuen Fälle ab: Trigger angenommen (auch mit `:new`/`:old`, `<<label>>`, `GOTO`), System-Trigger, Packages und Blöcke abgewiesen.
