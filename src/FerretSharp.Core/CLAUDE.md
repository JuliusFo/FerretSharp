# FerretSharp.Core – Oracle-Fallstricke und Fallen

Wird automatisch geladen, sobald Dateien unter `src/FerretSharp.Core/` gelesen werden. Ergänzt die `CLAUDE.md` im Repo-Root; Domänenmodell und Query-Regeln im Detail: `docs/architecture.md`.

## Oracle-Fallstricke

**Identifier & Werte**
- Identifier sind uppercase, es sei denn, sie wurden mit `"…"` angelegt → Namen immer aus den `ALL_*`-Views übernehmen und per `OracleIdentifier.Quote()` exakt so quoten.
- Leerer String == NULL. Die UI muss NULL explizit darstellen (z. B. kursives `NULL`), nicht als leeres Feld.

**Typen**
- `NUMBER` hat bis zu 38 Stellen, `decimal` nur ~28 → `GetDecimal` wirft. `OracleDataReader.SuppressGetDecimalInvalidCastException = true` setzen bzw. `OracleDecimal` nutzen; Anzeige als String ohne Präzisionsverlust.
- Heuristik für Editor-Typen: Scale 0 & Precision ≤ 9 → int, ≤ 18 → long, sonst decimal/`OracleDecimal`. `NUMBER` ohne Precision → `OracleDecimal`.
- `DATE` enthält eine Uhrzeit; `TIMESTAMP` reicht bis Nanosekunden (`OracleTimeStamp`, nicht `DateTime`); `TIMESTAMP WITH (LOCAL) TIME ZONE` gesondert.
- `CLOB`/`BLOB`/`RAW`/`LONG`: im Grid nur Länge und Vorschau (nicht vollständig laden; z. B. `InitialLOBFetchSize` oder `DBMS_LOB.SUBSTR`). Bearbeitung (v2) in eigenem Editor-Dialog.
- Unbekannte oder exotische Typen (`INTERVAL`, `XMLTYPE`, `JSON`, `BOOLEAN`/`VECTOR` ab 23ai, `BINARY_FLOAT/DOUBLE`, Objekttypen): als read-only Textdarstellung anzeigen, nie abstürzen.

**Client-Locale (bei deutschem Windows sofort relevant)**
- ODP.NET übernimmt NLS-Einstellungen aus dem Windows-Locale → `NLS_SORT=GERMAN` sortiert Ziffern hinter Buchstaben und kann keine Indizes nutzen. `OracleSession` setzt deshalb `Sort`/`Comparison` = `BINARY` (Integrationstest prüft das).
- `OracleDecimal.ToString()` formatiert mit der aktuellen Kultur („1,5“) → vor dem Parsen normalisieren (`OracleDataAccess.NormalizeDecimalSeparator`).

