# WP-78 Zeitreise im Grid → Release 3.66.0

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
- **Startpunkt an die Datenbank gebunden (Frage aus WP-44):** `DB_UNIQUE_NAME` und `CON_NAME` mitspeichern, bei Abweichung verwerfen; ebenso bei einer gespeicherten SCN über der aktuellen. (Später: `CON_DBID` dazu nach dem E2E, `DB_NAME` statt `DB_UNIQUE_NAME` nach dem Code-Review – siehe Nachtrag.)
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
- `DB_NAME`, `DB_UNIQUE_NAME`, `CON_NAME` und `CON_DBID` liefert `USERENV` ohne Recht (Oracle 23, im E2E geprüft: `FREE/FREEPDB1/3182349174`), `CON_DBID` deshalb mit in der Kennung; ein neu aufgesetzter Container desselben Images trägt denselben Namen – dort fängt nur die SCN-Prüfung (gespeicherte SCN über der jetzigen).
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

## Nachtrag: Code-Review (2026-11-09)
Review per `/branch-code-review` (eigener Reviewer-Agent, Befunde nachgeprüft). Entscheidung des Nutzers: „Jop, deine Empfehlungen passen.“ – alle 12 Befunde beheben, mit den Empfehlungen zu #8 (`DB_NAME`) und #9 (nur Ansicht „Daten“).
1. S Zurückgesetzte Zeilen landeten im Tab des **angezeigten** Workspaces – nach einem Verbindungswechsel während des Lesens in einer anderen Datenbank. **Behoben:** `ShellState.OpenTableIn(workspaceId, …)`; Test mit Wechsel.
2. M Zurücksetzen lief in ein Schreiben oder einen Commit des Workspaces (Änderungen still verworfen oder ungesehen mitcommittet). **Behoben:** gesperrt, solange `Editing.IsBusy`, und nach dem Lesen erneut geprüft; Test mit Commit während des Lesens.
3. M Werte, die das Grid nie schreibt, kamen verändert zurück (TIMESTAMP(8/9), Zeitzonen-Region, INTERVAL). **Behoben:** `ExactlyWritable` – solche Zeilen „nicht zurückholbar“, gezählt; Test.
4. M Ganze LOBs ohne Grenze, zweimal, nicht abbrechbar. **Behoben:** Zeilen mit LOB über `LobLimits.MaxLoadLength` werden nicht gelesen (gezählt), Lesen abbrechbar im Diff-Tab; Test. Keine Obergrenze der Zeilenzahl (bis 10.000 vom Diff) – mit Abbrechen vertretbar.
5. M Row Movement: „gelöschte“ (verschobene) Zeilen zurückzuholen verdoppelte sie. **Behoben:** gelöschte Zeilen bei Row Movement abgelehnt; Test.
6. K Eine gescheiterte Datenbankprüfung eines Zeitreise-Tabs galt als bestanden. **Behoben:** `PointChecked` erst nach Erfolg (neu geprüft, wenn die Ansicht neu entsteht); Test.
7. K Der tatsächliche Plan eines Zeitreise-Tabs lief auf der Workspace-Session. **Behoben:** `PlanDialog.DataOf`; `ExplainActualAsync` ohne Snapshot-Wiederholung bei `AS OF`.
8. K Data Guard: `DB_UNIQUE_NAME` wechselt beim Switchover, die SCN läuft weiter. **Behoben:** `DB_NAME/CON_NAME/CON_DBID`.
9. K Struktur, Designer, Synonyme, Grants im Zeitreise-Tab zeigten den Stand von jetzt. **Behoben:** nur Ansicht „Daten“ (`TableTab.Mode`, `TabView.Modes`); Test.
10. K Zeitreise-Tabs luden nach jedem Commit neu. **Behoben:** `ReloadTableTabs` überspringt sie.
11. K Eine seit dem Startpunkt hinzugefügte NOT-NULL-Spalte ließ das UPDATE mit ORA-01407 scheitern. **Behoben:** NULL bei NOT NULL im UPDATE ausgelassen; Test.
12. K Gelöschte Zeile zurückgeholt, obwohl eine neue Zeile des Tabs denselben PK hat (ORA-00001). **Behoben:** als „schon geändert“ gezählt; Test.

Zweiter Durchgang (derselbe Reviewer, nur die Fix-Commits): 1–12 passen, die Tests scheitern ohne die Fixes. Freigabe des Nutzers für die neuen Punkte: „Jop, behebe die ruhig alle“.
- 13 M Das Tab-Menü eines Zeitreise-Tabs bot Designer, Ansichten, „Abfrage bauen“, „Synonym löschen“ an, die seit 9 nichts mehr taten; „SQL“ ohne `AS OF`. **Behoben:** gesperrt mit Grund (`GridText.TimeTravel_DataOnly`), auch die LINQ-Konsole; „SQL“ mit der SCN; Test.
- 14 K TIMESTAMP(9)-Werte, die sich nur in den Nanosekunden unterscheiden, lesen sich gleich – die Zeile blieb still liegen. **Behoben:** gezählt als „nicht zurückholbar“; Test.
- 15 K Texte: abgelehnte Typen allgemeiner benannt, „Nimm die Haken … heraus“ statt „Hake … ab“. **Behoben.**
- 16 K Kommentar und Testname zu 6 versprachen eine Prüfung „beim nächsten Zeigen“. **Behoben** (richtiggestellt; die Ansicht bleibt gemountet, neu geprüft wird nach Neuverbinden oder Neustart).
- 17 K Protokoll, ADR und CHANGELOG nannten die neuen Grenzen nicht. **Behoben** (dieser Nachtrag, ADR „Folgen“, CHANGELOG).
- Ohne Test geblieben (kleine direkte Änderungen, Test-App bräuchte Umbau): 7, 10 und der Abbruch selbst.

E2E der Fixes (2026-11-09, dieselbe Testinstanz und derselbe Container, echte Klicks): Zeitreise-Tab zeigt nur „Daten“; sein Tab-Menü sperrt Designer, die anderen Ansichten, LINQ und „Abfrage bauen“ mit Grund, „SQL“ enthält `AS OF SCN`. Startpunkt und Zeitreise-Tab aus Builds vor 8 (Kennung mit `DB_UNIQUE_NAME`) wurden beim ersten Vergleich bzw. Zeigen mit Meldung verworfen („… gehört zur Datenbank FREE/FREEPDB1/…, die Verbindung erreicht jetzt FREEPDB1/FREEPDB1/…“) – in einer PDB liefert `DB_NAME` den PDB-Namen; betrifft nur Dateien von Builds dieses Branches (WP-44 speicherte keine Kennung). Diff ab 10:19 → alle zurücksetzen → Schreiben, Commit → DB wieder auf dem Startstand (auch der CLOB mit 5500 Zeichen). Log ohne neue Warnungen. Nicht in der App nachgestellt: Zurücksetzen während eines Commits und das Abbrechen (Lesen lokal zu schnell; beides im UI-Test bzw. ohne Test).
