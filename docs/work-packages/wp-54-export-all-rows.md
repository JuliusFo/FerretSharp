# WP-54 Alle Zeilen des Filters exportieren → Release 3.42.0

Branch `wp/54-export-all-rows`. ADR 0029. Vom Nutzer bei der Planung von WP-53 als eigenes Paket eingeplant (2026-10-17), begonnen am 2026-10-19. Mit A13 aus dem Gesamt-Review 3.40.0.

## Planung (aus `docs/roadmap.md`)
- Export (CSV und Excel) aller Zeilen, die Filter und Sortierung des Tabs liefern, nicht nur der markierten.
- Die Abfrage des Tabs blockweise auf dem Leseweg der Workspace-Session neu ausführen und direkt in die Datei schreiben; Speichern als Stream über den Host. Fortschritt mit Zeilenzahl und Abbrechen, Obergrenze (Excel: 1 048 576 Zeilen je Blatt).
- A13: Excel und CSV entstanden synchron im UI-Thread **vor** dem Speichern-Dialog, ohne Abbruch, ganz im Speicher. Neu: erst der Dialog, dann der Bau per `Task.Run` mit Fortschritt und Abbruch.

## Entscheidungen des Nutzers (2026-10-19, alle Empfehlungen übernommen)
- **Session:** die Session des Workspaces. Sie sieht die eigenen geschriebenen Änderungen; Grid, Filter und Commit dieses Workspaces warten so lange.
- **LOBs:** nur die Vorschau mit Warnung, wie bisher.
- **Anzeigespalten (WP-31):** im Excel-Export je Block nachschlagen (eine kleine Abfrage je Anzeigespalte und Block); sonst wären die Namensspalten bei „alle Zeilen“ fast leer.
- **Wo:** Tabellen-Tabs und Ergebnisse des SQL-Editors (die Abfrage läuft noch einmal); die LINQ-Konsole kommt in den Backlog.
- **Obergrenze für CSV:** keine; Fortschritt und Abbrechen reichen.

## Entscheidungen in der Umsetzung (Begründung im ADR)
- **Ein Cursor statt Seiten:** `QueryBuilder.BuildSelectAll` ist die Abfrage des Grids ohne `OFFSET/FETCH` (Test: dieselbe SQL bis auf die Paging-Zeile). `IDataAccess.ReadAllAsync` reicht Blöcke à 500 an einen `RowBlockHandler`, der mit `false` aufhören kann (Excel-Blatt voll).
- **Kein neuer Snapshot, Wiederholung nur vor dem ersten Block:** Gelesen wird im Stand der Session. Bei gesperrten Workspaces ist das der Snapshot des Grids; ein neuer würde die nächsten Seiten des Grids in einen anderen Stand verschieben. Nach dem ersten Block wiederholt `OracleSession.ExecuteStreamingReaderAsync` bei ORA-01555 nicht, sondern meldet: Ein Teil der Datei ist dann schon geschrieben (Nachtrag aus dem Review unten).
- **Nachschlagen bei offenem Cursor:** Das Gate ist belegt, deshalb laufen die Abfragen über die `NestedReader` des Aufrufs auf derselben Verbindung (`IRowLookup`, `DisplayNames.FillAsync`). Für den Export eine eigene Kopie der Namen (`DisplayNames.Fresh`) mit einem eigenen Formatierer: Der des Grids ist nur für den UI-Thread gedacht. Eine Abfrage, die scheitert, schaltet die Spalte für diesen Export ab, mit Warnung „Namen aus KUNDEN.NAME fehlen: …“.
- **SQL-Ergebnis:** `ReadSqlAllAsync` vergleicht die Spaltennamen mit denen des angezeigten Ergebnisses und lehnt ab, wenn sie inzwischen anders sind (`DataText.ExportResultChanged`).
- **Fortschritt in der Statusleiste statt im Tab** (Plan sagte „im Tab“): Der Export läuft weiter, wenn der Nutzer den Tab oder die Verbindung wechselt, und der Export der markierten Zeilen aus SQL-Editor und LINQ-Konsole nutzt denselben Weg. `FileExports` (an `ShellState`) und `ExportStatus` zeigen Dateiname, Zeilen und „Abbrechen“.
- **„x von n“ nur, wenn n bekannt ist:** gezählte Zeilen des Tabs, alle Blöcke geladen oder die markierten Zeilen. Vor dem Export wird nicht eigens gezählt (eine zusätzliche, über VPN teure Abfrage); sonst steht nur die Zahl der geschriebenen Zeilen da.
- **Die Datei entsteht neben dem Ziel** als temporäre Datei (seit dem Review mit eigenem Namen) und wird am Ende umbenannt (`AtomicFile`, `AtomicJsonFile` baut jetzt darauf). Ein Abbruch oder Fehler hinterlässt nichts, und eine Datei, die der Nutzer im Dialog ersetzen wollte, bleibt bis zum Ende unberührt.
- **Excel-Breiten aus den ersten 1000 Zeilen** (`XlsxExport.WidthSample`), danach streamt der Writer. Bisher bestimmten alle markierten Zeilen die Breiten; bei mehr als 1000 markierten Zeilen kann eine Spalte jetzt schmaler ausfallen.
- **Autofilter ohne bekannte Zeilenzahl nur über der Kopfzeile:** Excel erweitert ihn beim Öffnen auf die Daten darunter. Geprüft mit Excel 365 bei 5 und bei 150 001 Zeilen. Bei bekannter Zahl (markierte Zeilen) deckt er sie wie bisher genau ab.
- **Menü:** eigene Gruppe „Alle Zeilen des Filters“ bzw. „… des Ergebnisses“ unter der Exportgruppe der markierten Zeilen, mit „Als CSV speichern …“, „Als Excel-Datei speichern …“ und der Rohwert-Variante, wenn es eine Präsentation gibt. Die Zeilenzahl steht in der Überschrift, wenn sie bekannt ist. Der Tooltip sagt, dass ausstehende Änderungen fehlen und der Workspace wartet.
- **Ausstehende Änderungen:** „Alle Zeilen“ liest aus der Datenbank, also geschriebene Änderungen ja, ausstehende und neue Zeilen nein. Der Export der markierten Zeilen nimmt weiter die angezeigten Werte (A21).