**Sessions & Nebenläufigkeit**
- `OracleConnection` ist nicht thread-safe → pro Session ein `SemaphoreSlim(1,1)` (kein `lock`, wegen `await`); Queries pro Workspace laufen sequenziell.
- **ODP.NET (managed, 23.26) verliert die Session, wenn `ActionName`/`ClientInfo` Nicht-ASCII enthalten**: Der nächste Roundtrip endet mit ORA-12537, die Connection ist weg (schon ein einzelnes „Ä“ reicht; per Integrationstest gefunden). `OracleSession.ToSessionAttribute` transliteriert deshalb (ä → ae, ß → ss, é → e, – → -, sonst `?`) und kürzt auf 64 Zeichen. Gilt für jeden Wert, der in MODULE/ACTION/CLIENT_INFO landet.
- Alles async mit `CancellationToken`; Abbruch löst `OracleCommand.Cancel()` aus (Token-Registration). Lang laufende Abfragen (`COUNT(*)`, FK-Counts) sind in der UI abbrechbar.
- Verbindungsabbruch (Idle-Timeout, Firewall, `IDLE_TIME`-Profil) erkennen und laut melden. Seit v1.6 pingt `ConnectionKeepAlive` alle 2 min die Sessions, die mindestens 1 min ruhten (laufende werden übersprungen, Ping-Timeout 30 s), und meldet einen Abbruch sofort über das Banner. Abschaltbar in den Einstellungen (`AppSettings.KeepAlive`; alle Einstellungen laufen über `AppSettingsService`, damit sich Änderungen nicht gegenseitig überschreiben). Nebenwirkung, vom Nutzer so entschieden: Die Sessions bleiben auch gegen ein `IDLE_TIME`-Limit offen – in v2 bei offenen Transaktionen neu bewerten. In v2 gehen dabei uncommittete Änderungen verloren → das muss der Nutzer klar sehen.
- Das Gate der Session (ein Aufrufer zur Zeit, Schließen lässt alle sofort los, Arbeit, die den Abbruch ignoriert, behält das Gate bis zum Ende) ist `SessionGate` – mit Unit-Tests ohne DB (R3b). `OracleSession` steckt nur die Oracle-Fehlerübersetzung und die Roundtrip-Zeit hinein.
- `OracleSession` ist der einzige Besitzer der Connection: Dispose bricht das laufende Kommando ab und wartet auf das Gate; alle Aufrufer (auch der des laufenden Kommandos) bekommen sofort `OperationCanceledException`. ODP.NET schließt eine Connection **synchron** und blockiert auf einer still gewordenen Verbindung (VPN) bis TCP aufgibt – deshalb nie auf dem UI-Thread: Ignoriert das Kommando den Abbruch, behält es das Gate, und die Connection schließt im Hintergrund. Nachstellen ohne VPN: TCP-Proxy vor dem Test-Container, der auf Kommando alle Daten verwirft. Die Statement-Sperren (`StatementGuard`) nutzen den Tokenizer des SQL-Editors (`SqlScript.Tokenize`).

**Transaktionen (v2)**
- Uncommittetes Update in Workspace A blockiert ein Update derselben Zeile in B. Ein normales `UPDATE` wartet unbegrenzt; `ORA-00054` gibt es nur bei `NOWAIT`. Deshalb `FOR UPDATE WAIT n` (→ `ORA-30006`, in Oracle 23 `ORA-00054`) und Dialog mit dem sperrenden Workspace bzw. der Session (`V$SESSION`, falls Rechte vorhanden).
- Lange offene Transaktionen halten Locks und Undo. Die Statusleiste zeigt „Tx offen seit X min · N Zeilen gesperrt“.
- Prod im gesperrten Zustand: `SET TRANSACTION READ ONLY` (Oracle erzwingt das selbst). Achtung, Snapshot-Semantik: Alle Abfragen sehen den Stand vom Transaktionsbeginn. **Umgesetzt (WP-08, ADR 0006, Entscheidung des Nutzers):** Jede neue Abfrage (erste Seite: Tab öffnen, Filter, Sortierung, F5) startet einen neuen Snapshot, weitere Seiten bleiben darin. Nach Rollback wird neu gesetzt. Bei ORA-01555, ORA-01466 (DDL nach Snapshot-Beginn, auch noch ~1 s danach) und ORA-08176 (erstes Segment einer Tabelle mit verzögerter Segment-Erzeugung) automatisch neu starten und wiederholen.
- `SET TRANSACTION READ ONLY` nur innerhalb einer ODP.NET-Transaktion (`BeginTransaction`) – ohne sie committet ODP.NET sofort und der Schutz ist weg. DDL läuft auch in einer Read-only-Transaktion (implizites Commit). Belegt in `TransactionBehaviorTests`.
- **DDL gegen fremde offene Transaktionen (WP-22, ADR 0019):** `DROP TABLE` auf einer Tabelle mit nicht committeter DML einer anderen Session scheitert sofort mit ORA-00054. `ALTER TABLE … ADD` dagegen **wartet** (`enq: TX - row lock contention`), bis die andere Transaktion endet – und weder `OracleCommand.Cancel()` noch `CommandTimeout` noch `Close()` der Connection beenden das Warten (`Close` blockiert); danach läuft das DDL trotzdem durch. Ein Integrationstest, der auf ORA-00054 wartete, hing deshalb. Sofort ORA-00054: `MODIFY`, `DROP/RENAME COLUMN`, `ADD CONSTRAINT`, `CREATE INDEX`, `DROP TABLE`, `RENAME`; kein Konflikt: `COMMENT`, `GRANT`, `CREATE VIEW`. Deshalb holt sich der Schemaweg vor `ALTER TABLE` die Tabelle mit `LOCK TABLE … IN EXCLUSIVE MODE NOWAIT` (scheitert bei fremder DML nach Millisekunden, braucht keine Rechte; ein DDL danach in derselben ODP.NET-Transaktion läuft, und `Rollback`/`Dispose` des Transaktionsobjekts danach schadet nicht). `V$LOCKED_OBJECT` darf ein Entwicklerbenutzer meist nicht lesen (ORA-00942).
- Concurrency-Check optional über `ORA_ROWSCN` (nur zuverlässig bei `ROWDEPENDENCIES`-Tabellen) oder Original-Werte im WHERE.
- `EXPLAIN PLAN` geht nicht in einer READ-ONLY-Transaktion (ORA-01456) → Explorer-Session. `ReadSqlAsync` blättert durch erneutes Ausführen (`SELECT * FROM (…)` scheitert an doppelten Spaltennamen der EF-Joins, ORA-00918).

