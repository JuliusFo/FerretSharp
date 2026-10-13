---
paths:
  - "src/FerretSharp.Core/Oracle/**"
  - "src/FerretSharp.Core/Schema/**"
  - "src/FerretSharp.Core/Query/**"
  - "src/FerretSharp.Core/Data/**"
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
- `CLOB`/`BLOB`/`RAW`/`LONG`: im Grid nur Länge und Vorschau (Projektion mit `DBMS_LOB.SUBSTR`/`GETLENGTH`, `docs/architecture.md` 1.4). Ganze Werte lädt und schreibt der LOB-Editor (WP-10, ADR 0008); freie Abfragen (SQL-Editor) laden LOBs ganz (`InitialLOBFetchSize = -1`).
- Unbekannte oder exotische Typen (`INTERVAL`, `XMLTYPE`, `JSON`, `BOOLEAN`/`VECTOR` ab 23ai, `BINARY_FLOAT/DOUBLE`, Objekttypen): als read-only Textdarstellung anzeigen, nie abstürzen.
- Typwissen (Anzeige, DDL, Länge in Bytes, Familien, LOB/LONG, was ein Typwechsel braucht) nur in `Schema/OracleTypes` – auch die Einzelentscheidungen (`IsFixedChar`, `IsNational`, `IsDecimalNumber`, `IsTimestamp`, `HasTimeZone`, `IsLocalTimeZone`, `IsInterval` …, R5b); keine Vergleiche wie `DataType is "CHAR" or "NCHAR"` außerhalb.
- National binden (NCHAR/NVARCHAR2/NCLOB) überall, wo ein Wert mit der Spalte verglichen oder in sie geschrieben wird: Filter, FK-Sprung, Anzeigespalten, SQL-Variablen (`SqlVariable.National` folgt der Spalte), LINQ (`NChar` von EF). Sonst werden Zeichen außerhalb des Datenbank-Zeichensatzes zu „?“ (A18).

## Client-Locale (bei deutschem Windows sofort relevant)
- ODP.NET übernimmt NLS-Einstellungen aus dem Windows-Locale → `NLS_SORT=GERMAN` sortiert Ziffern hinter Buchstaben und kann keine Indizes nutzen. `OracleSession` setzt deshalb `Sort`/`Comparison` = `BINARY` (Integrationstest prüft das).
- `OracleDecimal.ToString()` formatiert mit der aktuellen Kultur („1,5“) → vor dem Parsen normalisieren (`OracleDataAccess.NormalizeDecimalSeparator`).