## Gebaut
- **Core:** `QueryBuilder.BuildSelectAll`; `IRowLookup` (aus `IDataAccess` herausgelöst), `RowBlockHandler`, `IDataAccess.ReadAllAsync`/`ReadSqlAllAsync`/`BlockSize`; `OracleSession.ExecuteStreamingReaderAsync` mit `NestedReader`; `OracleDataAccess` (Blöcke mit `FetchManyRows`, `NestedLookup`, gemeinsame Zeilenleser für SQL-Ergebnisse). `RowExport` (`ExportSummary`, `IExportWriter`, `RowReader`, `FromRows`, `WriteAsync` mit Namen je Block und Fortschritt), `DelimitedExport.CreateWriter`, `XlsxExport.CreateWriterAsync` (`BuildAsync` darauf aufgebaut), `DisplayNames.Fresh`/`Copy`/`FillAsync`, `AtomicFile`. Texte `ExportResultChanged`, `ExportNamesMissing`.
- **Host/UI:** `IFileSaveService.AskPathAsync`; `ExportActions.SaveRowsAsync`/`SaveAllRowsAsync` (Dialog, Hintergrund, Hinweise, Fehler über `DbCalls`), `AllRowsExport`, `FileExports`, `ExportStatus` in der Statusleiste. `ExportMenuItems` mit der Gruppe für alle Zeilen; `GridContextMenu` (Abfrage und Namen des Tabs; die Excel-Spalten der markierten Zeilen nehmen eine Kopie der Namen des Grids) und `ResultMenu`/`SqlResultGrid` (`ExportsAllRows` nur im SQL-Editor). Texte `GridMenu_AllRowsGroup`, `GridMenu_AllResultRowsGroup`, `GridMenu_AllRowsTitle`, `Export_Cancelled`, `Status_ExportTitle`, `Status_ExportRowsOf`.
- **Tests:** Core `RowExportTests` (CSV wie `Build` mit BOM, Excel gestreamt mit Kopfzeilen-Filter und Breiten aus der Stichprobe, volles Blatt stoppt das Lesen, Blöcke/Fortschritt/Dispose, Abbruch zwischen Blöcken, Namen je Block und gescheiterte Abfrage, `Copy`/`Fresh`), `AtomicFileTests`, `QueryBuilderTests` (ohne Paging), `ReadOnlyTests` (neue Methoden, Vertrag mit geerbten Interfaces). Integration `OracleDataAccessTests`: dieselben Zeilen wie über Seiten, Abbruch durch den Verbraucher, Nachschlagen bei offenem Cursor samt ORA-00942 mit weiterlaufendem Lesen, SQL-Ergebnis ganz und abgelehnt bei anderen Spalten, Lesesperre des neuen Wegs. UI `ExportActionsTests` (Hintergrund und Statusleiste, Hinweise, abgebrochener Dialog, Abbrechen behält die alte Datei, DB-Fehler, Ablehnung, Fortschritt).