**Code im Oracle-Layer (R3b)**
- `OracleSchemaReader` ist nach Thema aufgeteilt (`.cs` Katalog und gemeinsame Select-Listen, `.ObjectDetails.cs`, `.Snapshot.cs`, `.Diagnostics.cs`). Statements für eine Tabelle und für das ganze Schema entstehen aus **derselben** Select-Liste plus WHERE/ORDER – nie eine Spaltenliste von Hand kopieren (`ReadColumn` liest nach Position).
- Zeilen lesen über `OracleReading` (`ReadListAsync`, `Text`/`Int`/`Long`/`Date`, `FetchManyRows`).
- Typwissen (Anzeige, DDL, Länge in Bytes, Familien, LOB/LONG) nur in `Schema/OracleTypes`.
- `ReadOnlyTests` prüfen jeden SELECT/WITH-Text (Konstante oder statisches Feld) aller Typen in `Core/Oracle` gegen die Lesesperre.

**Dictionary & Performance**
- `ALL_*`-Views immer nach `OWNER` filtern (`docs/architecture.md` 1.3).
- FK-Spalten sind in Oracle oft nicht indiziert → eingehende FK-Counts lazy, mit Timeout und abbrechbar.
- Großes `OFFSET` wird langsam → Keyset-Paging ist Backlog-Option.
- Dictionary über ein ganzes Schema (WP-20): Constraints gelöschter Tabellen bleiben im Papierkorb unter `BIN$…` in `ALL_CONSTRAINTS` → owner-weite Abfragen nach der Objektliste filtern. Eine `LONG`-Spalte (`DATA_DEFAULT`) zählt mit 32.767 Byte in `OracleDataReader.RowSize` → Fetch-Größe deckeln (`FetchManyRows`, 16 MB). `DEFAULT ON NULL` entfernen nimmt in Oracle 23 auch NOT NULL weg.

