# WP-78 Zeitreise im Grid → Release offen

Branch `wp/78-time-travel` (von `main`), 2026-11-09. ADR 0048. Braucht WP-44 (Daten-Diff, Lese-Session, ADR 0047).

## Planung (aus `docs/roadmap.md`)
Eingeplant mit dem Nutzer am 2026-11-09, aus dem Backlog (Ideensammlung 2026-10-28). Entscheidungen des Nutzers (2026-11-09, alle Vorschläge übernommen):
- **Zeitreise als reine Ansicht:** Ein Tab zeigt nur lesend den Stand zu einer gewählten Zeit. Kein neuer Leseweg: Die Abfrage bleibt ein SELECT mit `AS OF SCN` (wie in WP-44) und passiert die Lesesperre. Banner „Stand vom …, nur lesend“.
- **Grid-Funktionen:** alles Lesende bleibt (Filter, Sortierung, Kopieren, Export, Formular nur lesend), alles Schreibende ist gesperrt. Der FK-Sprung öffnet das Ziel zum selben Zeitpunkt.
- **Zeitauswahl:** absolut (Datum und Uhrzeit) plus Schnellwahl „vor 5 / 15 / 60 min“. Die Zeit wird beim Öffnen einmal zur SCN, danach blättert der Tab mit `AS OF SCN` – alle Seiten auf demselben Stand. ORA-08180 meldet schon die Auswahl, ORA-01555 die erste Seite mit der Erklärung aus `DataDiffErrors`; keine eigene Probeabfrage.
- **Session:** die Lese-Session aus WP-44.
- **Daten-Diff ab gewählter Zeit:** „Vergleich starten“ auch mit einer Zeit in der Vergangenheit, über dieselbe Zeitauswahl.
- **Zurückholen und Zurücksetzen im Diff-Tab**, nicht im Zeitreise-Grid: angehakte gelöschte Zeilen werden *ausstehende* INSERTs, geänderte ausstehende UPDATEs auf den alten Stand – danach der normale Weg (Flush, Undo, Commit), kein neuer Schreibweg; auf schreibgeschützten Profilen gesperrt wie jede Änderung. `GENERATED ALWAYS AS IDENTITY` abgelehnt mit Erklärung, virtuelle Spalten ausgelassen.
- **Genauere SCN (Frage aus WP-44):** einmal je Verbindung `V$DATABASE.CURRENT_SCN`, ohne Recht still `TIMESTAMP_TO_SCN(SYSTIMESTAMP)`.
- **Startpunkt an die Datenbank gebunden (Frage aus WP-44):** `DB_UNIQUE_NAME` und `CON_NAME` mitspeichern, bei Abweichung verwerfen; ebenso bei einer gespeicherten SCN über der aktuellen.
- **Kein Diagnostics/Tuning Pack** (ADR 0045).

Entscheidungen zum Plan (2026-11-09, „Ja, so umsetzen“ – alle vier Empfehlungen übernommen):
1. Zurücksetzen löscht auch hinzugekommene Zeilen (ausstehendes DELETE) – „Tabelle auf den Stand von …“ vollständig.
2. Anzeigespalten lesen zum selben Zeitpunkt wie der Tab (statt „aktueller Stand mit Hinweis“): `AsOf` hängt am Datenzugriff.
3. Die Zeit geht als Abstand zu „jetzt“ der Datenbank in die Abfrage (`SYSTIMESTAMP - NUMTODSINTERVAL`), nicht als Bind mit Zeitzone.
4. Der Zeitdialog zeigt, wie weit das Undo etwa reicht (`V$UNDOSTAT.TUNED_UNDORETENTION`); ohne Recht entfällt der Hinweis.

