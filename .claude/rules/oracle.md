---
paths:
  - "src/FerretSharp.Core/Oracle/**"
  - "src/FerretSharp.Core/Schema/**"
  - "src/FerretSharp.Core/Query/**"
  - "src/FerretSharp.Core/Data/CellFormatter*"
  - "src/FerretSharp.Core/Data/DataModel*"
  - "src/FerretSharp.Core/Data/LobContent*"
  - "src/FerretSharp.Core/Data/SessionActivity*"
  - "src/FerretSharp.Core/Data/Snapshots*"
  - "src/FerretSharp.Core/Data/TimedQuery*"
  - "src/FerretSharp.Core/Data/ValueCulture*"
  - "tests/FerretSharp.Integration.Tests/**"
---

# Oracle-Fallstricke

Gilt für den Oracle-Layer und alles, was SQL, Typen und Werte anfasst. Domänenmodell und Query-Regeln im Detail: `docs/architecture.md`. Transaktionen und Sperren: `.claude/rules/oracle-transactions.md`; PL/SQL im Dictionary: `.claude/rules/plsql-dictionary.md`.

## Identifier & Werte
- Identifier sind uppercase, es sei denn, sie wurden mit `"…"` angelegt → Namen immer aus den `ALL_*`-Views übernehmen und per `OracleIdentifier.Quote()` exakt so quoten.
- Getippte Namen (Designer, Indizes, Constraints, Audit-Vorlagen) prüft nur `OracleIdentifier.Problem` (Anführungszeichen/NUL, mehr als `MaxBytes` = 128 **Byte**), die Warnung „braucht Anführungszeichen“ nur `NeedsQuotes` (auch reservierte Wörter); gekürzt wird mit `OracleIdentifier.Cut` (nach Bytes, nie ein halbes Zeichen). Vorgeschlagene Namen über `IndexDesign.SuggestName`/`PlainName` (R5b).
- Text als SQL-Literal (Anzeige, Skripte, nie für ausgeführte Werte – die sind Binds) nur über `OracleLiteral` (`N'…'` für nationale Typen).
- Leerer String == NULL. Die UI muss NULL explizit darstellen (kursives `NULL`), nicht als leeres Feld.

## Typen
- `NUMBER` hat bis zu 38 Stellen, `decimal` nur ~28 → `GetDecimal` wirft. `OracleDataReader.SuppressGetDecimalInvalidCastException = true` bzw. `OracleDecimal`; Anzeige als String ohne Präzisionsverlust (`BigNumber`).
- C#-Typ einer `NUMBER`-Spalte (wie EF Core mit dem Oracle-Provider liest; „Entity aus Tabelle“, `PropertyCode.DefaultType`): Scale 0 & Precision ≤ 9 → int, ≤ 18 → long, sonst decimal; `NUMBER` ohne Precision → decimal. Im Grid und beim Binden: mehr als 28 Stellen → `BigNumber`/`OracleDecimal`.
- `DATE` enthält eine Uhrzeit; `TIMESTAMP` reicht bis Nanosekunden (`OracleTimeStamp`, nicht `DateTime`); `TIMESTAMP WITH (LOCAL) TIME ZONE` gesondert.
- `CLOB`/`BLOB`/`RAW`/`LONG`: im Grid nur Länge und Vorschau (Projektion mit `DBMS_LOB.SUBSTR`/`GETLENGTH`, `docs/architecture.md` 1.4). Ganze Werte lädt und schreibt der LOB-Editor (WP-10, ADR 0008); freie Abfragen (SQL-Editor) holen nur die Vorschau mit dem Zeilenabruf (`InitialLOBFetchSize` = 200, Länge und Vorschau aus dem Locator ohne weiteren Roundtrip; `OracleClob.Length` zählt Bytes von UTF-16 – Zeichen = Länge / 2; A12, Review 3.40.0). Ohne Vorabruf kostet jede LOB-Zelle zwei Roundtrips, `GetString` auf einen längeren LOB einen.
- Unbekannte oder exotische Typen (`INTERVAL`, `XMLTYPE`, `JSON`, `BOOLEAN`/`VECTOR` ab 23ai, `BINARY_FLOAT/DOUBLE`, Objekttypen): als read-only Textdarstellung anzeigen, nie abstürzen.
- Typwissen (Anzeige, DDL, Länge in Bytes, Familien, LOB/LONG, was ein Typwechsel braucht) nur in `Schema/OracleTypes` – auch die Einzelentscheidungen (`IsFixedChar`, `IsNational`, `IsDecimalNumber`, `IsTimestamp`, `HasTimeZone`, `IsLocalTimeZone`, `IsInterval` …, R5b); keine Vergleiche wie `DataType is "CHAR" or "NCHAR"` außerhalb.
- National binden (NCHAR/NVARCHAR2/NCLOB) überall, wo ein Wert mit der Spalte verglichen oder in sie geschrieben wird: Filter, FK-Sprung, Anzeigespalten, SQL-Variablen (`SqlVariable.National` folgt der Spalte), LINQ (`NChar` von EF). Sonst werden Zeichen außerhalb des Datenbank-Zeichensatzes zu „?“ (A18).