**PL/SQL im Dictionary (WP-28)**
- `ALL_ARGUMENTS`: Oracle 23 legt für Unterprogramme **ohne Parameter gar keine Zeile** an (ältere Versionen eine Platzhalterzeile ohne Name und Typ) → die Liste der Unterprogramme kommt aus `ALL_PROCEDURES`, die Parameter werden angehängt (`PlSqlArguments.Group`). Position 0 ist der Rückgabewert, `OVERLOAD` ist ohne Überladung NULL, vor 18c gibt es Zeilen mit `DATA_LEVEL > 0` (Komponenten zusammengesetzter Typen). `DEFAULT_VALUE` bleibt leer, nur `DEFAULTED` sagt etwas. Ohne Owner- und Namensfilter ist die View sehr langsam.
- `ALL_SOURCE`: eine Zeile je `LINE` mit Zeilenende im `TEXT`; nur so stimmen Zeile/Spalte aus `ALL_ERRORS`. Wrapped-Code steckt mit vielen Zeilen in einer `TEXT`-Zeile (Kopf „… wrapped“ + `a000000`). `TEXT` ist `VARCHAR2(4000)` → `FetchManyRows`, sonst ~30 Zeilen je Roundtrip. Den Body eines fremden Packages zeigt Oracle nur dem Eigentümer bzw. mit `DEBUG` darauf – mit `EXECUTE` ist er leer (kein Fehler).
- Trigger haben einen eigenen Namensraum (Trigger und Tabelle dürfen gleich heißen) → `PlSqlRef` mit Art. Trigger gelöschter Tabellen bleiben als `BIN$…` in `ALL_OBJECTS`.
- `CREATE` mit Kompilierfehlern wirft in ODP.NET ORA-24344 („success with compilation error“); in SQL*Plus ist es nur eine Warnung. Ein Package mit ~5.500 Konstanten scheitert an PLS-00123 (Diana nodes).

## C#-Modell und ModelHost
- C#-Modell gegen die DB (WP-27) nur mit **konfigurierten** Facetten vergleichen: Die Provider-Vorgaben (`NVARCHAR2(2000)`, `NUMBER(10)` für `int`) beschreiben nicht die Absicht des Projekts und erzeugen Massen an Abweichungen. Für NULL `ColumnNullable` (EFs Spaltensicht) statt `Nullable` der Property (TPH, Owned).
- ModelHost: Roslyn im Projektprozess nur bis 4.11 (ab 4.12 `System.Reflection.Metadata` 9.0, nicht ladbar in .NET 8); `AppContext.BaseDirectory` ist unter `dotnet exec --depsfile` nicht verlässlich (`typeof(Program).Assembly.Location`); Satelliten-Assemblies stehen nicht in der deps.json; ein Interceptor-Exemplar für die ganze Lebensdauer. Roslyn sieht eine Position am Textende als hinter einem unfertigen Lambda.
- Der Host läuft nie direkt aus dem `bin` des Projekts, sondern aus einer Schattenkopie (`BuildOutputShadow`, ADR 0016) – sonst sperrt er die DLLs gegen jeden Build. `BuildOutputWatcher` lädt das Modell nach einem Build neu, die LINQ-Konsole startet danach neben der alten neu.
- Hilfsprozesse (Modell-Export, LINQ-Konsole) nie mit `Process.Kill(entireProcessTree: true)` bzw. `DotNetCli.KillTree` beenden, sondern mit `DotNetCli.Kill`: Der Baum-Kill geht alle Prozesse des Systems durch und wirft für jeden geschützten eine Ausnahme – unter dem VS-Debugger hält jede den ganzen Prozess an (UI bis zu 12 s eingefroren nach jedem Konsolen-Neustart). `KillTree` nur für `dotnet build`. Gefunden mit `UiStallMonitor` (App; Einstellung „Hänger der Oberfläche protokollieren“, standardmäßig aus), der bei Hängern über 1,5 s `dotnet-stack` (globales Tool) aufruft und `stall-*.txt` ins Log schreibt. Schattenkopien, die 7 Tage niemand benutzt hat, entfernt `BuildOutputShadow.DeleteUnusedAsync` beim Start (nur über `SafeDelete`).