## Nachtrag: Code-Review des PR (2026-10-19)
Zehn Befunde, alle umgesetzt:
- **Zieldatei vorher prüfen:** `AtomicFile.WriteExportAsync` öffnet ein vorhandenes Ziel zum Schreiben, bevor gelesen wird. Eine Datei, die in Excel offen ist, scheiterte sonst erst beim Umbenennen nach dem ganzen Export. Die temporäre Datei hat einen eigenen Namen (`<Ziel>.<guid>.tmp`), damit eine Datei des Nutzers namens `<Ziel>.tmp` nie ersetzt oder gelöscht wird. Die JSON-Stores bleiben bei `file.tmp`.
- **Snapshot-Wiederholung vor dem ersten Block:** `ExecuteStreamingReaderAsync` wiederholt ORA-01466/ORA-08176/ORA-01555 im neuen Snapshot wie das Grid, solange noch kein Block weitergegeben wurde (`NestedReader.HandingOn`), Integrationstest wie für das Grid.
- **Fehler beim Nachschlagen ins Log:** `FillAsync` liefert `NameLookupFailure` (Warnung und Fehler); `ExportSummary.Failures` gehen über `QueryErrorLog` ins Log. Eine verlorene Verbindung ist kein Fehler einer Anzeigespalte: Sie beendet den Export und meldet sich wie jeder Verbindungsverlust.
- **Abbrechen nach dem Ende** (Klick, bevor die Statusleiste neu gezeichnet hat) tut nichts mehr, statt auf eine entsorgte Quelle zu treffen.
- **Aufbau von „alle Zeilen“ als State-Klasse** `AllRowsExports` mit Tests (`AllRowsExportsTests`): angewandte Filter und Sortierung beim Öffnen des Menüs, Zeilenzahl, eigene Namen. Die Excel-Spalten und die Kopie der Namen entstehen erst beim Klick (`AllRowsExport.Excel`, `ExportMenuItems.XlsxColumnsToSave`), nicht bei jedem Öffnen des Menüs.
- Aufgeräumt: `XlsxExport.BuildAsync` und `ExportFile` entfernt (nur noch Tests nutzten sie), `SyncProgress<T>` des Core in den Tests, der Zweig für abgeschaltete Verknüpfungen in `FillAsync` vereinfacht.

## E2E (2026-10-19)
Eigene Instanz (`--data-dir`, eigener WebView2-Ordner, Build im Scratchpad), eigener Container `ferret-e2e-wp54` auf Port 1534 (danach entfernt), Passwort nicht gespeichert, CDP-Eingabe.
- **Zellmenü (KUNDEN, 150 000 Zeilen):** Gruppe „Alle Zeilen des Filters“ mit CSV und Excel unter der Exportgruppe, das Menü bleibt im Fenster. „Als CSV speichern …“ öffnet den Dialog mit `KUNDEN.csv`, Typ „CSV (Semikolon)“ (nur ausgelesen, per `WM_CLOSE` geschlossen). Danach weder Hinweis noch Eintrag in der Statusleiste.
- **SQL-Editor:** Gruppe „Alle Zeilen des Ergebnisses“; „Als Excel-Datei speichern …“ bietet `KUNDEN.xlsx` als „Excel-Arbeitsmappe“ an.
- **Weg bis zur Datei mit dem Core** gegen denselben Container (Konsolenprojekt im Scratchpad: `ReadAllAsync` → `RowExport` → `AtomicFile`): KUNDEN mit 150 000 Zeilen als CSV in 1,2 s (9 MB) und als Excel in 1,1 s (6 MB); AUFTRAG mit 50 000 Zeilen und nachgeschlagenen Kundennamen in 1,3 s, Warnung für die CLOB-Vorschau. In Excel 365: Autofilter nach dem Öffnen `A1:J150001`, Filter auf die letzte ID trifft genau eine Zeile, alle 50 000 Namen gefüllt.
- Log der Testinstanz ohne `[ERR]`/`[WRN]`.
- **Nicht geprüft:** Speichern über den Dialog bis zur Datei in der App (native Dialoge werden nicht bedient), damit auch nicht die Anzeige in der Statusleiste mit echtem Export und „Abbrechen“ dort (Logik in `ExportActionsTests`, Rendern ungesehen); der Export über das VPN mit einer langsamen Verbindung; echte Windows-Eingabe (`SendInput`; die Einträge sind gewöhnliche `@onclick`-Knöpfe).

## Offen
- LINQ-Konsole: „alle Zeilen“ (Backlog). Ihr Ergebnis ist ebenfalls eine SQL-Abfrage (`QuerySpec`, die das Grid schon seitenweise erneut ausführt) – technisch wäre es derselbe Weg wie im SQL-Editor.
- LOBs vollständig exportieren (Backlog, unverändert).
