# WP-44 Daten-Diff → Release offen

Branch `wp/44-data-diff` (von `main`), 2026-11-08/09. ADR 0047. Braucht kein anderes Paket; WP-45 baut darauf (Tabellen eines Mitschnitts), WP-43 übernimmt die Lese-Session.

## Planung (aus `docs/roadmap.md`)
Ausgewählt mit dem Nutzer am 2026-10-14 (Ideensammlung). Entscheidung des Nutzers (2026-10-28): direkt Flashback mit `MINUS`, der Snapshot im Speicher wandert in den Backlog (Ausweichweg ohne `FLASHBACK`-Recht oder für längere Zeiträume).

Entscheidungen des Nutzers (2026-11-08, alle Empfehlungen übernommen):
- **Obergrenze:** 10.000 abweichende Zeilen je Tabelle, darüber abschneiden mit Hinweis („Filter enger setzen“).
- **Tabellen bis WP-45:** der aktuelle Tab mit seinem Filter plus eine Liste ganzer Tabellen, von Hand ergänzt.
- **Startpunkt je Workspace**, mit dem Workspace gespeichert (nur die Zahl, keine Werte), übersteht einen Neustart; reicht das Undo nicht mehr, meldet es der Vergleich.
- **Darstellung:** eigener Ergebnis-Tab, Grid mit Status-Spalte wie die Änderungsübersicht, geänderte Zellen markiert mit altem Wert im Tooltip; links die Tabellen mit Zahl der Änderungen.
- **Prod:** erlaubt wie jeder Lesevorgang. **Lese-Session:** WP-44 baut sie allgemein, WP-43 übernimmt sie.
- **Start:** Workspace-Leiste („Vergleich starten“, danach „Vergleichen (seit hh:mm)“ und „Startpunkt verwerfen“), Tab-Menü „Diesen Tab vergleichen“.
- **Ohne PK:** Vergleich über die ROWID, mit Hinweis bei Row Movement.
- Plan bestätigt (2026-11-08); einfachste Variante mit Frage notiert: Genauigkeit von `TIMESTAMP_TO_SCN`, CLOBs über Vorschau und Länge.

## Gebaut
- **Core:**
  - `QueryBuilder.BuildDiff` (`QueryBuilder.Diff.cs`, `DiffQuery`): beide `MINUS` (jetzt gegen `AS OF SCN :d_scnN`) per `UNION ALL` mit `FS_SIDE`, jede Auswahl mit Alias (`C<i>`, CLOB als `C<i>_P`/`C<i>_L`, ROWID als `FS_RID`), sortiert nach Schlüssel und Seite, `FETCH FIRST :d_limit`. Filter-Binds viermal über den gemeinsamen `Builder` (eindeutige Namen). LONG, XMLTYPE, Objekttypen (`Projection.NullMarker`) fehlen in der Abfrage (`Omitted`). Views abgelehnt. `CurrentScnSql`.
  - `IDataAccess.CurrentScnAsync`, `ReadDiffAsync` (`OracleDataAccess`: höchstens `DataDiff.ReadLimit` = 2 × 10.000 + 1 Zeilen, `ReadRow` mit Startspalte und ausgelassenen Spalten; bei ROWID-Schlüssel `ALL_TABLES.ROW_MOVEMENT`). Beide frischen den Snapshot einer gesperrten Session auf.
  - `DataDiff.Pair` (`DiffSideRow`, `DiffRead`, `DiffRow`, `TableDiff`, `DiffStatus`): Paarung über den Schlüssel, Zellvergleich mit `OracleTypeMapper.ValuesEqual`; eine abgeschnittene Lesung lässt ihren letzten Schlüssel weg (dessen zweite Hälfte fehlen kann). `DataDiffErrors.Explain`: ORA-01031 (mit `GRANT FLASHBACK ON …`), ORA-01555/08180/08181 (Undo), ORA-01466 (DDL).
  - `ReadSession` (`ActiveConnection.Reads`): nur `ReadAsync(Func<IDataAccess, …>)` öffentlich, öffnet über `SessionOpening` (ACTION `Reads`), schließt nach 5 min ohne Lesen (`TimeProvider`-Timer) und beim Trennen, verlorene Session wird verworfen (die nächste Lesung öffnet neu), read-only auf schreibgeschützten Profilen, im Keep-alive (`PingIdleSessionsAsync`).
  - `Workspace.DataDiff` (`DataDiffState`: SCN, Zeit, `DataDiffEntry` mit Tabelle und Filter; `WithEntry`, `WithoutEntry`, `Normalized`), `WorkspaceManager.SetDataDiff` (speichert nur bei Änderung, Vergleich über JSON wie `SameTabs`), `TabState.DataDiff` (`DataDiffTabState`).