## Sessions & Nebenläufigkeit
- `OracleConnection` ist nicht thread-safe → je Session ein Gate (`SessionGate`, Unit-Tests ohne DB): ein Aufrufer zur Zeit, Schließen lässt alle Wartenden sofort los, Arbeit, die den Abbruch ignoriert, behält das Gate bis zum Ende. `OracleSession` steckt nur die Oracle-Fehlerübersetzung (einzige Stelle für `DatabaseException`) und die Roundtrip-Zeit hinein. Queries pro Workspace laufen sequenziell.
- **Nicht-ASCII in `ActionName`/`ClientInfo` zerstört die Session** (ODP.NET managed 23.26: nächster Roundtrip ORA-12537, schon ein „Ä“ reicht). Jeder Wert für MODULE/ACTION/CLIENT_INFO läuft über `OracleSession.ToSessionAttribute` (transliteriert ä → ae, ß → ss, é → e, – → -, sonst `?`; max. 64 Zeichen).
- Alles async mit `CancellationToken`; Abbruch löst `OracleCommand.Cancel()` aus (Token-Registration). Lange Abfragen (`COUNT(*)`, FK-Counts) sind in der UI abbrechbar.
- **ODP.NET schließt eine Connection synchron** (kein eigenes `DisposeAsync`) und blockiert auf einer still gewordenen Verbindung (VPN) bis TCP aufgibt → nie auf dem UI-Thread. `OracleSession.DisposeAsync` bricht das laufende Kommando ab, alle Aufrufer bekommen sofort `OperationCanceledException`; Rollback und Close laufen im Hintergrund mit Zeitgrenze, ein Kommando, das den Abbruch ignoriert, bleibt zurück und schließt später (R5a). Nachstellen ohne VPN: TCP-Proxy vor dem Test-Container, der auf Kommando alle Daten verwirft.
- Verbindungsabbruch (Idle-Timeout, Firewall, `IDLE_TIME`-Profil) laut melden. `ConnectionKeepAlive` pingt alle 2 min die Sessions, die mindestens 1 min ruhten (laufende übersprungen, Ping-Timeout 30 s), und meldet einen Abbruch sofort über das Banner; abschaltbar (`AppSettings.KeepAlive`, alle Einstellungen über `AppSettingsService`). Entscheidung des Nutzers: Die Sessions bleiben auch gegen ein `IDLE_TIME`-Limit offen, auch mit offener Schreib-Transaktion; die Statusleiste warnt ab 10 min. Bricht eine Session trotzdem ab, sind ihre nicht committeten Änderungen weg – das Banner sagt es.
- Die Statement-Sperren (`StatementGuard`) nutzen nur Tokenizer und Classifier des SQL-Editors (`SqlTokenizer`, `SqlClassifier`); `SqlScript` ist eine Fassade über diese, `SqlSplitter` und `SqlAnalysis` (ein Test in `SqlScriptTests` prüft die Abhängigkeit).

## Code im Oracle-Layer
- `OracleSchemaReader` ist nach Thema aufgeteilt (`.cs` Katalog und gemeinsame Select-Listen, `.ObjectDetails.cs`, `.Snapshot.cs`, `.Diagnostics.cs`). Statements für eine Tabelle und für das ganze Schema entstehen aus **derselben** Select-Liste plus WHERE/ORDER – nie eine Spaltenliste von Hand kopieren (`ReadColumn` liest nach Position).
- Zeilen lesen über `OracleReading` (`ReadListAsync`, `Text`/`Int`/`Long`/`Date`, `FetchManyRows`).
- `ReadOnlyTests` prüfen jeden SELECT/WITH-Text (Konstante oder statisches Feld) aller Typen in `Core/Oracle` gegen die Lesesperre.
- Row-Key: Bei Tabellen ist ein PK nur Row-Key, wenn er `ENABLED` und `VALIDATED` ist (`TableDetails.PrimaryKeyEnforced`), sonst ROWID; UPDATE/DELETE, das nicht genau eine Zeile trifft, geht zum Savepoint zurück (R5a). Schlüssel über INVISIBLE-Spalten → ROWID (die Spaltenliste filtert `hidden_column = 'NO'`).

## Dictionary & Performance
- `ALL_*`-Views immer nach `OWNER` filtern (`docs/architecture.md` 1.3).
- FK-Spalten sind in Oracle oft nicht indiziert → eingehende FK-Counts lazy, mit Timeout und abbrechbar.
- Großes `OFFSET` wird langsam → Keyset-Paging ist Backlog-Option.
- Dictionary über ein ganzes Schema (WP-20): Constraints gelöschter Tabellen bleiben im Papierkorb unter `BIN$…` in `ALL_CONSTRAINTS` → owner-weite Abfragen nach der Objektliste filtern. Eine `LONG`-Spalte (`DATA_DEFAULT`) zählt mit 32.767 Byte in `OracleDataReader.RowSize` → Fetch-Größe deckeln (`FetchManyRows`, 16 MB). `DEFAULT ON NULL` entfernen nimmt in Oracle 23 auch NOT NULL weg.
- `EXPLAIN PLAN` geht nicht in einer READ-ONLY-Transaktion (ORA-01456) → Explorer-Session. `ReadSqlAsync` blättert durch erneutes Ausführen (`SELECT * FROM (…)` scheitert an doppelten Spaltennamen der EF-Joins, ORA-00918).