## Gebaut
- **Core:**
  - `QueryBuilder.AsOf.cs`: `From(table, asOf, …)` für alle Abfragen des Lesewegs (`BuildSelect`, `BuildSelectAll`, `BuildCount`, `BuildColumnValues`, `BuildSelectLob`, `BuildRowsByKeys`, `BuildLookup`; `BuildSelectStar` schreibt die SCN als Zahl), `ScnBackSql`, `ExactScnSql`, `UndoReachSql`; `CurrentScnSql` liefert die Datenbank mit.
  - `IDataAccess.AsOf`, `CurrentPointAsync(exact)`, `PointBackAsync`, `UndoReachAsync` (`DatabasePoint`); `OracleDataAccess(session, asOf)` – Abfragen mit `AS OF` nie in einem neuen Snapshot wiederholt (`ExecuteStreamingReaderAsync` hat den Schalter jetzt auch), `RowPage.DataAsOf` dann leer.
  - `ReadSession.AsOf` (`ReadSessionData`: jeder Aufruf ein eigenes `ReadAsync`), `ReadSession.CurrentPointAsync` (genaue SCN, fehlendes Recht je Verbindung gemerkt).
  - `ChangeTracker.Restore` (`RestoreRow`, `RestoreResult`, `RestoreUnavailable`), `DataRestore.ReadAsync` (Werte ganz: damals `AS OF`, jetzt auf der Workspace-Session; seither geänderte Zeilen gezählt), `TimePoints` (`Back`, `Mismatch`), `DataDiffErrors` mit Texten für die Zeitreise und `ExplainPoint` (ORA-08180).
  - `DataDiffState.Database`, `TabState.TimeTravel` (`TimeTravelState`), `ShortcutAction.TimeTravel` (ohne Standardtaste).
- **UI:**
  - `TabData` (Lesen eines Tabs: Workspace-Session oder Lese-Session `AS OF`), eingesetzt in `FerretGrid` (Seiten, markierte Zeilen, Anzeigenamen), `TabFooter`, `LobEditorDialog`, `ColumnMenu`, `GridContextMenu` (Export, FK-Zähler), `RowFormPanel`; `FkCounts.For(…, asOf)`.
  - `TableTab.TimeTravel` (nur lesend: `CanEdit`, keine Audit-Markierung, Datensatz-Diagramm und „In andere Verbindung kopieren“ gesperrt mit Grund), Banner mit „Aktuellen Stand öffnen“, Fußzeile „Stand vom …“, Tab mit Icon `TimeTravel` (Lucide `rewind`) und Uhrzeit; `ShellState.OpenTimeTravel`, `OpenFiltered` behält den Zeitpunkt (`now: true` für den jetzigen), `OpenTable` nimmt nie einen Zeitreise-Tab; `FilterStatement` und Plan mit `AS OF`.
  - `TimePointDialog` (`TimeChoice`: Eingabe lokal auf die Sekunde, Schnellwahl, Undo-Hinweis), angefordert über `ShellState.AskTimeTravel`/`AskDiffStart`; Einstiege: Tab-Menü „Zeitreise …“, Befehlspalette, Workspace-Menü und Diff-Tab „Vergleich ab Zeitpunkt …“/„Startpunkt ab Zeitpunkt …“.
  - `WorkspaceLifecycle.TimeTravel.cs`: `TimeTravelAsync`, `StartDataDiffAsync(…, at)`, Prüfung der Datenbank vor jedem Vergleich (`StillBelongsAsync`) und für Zeitreise-Tabs aus der Datei (`CheckTimeTravelAsync`), `OpenAtStart` („Stand zum Startpunkt“), `RestoreAsync`/`RestoreUnavailable`.
  - `DataDiffView`: Haken je Zeile und im Kopf (alle gezeigten), „Auf Stand … zurücksetzen (n)“, „Stand zum Startpunkt“.
- **Tests:** Core `QueryBuilderTests.AsOf` (jede Abfrage des Lesewegs, SQL-Text, Zeitpunkt-Abfragen), `DataRestoreTests` (INSERT/UPDATE/DELETE in einem Undo-Schritt, Identity, LONG, schon geänderte Zeilen, Lesen damals/jetzt, `TimePoints`), `ReadSessionTests` (genaue SCN einmal je Verbindung, `AsOf` auf der Lese-Session), `ReadOnlyTests` (Oberflächen); UI `TimeTravelTests` (Tab und Lesen `AS OF` auf der Lese-Session, FK-Sprung und Explorer, Zeit in der Zukunft/ORA-08180, Diff ab Zeit, andere Datenbank, Tab aus der Datei, Zurücksetzen, gesperrter Workspace, `TimeChoice`); Integration `DataDiffTests.TimeTravel` (Leseweg `AS OF` mit Filter und ganzem CLOB, auch in read-only Transaktion; Zeit → SCN derselben Datenbank, `V$DATABASE` ohne Recht, ORA-08180 erklärt; Zurücksetzen bis zum Commit, CLOB über 4000 Zeichen).