## Client-Locale (bei deutschem Windows sofort relevant)
- ODP.NET übernimmt NLS-Einstellungen aus dem Windows-Locale → `NLS_SORT=GERMAN` sortiert Ziffern hinter Buchstaben und kann keine Indizes nutzen. `OracleSession` setzt deshalb `Sort`/`Comparison` = `BINARY` (Integrationstest prüft das).
- `OracleDecimal.ToString()` formatiert mit der aktuellen Kultur („1,5“) → vor dem Parsen normalisieren (`OracleDataAccess.NormalizeDecimalSeparator`).

## Sessions & Nebenläufigkeit
- `OracleConnection` ist nicht thread-safe → je Session ein Gate (`SessionGate`, Unit-Tests ohne DB): ein Aufrufer zur Zeit, Schließen lässt alle Wartenden sofort los, Arbeit, die den Abbruch ignoriert, behält das Gate bis zum Ende. `OracleSession` steckt nur die Oracle-Fehlerübersetzung (einzige Stelle für `DatabaseException`) und die Roundtrip-Zeit hinein. Queries pro Workspace laufen sequenziell.
- **Nicht-ASCII in `ActionName`/`ClientInfo` zerstört die Session** (ODP.NET managed 23.26: nächster Roundtrip ORA-12537, schon ein „Ä“ reicht). Jeder Wert für MODULE/ACTION/CLIENT_INFO läuft über `OracleSession.ToSessionAttribute` (transliteriert ä → ae, ß → ss, é → e, – → -, sonst `?`; max. 64 Zeichen).
- `CLIENT_INFO` jeder Session ist ihre Kennung (`OracleSessionDefaults.NewSessionTag`, WP-52, ADR 0027): Daran findet die Explorer-Session eine wartende Workspace-Session in `V$SESSION` und erkennt eigene Workspaces als Sperrhalter. Nicht für anderes verwenden. ODP.NET schickt Session-Attribute mit dem nächsten Aufruf (wie bei `SetActionAsync`), nicht beim Setzen. `V$SESSION.BLOCKING_SESSION` ist eine SID der Instanz `BLOCKING_INSTANCE`: nur auf derselben Instanz joinen.
- Alles async mit `CancellationToken`; Abbruch löst `OracleCommand.Cancel()` aus. Lange Abfragen (`COUNT(*)`, FK-Counts) sind in der UI abbrechbar. `OracleSession.RunAsync` gibt ODP.NET nur ein eigenes Token, das auf einem Pool-Thread abgebrochen wird (A3, Review 3.40.0): `Cancel()` sendet den Break synchron und blockiert über ein stilles VPN – ein `_cts.Cancel()` im UI-Thread darf deshalb nie direkt bei ODP.NET landen. Neue Wege zu einem `OracleCommand` laufen über `RunAsync`.
- **Breaks gehen standardmäßig out-of-band** (TCP-Urgent-Daten), wenn der Server zustimmt. Eine Strecke, die sie verliert, verzögert den Abbruch bis zum Ende der Rechnung; eine, die das URG-Flag löscht, zerstört die Session (ORA-12537/12570, sobald der Server wieder liest). In-band nur über `OracleConfiguration.DisableOOB` (prozessweit, nur vor der ersten Verbindung, sonst ORA-50099; `OracleSession.SendBreaksInBand`, Startargument `--disable-oob`) oder `DISABLE_OOB=on` in einer sqlnet.ora per `Tns_Admin` (pro Verbindung); `(DISABLE_OOB=ON)` im Connect-Descriptor wirkt nicht. **Die gvenzl-Test-Container haben OOB auf dem Server aus** (`DISABLE_OOB=ON`) – Tests zu Breaks schalten es per `AllowOutOfBandBreaksAsync` ein und laufen in `SerialTests`; der Port-Forwarder von Docker Desktop verwirft Urgent-Daten. Nachstellen: `BreakProxy` (`fixes-oob-breaks.md`).
- Sessions öffnen, schließen, Keep-alive und automatisch neu verbinden: `.claude/rules/connections.md`.
- Ganze Ergebnisse (Export aller Zeilen, WP-54, ADR 0029) über `ExecuteStreamingReaderAsync`: ein Cursor, Blöcke an den Aufrufer, Wiederholung im neuen Snapshot **nur vor dem ersten Block** (`NestedReader.HandingOn`; was schon in der Datei steht, ist nicht zurückzuholen – ORA-01555 danach wird gemeldet). Das Gate bleibt so lange belegt; weitere Abfragen während des offenen Cursors (Anzeigespalten nachschlagen) nur über die `NestedReader` des Aufrufs – ein normaler Aufruf der Session würde auf das eigene Gate warten. Sie übersetzt Oracle-Fehler selbst und setzt `_running` zurück, damit Trennen den Cursor abbricht.
- Die Statement-Sperren (`StatementGuard`) nutzen nur Tokenizer und Classifier des SQL-Editors (`SqlTokenizer`, `SqlClassifier`); `SqlScript` ist eine Fassade über diese, `SqlSplitter` und `SqlAnalysis` (ein Test in `SqlScriptTests` prüft die Abhängigkeit).

