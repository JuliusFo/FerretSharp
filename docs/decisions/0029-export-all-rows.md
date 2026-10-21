# ADR 0029: Alle Zeilen exportieren – ein Cursor in der Workspace-Session

- Status: akzeptiert
- Datum: 2026-10-19
- Paket: WP-54

## Kontext

Der Export (CSV, Excel) nahm bisher nur die markierten Zeilen, also höchstens die Blöcke, die das Grid geladen hat (à 500, höchstens 40 im Speicher). „Alle Zeilen des Filters“ heißt: die Abfrage des Tabs noch einmal ausführen und alle Zeilen in die Datei schreiben, auf großen Tabellen über das VPN auch Hunderttausende. Dabei müssen drei Fragen entschieden werden: wie gelesen wird (Seiten oder ein Cursor), in welcher Session, und wie die Namen der Anzeigespalten (WP-31) in die Excel-Datei kommen, die das Grid nur für die geladenen Zeilen nachschlägt.

Dazu kam A13 aus dem Gesamt-Review 3.40.0: Excel und CSV entstanden synchron im UI-Thread **vor** dem Speichern-Dialog, ohne Abbruch, ganz im Speicher.

## Entscheidung

**Ein Cursor statt Seiten.** `IDataAccess.ReadAllAsync` führt die Abfrage des Grids (`QueryBuilder.BuildSelectAll`: dieselbe Select-Liste, dieselben Filter, dieselbe deterministische Sortierung, nur ohne `OFFSET/FETCH`) einmal aus und reicht die Zeilen in Blöcken à 500 weiter (`RowBlockHandler`), mit großen Fetch-Portionen (`OracleReading.FetchManyRows`). Seiten über `OFFSET` würden mit jeder Seite langsamer und lesen bei ungesperrten Workspaces nicht denselben Stand. Für den SQL-Editor gibt es `ReadSqlAllAsync`: Die Abfrage läuft noch einmal, und sie wird abgelehnt, wenn sie inzwischen andere Spalten liefert.

**In der Session des Workspaces** (Entscheidung des Nutzers, 2026-10-19). Der Export sieht damit, was der Workspace sieht: geschriebene, nicht committete Änderungen, und bei gesperrten Workspaces den Snapshot des Grids. Ein neuer Snapshot wird nicht gestartet, sonst würden die nächsten Seiten des Grids aus einem anderen Stand kommen. Der Preis ist, dass Grid, Filter und Commit dieses Workspaces warten, bis der Export fertig oder abgebrochen ist. Die Statusleiste zeigt das. Eine eigene Lese-Session hätte den Workspace frei gelassen, aber die offenen Änderungen nicht gesehen und über das VPN eine weitere Anmeldung gekostet.

**Wiederholung im neuen Snapshot nur vor dem ersten Block.** `ExecuteReaderAsync` wiederholt eine Abfrage, deren Snapshot unbrauchbar wurde (ORA-01555, ORA-01466, ORA-08176), in einem neuen. Der neue interne Weg `OracleSession.ExecuteStreamingReaderAsync` tut das auch, aber nur, solange noch kein Block weitergegeben wurde (`NestedReader.HandingOn`). Danach steht ein Teil der Zeilen schon in der Datei, und der Fehler wird gemeldet. Er nimmt nur, was die Lesesperre durchlässt (`IsReadOnlyStatement`), wie `ExecuteReaderAsync` selbst. Die Lesesperre bleibt unverändert.

**Nachschlagen bei offenem Cursor.** Die Namen neben FK-Werten schlägt der Export je Block nach, bevor der Block geschrieben wird (`DisplayNames.FillAsync`). Die Session ist dabei schon belegt, weil der Export das Gate hält. Darum bekommt der Leser eine `NestedReader`, die weitere Abfragen auf derselben Verbindung ausführt, solange der Cursor offen ist. Oracle hält mehrere Cursor einer Session, und ODP.NET erlaubt das. Der Integrationstest prüft es, auch eine scheiternde Nachschlage-Abfrage, nach der das Lesen weiterläuft. Scheitert das Nachschlagen für eine Anzeigespalte, bleibt ihre Spalte leer, der Export warnt, und der Fehler steht im Log. Eine verlorene Verbindung beendet den Export.

**Erst der Dialog, dann im Hintergrund** (A13). `ExportActions` fragt nach dem Pfad (`IFileSaveService.AskPathAsync`) und schreibt dann per `Task.Run` in eine temporäre Datei mit eigenem Namen neben dem Ziel. Diese wird erst am Ende umbenannt (`AtomicFile.WriteExportAsync`). Das Ziel wird vorher zum Schreiben geöffnet: Eine Datei, die in Excel offen ist, scheitert so sofort und nicht erst nach dem ganzen Lesen. Ein abgebrochener oder gescheiterter Export hinterlässt so keine halbe Datei, und eine vorhandene Datei bleibt, wie sie war. Laufende Exporte stehen mit Zeilenzahl und „Abbrechen“ in der Statusleiste (`FileExports`), egal welcher Tab gerade zu sehen ist. Der Export der markierten Zeilen läuft über denselben Weg.

**Excel ohne bekannte Zeilenzahl.** SpreadCheetah braucht Spaltenbreiten und Autofilter beim Start des Blatts. Die Breiten kommen aus den ersten 1000 Zeilen (`XlsxExport.WidthSample`). Der Autofilter liegt nur über der Kopfzeile, wenn die Zahl der Zeilen vorher nicht feststeht: Excel erweitert ihn beim Öffnen auf die Zeilen darunter (mit Excel 365 geprüft, auch bei einer großen Datei). Eine Zeile, die in jeder Spalte leer ist, würde ihn beenden. Nach 1 048 575 Datenzeilen ist das Blatt voll, das Lesen hört auf, und der Export warnt.

## Konsequenzen

- `IDataAccess` hat zwei Lesemethoden mehr (`ReadAllAsync`, `ReadSqlAllAsync`). `LookupAsync` liegt jetzt in `IRowLookup`, das auch der Leser während des Exports anbietet. `ReadOnlyTests` kennen die neuen Methoden.
- Während eines Exports warten die Seiten des Grids ohne Zeitgrenze. Nachschlagen und FK-Zählungen im selben Workspace haben eine und können während eines langen Exports hineinlaufen. Nach drei Zeitüberschreitungen in Folge schaltet ein Grid eine Anzeigespalte für den Tab ab (WP-31).
- LOBs exportiert der Export weiterhin nur als Vorschau, mit Warnung (Entscheidung des Nutzers). Ausstehende, noch nicht geschriebene Änderungen und neue Zeilen sind nicht dabei; der Export der markierten Zeilen nimmt sie weiter mit.
- Für Ergebnisse der LINQ-Konsole gibt es „alle Zeilen“ noch nicht (Backlog).