## Erkenntnisse
- `AS OF SCN` funktioniert in der read-only Transaktion einer gesperrten Session (Prod-Lese-Session); geprüft im Integrationstest.
- `DB_UNIQUE_NAME`, `CON_NAME` und `CON_DBID` liefert `USERENV` ohne Recht (Oracle 23, im E2E geprüft: `FREE/FREEPDB1/3182349174`), `CON_DBID` deshalb mit in der Kennung; ein neu aufgesetzter Container desselben Images trägt denselben Namen – dort fängt nur die SCN-Prüfung (gespeicherte SCN über der jetzigen).
- Ein INSERT lässt NULL weg wie eine neue Zeile im Grid: eine DEFAULT-Spalte, die damals NULL war, bekommt den Default (ADR 0048, Folgen).

## E2E
2026-11-09, eigener Container (`New-SampleDb.ps1 -Name ferret-e2e-wp78 -Port 1578`), Testinstanz mit `--data-dir` (deutsch/dunkel), Verbindungen „E2E WP78“ (Test) und „E2E WP78 Prod“ (read-only) als `connections.json`, Passwort nicht gespeichert. Tabelle `E2E_TT` (20 Zeilen, ein CLOB mit 5500 Zeichen), Änderungen aus einer fremden Session (sqlplus im Container). Klicks mit echter Windows-Eingabe (`Invoke-RealClick.ps1`, Probe vorher bestanden), Tippen und Feldwerte per CDP. Die Uhr des Servers lag eine Stunde hinter der lokalen (UTC gegen MEZ) – der Zeitreise-Tab zeigte trotzdem genau den Stand vor den Änderungen (Abstand zu `SYSTIMESTAMP`).
- Geprüft: Tab-Menü „Zeitreise …“ → Dialog (Schnellwahl, Zeit, ohne `V$UNDOSTAT`-Recht kein Hinweis) → Tab mit Icon, Uhrzeit, Banner, Fußzeile; Grid ohne „+ Zeile“; Zellmenü: Datensatz-Diagramm und „In andere Verbindung kopieren“ gesperrt mit Grund; LOB-Dialog zeigt den ganzen alten CLOB, nur lesend; „SQL“ zeigt `AS OF SCN :p_asof`; „Aktuellen Stand öffnen“. Workspace-Menü „Vergleich ab Zeitpunkt …“ → Diff (1 geändert, 2 gelöscht, 1 neu) → alle anhaken → „Auf Stand 08:53 zurücksetzen (4)“ → 4 ausstehend im Tab (neue Zeilen oben) → Schreiben, Commit → in der DB wieder der Startstand (CLOB mit 5500 Zeichen), erneuter Vergleich „Unverändert“. Prod: Diff ab Zeitpunkt geht, „Zurücksetzen“ gesperrt („Der Workspace ist schreibgeschützt …“). Nach dem Neustart: Zeitreise-Tabs und Startpunkt wieder da.
- Gefunden und behoben: Ein Zeitpunkt vor dem Anlegen der Tabelle zeigte im Grid die rohe ORA-01466 statt der Erklärung (`DataDiffErrors.Explain(…, timeTravel: true)` jetzt auch im Grid).
- `CON_DBID` liefert `USERENV` ohne Recht → in die Kennung der Datenbank aufgenommen.
- Log: nur die zwei erwarteten Warnungen der zu frühen Zeitpunkte (ORA-01466, ORA-08181), kein `[ERR]`.

## Offen / Fragen an den Nutzer
- „Link auf diese Ansicht“ aus einem Zeitreise-Tab nimmt den Zeitpunkt nicht mit – der Link öffnet den jetzigen Stand. Den Zeitpunkt in den Link aufnehmen (SCN gilt nur für dieselbe Datenbank), den Eintrag sperren, oder so lassen?
- Die Zahl in der Fußzeile eines Zeitreise-Tabs („Zeilen zählen“) zeigt einen Fehler roh (nicht über `DataDiffErrors`) – selten, da die erste Seite schon scheitert.