## Code im Oracle-Layer
- `OracleSchemaReader` ist nach Thema aufgeteilt (`.cs` Katalog und gemeinsame Select-Listen, `.ObjectDetails.cs`, `.Snapshot.cs`, `.Diagnostics.cs`). Statements für eine Tabelle und für das ganze Schema entstehen aus **derselben** Select-Liste plus WHERE/ORDER – nie eine Spaltenliste von Hand kopieren (`ReadColumn` liest nach Position).
- Zeilen lesen über `OracleReading` (`ReadListAsync`, `Text`/`Int`/`Long`/`Date`, `FetchManyRows`).
- Array Binding (WP-42, `ExecuteNonQueryAsync(BatchSpec)`): ODP.NET braucht je Parameter einen festen `OracleDbType` (aus einem Array leitet es keinen ab), NULL als `DBNull` im Array; CLOB/BLOB gehen (gegen Oracle 23 geprüft). Scheitert eine Zeile, kommt **ORA-24381** („Fehler in Array-DML“), der eigentliche Fehler (ORA-02291 …) steht erst in `OracleException.Errors` – `OracleErrors.Translate` nimmt den ersten Fehler einer Zeile.
- `ReadOnlyTests` prüfen jeden SELECT/WITH-Text (Konstante oder statisches Feld) aller Typen in `Core/Oracle` gegen die Lesesperre.
- Row-Key: Bei Tabellen ist ein PK nur Row-Key, wenn er `ENABLED` und `VALIDATED` ist (`TableDetails.PrimaryKeyEnforced`), sonst ROWID; UPDATE/DELETE, das nicht genau eine Zeile trifft, geht zum Savepoint zurück (R5a). Schlüssel über INVISIBLE-Spalten → ROWID (die Spaltenliste filtert `hidden_column = 'NO'`).

## Dictionary & Performance
- `ALL_*`-Views immer nach `OWNER` filtern (`docs/architecture.md` 1.3).
- FK-Spalten sind in Oracle oft nicht indiziert → eingehende FK-Counts lazy, mit Timeout und abbrechbar.
- Großes `OFFSET` wird langsam → Keyset-Paging ist Backlog-Option.
- Dictionary über ein ganzes Schema (WP-20): Constraints gelöschter Tabellen bleiben im Papierkorb unter `BIN$…` in `ALL_CONSTRAINTS` → owner-weite Abfragen nach der Objektliste filtern. Eine `LONG`-Spalte (`DATA_DEFAULT`) zählt mit 32.767 Byte in `OracleDataReader.RowSize` → Fetch-Größe deckeln (`FetchManyRows`, 16 MB). `DEFAULT ON NULL` entfernen nimmt in Oracle 23 auch NOT NULL weg.
- `EXPLAIN PLAN` geht nicht in einer READ-ONLY-Transaktion (ORA-01456) → Explorer-Session. `ReadSqlAsync` blättert durch erneutes Ausführen (`SELECT * FROM (…)` scheitert an doppelten Spaltennamen der EF-Joins, ORA-00918).