- **UI:**
  - `WorkspaceLifecycle.DataDiff.cs`: `StartDataDiffAsync` (SCN über die Lese-Session; ohne Tabellen kommt der aktive Tabellen-Tab mit Filter dazu; ein neuer Startpunkt behält die Tabellen), `CompareDataDiffAsync`, `CompareTabAsync`, `AddToDataDiffAsync`, `RemoveFromDataDiff`, `DiscardDataDiff`; je Tabelle Struktur laden und auf der Lese-Session lesen über `RunDbAsync` (Fehler erklärt, Verlust ans Banner). `ShellState.OpenDataDiff`, Wiederherstellen des Tabs, `ShellState.OpenedByName` jetzt `internal` (Tabellen anderer Owner).
  - `DataDiffTab` (Ergebnisse je Tabelle nur im Speicher, gehören zur SCN, laufender Vergleich abbrechbar), `DataDiffRows` (Spalten in Anzeige-Reihenfolge, „nur geänderte“, Startzeit kurz).
  - `DataDiffView`: Werkzeugleiste (seit, Vergleichen/Abbrechen, verglichen um, „+ Tabelle“ mit Liste der Tabellen des Schemas, Neuer Startpunkt, Startpunkt verwerfen), links die Tabellen (Filter oder „ganze Tabelle“, Zähler `DataDiffCounts`, Fehlerpunkt), rechts Filterknöpfe Alle/Neu/Geändert/Gelöscht, „Nur geänderte Spalten“, Hinweise (abgeschnitten, ausgelassen, ROWID, Row Movement) und eine HTML-Tabelle mit `Virtualize` (Status-Spalte klebt, Zahlen rechts, NULL kursiv, gelöschte Zeilen durchgestrichen, geänderte Zellen gelb mit „Am Startpunkt: …“ im `title`). Werte und Namen über `PresentationService`.
  - `WorkspaceBar`: Menüeinträge, Chip mit Startzeit am Workspace (Klick vergleicht). `TabBar`: „Diesen Tab vergleichen“ (gesperrt mit Grund bei Views und ohne Startpunkt), Symbol und Tooltip des Tabs. Icon `DataDiff` (Lucide `history`). Texte `ShellText.DataDiff_*`, `DataText.Diff*`, `ConnectionText.ReadSessionNotConnected`.
- **Tests:** Core `QueryBuilderTests.Diff` (ganzer Text mit Binds, ROWID, View), `DataDiffTests` (Paarung, abgeschnitten, Fehlertexte), `ReadSessionTests` (einmal öffnen, Leerlauf schließt, Verlust, read-only, Trennen), `ReadOnlyTests` (neue Methoden, Oberfläche `ReadSession`); UI `DataDiffTests` (Start mit aktivem Tab, Vergleich auf der Lese-Session, ORA-01031 erklärt, neuer Startpunkt, View/kein Startpunkt); Integration `DataDiffTests` (neu/geändert/gelöscht, Filter hinein/heraus, CLOB über Länge, LONG ausgelassen, ROWID mit Row Movement, hinzugefügte Spalte, `TRUNCATE` erklärt).

## Erkenntnisse
- Oracle 23 verträgt eine seit dem Startpunkt **hinzugefügte Spalte** (sie liest sich damals als NULL; Zeilen mit Wert erscheinen als geändert), ORA-01466 kommt erst bei DDL, die die Tabelle neu schreibt (`TRUNCATE`). Der ADR nennt das.
- `TIMESTAMP_TO_SCN(SYSTIMESTAMP)` reichte im Integrationstest den letzten Commit stets innerhalb des Wartens (Grenze 20 s); kurz vor dem Klick committete Änderungen können mit erscheinen – Frage an den Nutzer unten.

## E2E
2026-11-09, eigener Container (`New-SampleDb.ps1 -Name ferret-e2e-wp44 -Port 1544`), Testinstanz mit `--data-dir` (deutsch/dunkel, zum Schluss englisch/hell), Passwort nicht gespeichert. Änderungen aus einer fremden Session (sqlplus im Container, wie eine App). Klicks mit echter Windows-Eingabe (`Invoke-RealClick.ps1`, Probe vorher bestanden), Tippen per CDP. 12.000 geänderte Zeilen: abgeschnitten bei 10.000, rund 2,5 s mit Klick.
- Gefunden und behoben: Der Status gelöschter Zeilen hieß „löschen“ (Text der Änderungsübersicht, dort ein Befehl) – eigene Texte `DataDiff_Kind*`; Zahlen standen links; eine unveränderte Tabelle zeigte Filterknöpfe mit lauter Nullen; das „+“ für abgeschnitten in der Liste las sich wie „neu“ (jetzt „…“ mit Hinweis).
- Screenshots: `artifacts/wp-44-screenshots/` (nicht im Repo).
- Nicht mit echter Datenbank geprüft: ORA-01031 und Undo-Grenze (nur in Unit- und Integrationstest bzw. per Fake), Prod mit read-only Lese-Session (Unit-Test).

## Offen / Fragen an den Nutzer
- Genauigkeit des Startpunkts: `TIMESTAMP_TO_SCN` liegt bis zu ein paar Sekunden zurück. Soll FerretSharp `V$DATABASE.CURRENT_SCN` nehmen, wo das Recht da ist?
- CLOBs: Vergleich über die ersten 200 Zeichen und die Länge. Reicht das, oder ein Hash (`DBMS_CRYPTO` braucht ein Recht)?
- Das Grid des Diffs ist eine HTML-Tabelle mit `Virtualize`, nicht AG Grid: kein Kopieren von Zellen, kein Formular, keine Spaltenbreiten. Vorschlag (nicht bestätigt): reicht für die erste Stufe; AG Grid mit Zeilen aus dem Speicher wäre ein eigener Weg in `grid.js`.
- Backlog: „Diesen Tab vergleichen“ in der Befehlspalette, Zeitreise im Grid (`AS OF TIMESTAMP`) und Snapshot-Diff stehen schon dort.
