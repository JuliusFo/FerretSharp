# ADR 0014: Freier SQL-Editor – Statement-Art im Core, Ausführung über die bestehenden Wege

- Status: akzeptiert
- Datum: 2026-10-06

## Kontext

WP-17: SQL selbst schreiben und in der Session eines Workspaces ausführen – bisher entstand jedes Statement in FerretSharp (QueryBuilder, Schema-Reader, DmlBuilder) oder kam von EF (LINQ-Konsole). Die Leseschranke (`IsReadOnlyStatement`) ist bewusst „eine Stolperfalle gegen Programmierfehler, kein SQL-Parser“; mit frei geschriebenem SQL muss sie trotzdem tragen. Gesperrte Workspaces laufen in `SET TRANSACTION READ ONLY` – Oracle lehnt dort DML ab, DDL aber nicht (implizites Commit).

Entscheidungen des Nutzers (2026-10-05/06): SELECT überall, INSERT/UPDATE/DELETE/MERGE nur auf schreibbaren Workspaces in deren Transaktion, alles andere mit Begründung abweisen; Ctrl+Enter führt das Statement am Cursor aus (`;`, Leerzeile, `/`-Zeile trennen), eine Markierung hat Vorrang; Bestätigung auf Prod immer, sonst nur bei UPDATE/DELETE ohne WHERE; Bind-Variablen mit Eingabefeldern; Autovervollständigung; Verlauf je Verbindung (Prod ohne Werte); Export des Ergebnisses; „In SQL-Editor öffnen“ aus der SQL-Vorschau; Ctrl+Shift+Q.

## Entscheidung

- **Kein SQL-Parser, ein Tokenizer im Core** (`SqlScript`, Query/): Kommentare, Literale (auch `N'…'`, `q'[…]'`), Namen in Anführungszeichen und Bind-Variablen sind einzelne Tokens – ein `;` oder `:name` darin zählt nicht. Darauf: Trennen in Statements, Statement am Cursor, Statement-Art (erstes Wort; `ALTER SESSION/SYSTEM` gesondert), WHERE auf oberster Ebene, `FOR UPDATE`, Tabellen mit Aliasen, Spalten, mit denen Bind-Variablen verglichen werden. `SqlStatementInfo.Rejection` begründet jede Abweisung auf Deutsch.
- **Ausführen nur über die vorhandenen Wege, die Schranken bleiben:** Abfragen über `IDataAccess.ReadSqlAsync` (Leseschranke, READ-ONLY-Snapshot bei gesperrten Workspaces, Seiten durch erneutes Ausführen), DML über `IDataEditor.ExecuteAsync` (Transaktion bei Bedarf beginnen, eigener Savepoint `FS_EXEC_n`, gesperrte Workspaces abgelehnt). Die Analyse im Core entscheidet nur, welcher Weg gefragt wird; schützen tun weiterhin die Schranken der Session – was der Editor abweist, weisen auch sie ab (Integrationstest).
- **`IsWriteStatement` nimmt zusätzlich MERGE** – bewusste Erweiterung des Schreibwegs, weiterhin ein einzelnes Statement, nie DDL, nur in einer offenen Transaktion. Der Lesepfad weist MERGE weiter ab.
- `FOR UPDATE` und `LOCK TABLE` werden abgewiesen: Zeilen sperrt FerretSharp nur beim Editieren (mit `WAIT n`).
- **Bind-Variablen** (`SqlBinds`): Werte wie im Filter (deutsche/invariante Zahlen, deutsches/ISO-Datum, Hex), Typen Text, Text (CHAR – blank-padded wie im Grid), Zahl, Datum, Hex (RAW), NULL. Neue Variablen bekommen den Typ der Spalte, mit der sie verglichen werden. Gebunden nach Namen (`BindByName`), auch doppelt verwendete und in anderer Schreibweise.
- **Autovervollständigung** (`SqlCompletion`) aus dem Schema-Cache: nach FROM/JOIN/INTO/UPDATE/USING Tabellen, Views, Synonyme (mit Entity), nach `alias.` die Spalten dieser Tabelle in Schema-Reihenfolge (mit Typ und Property), sonst die Spalten aller Tabellen des Statements und Schlüsselwörter; Namen mit Bedarf (gemischte Schreibweise, reservierte Wörter) gequotet. Monaco fragt .NET über einen Completion-Provider je Sprache.
- **Verlauf** (`SqlHistoryStore`): `history\{Verbindung}.json`, neueste zuerst, höchstens 500, Wiederholung ersetzt den letzten Eintrag; Prod-Verbindungen ohne Bind-Werte. Wird mit der Verbindung gelöscht.
- Freie Abfragen liefern LOBs wie das Tabellen-Grid als Vorschau mit Länge (die Session liest sie trotzdem ganz – Grenze, Backlog).

## Konsequenzen

- Grenze: Eine Funktion mit autonomer Transaktion kann aus einem SELECT heraus schreiben; die Leseschranke sieht das nicht, auf gesperrten Workspaces auch der READ-ONLY-Snapshot nicht. Die einzige echte Garantie bleibt ein Benutzer mit reinen SELECT-Rechten (CLAUDE.md, Abschnitt 2).
- DDL, PL/SQL und Skripte in einem Rutsch bleiben außen vor (Backlog: DDL auf Dev ohne offene Transaktion, Skript ausführen).
- Ergebnisse freier Abfragen zeigen DB-Werte (keine Enum-Namen): ohne Bezug zwischen Ergebnisspalte und Tabellenspalte gibt es keine Präsentationsschicht.
