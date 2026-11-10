# WP-81 Ergebnisse im SQL-Editor behalten → Release 3.68.0

Branch `wp/81-keep-results` (von `main`), 2026-11-10. Kein ADR (weder Stack noch Lese-/Schreibweg ändern sich).

## Planung (aus `docs/roadmap.md`)
Branch `wp/81-keep-results`. Idee des Nutzers (2026-11-10, wie das Anpinnen in SQL Developer): Ergebnisse eines SQL-Tabs bleiben stehen, wenn danach ein anderes Statement oder dasselbe noch einmal läuft.
- **Entschieden (Nutzer, 2026-11-10):** Die Aktion heißt „Ergebnis behalten“ / „Keep result“, nicht „Anheften“ (das sind schon Tabs und Spalten). Ein behaltenes Ergebnis bleibt in der Ergebnisleiste des Tabs stehen, neue Läufe kommen daneben, nur nicht behaltene werden ersetzt. Beschriftung des Reiters: Tabelle bzw. erstes Wort des Statements und die Uhrzeit, umbenennbar; der Tooltip zeigt den SQL-Text.
- Heute: `SqlView.ExecuteAsync` leert `SqlTab.Runs` vor jedem Lauf, die Chip-Leiste erscheint nur bei mehreren Statements eines Skripts. Jedes `SqlRun` kennt schon Statement, `QuerySpec` (mit Bind-Werten), Zeit und erste Seite.
- **Entschieden (Nutzer, 2026-11-10, Vorschläge bestätigt):**
  - **Zeilen werden eingefroren.** Das Grid liest Seiten nach der ersten mit OFFSET/FETCH neu (`SqlResultGrid`, `RowBlocks`), ein späteres Scrollen zeigte sonst Zeilen vom aktuellen Stand. Beim Behalten wandern nur die schon geladenen Zeilen (zusammenhängend ab Zeile 0) in den Lauf, das Grid liest sie danach nur noch aus dem Speicher (wie die Sortierung im Speicher). „Alle Zeilen laden“ ist ein eigener Schritt. Sortieren nur im Speicher über die behaltenen Zeilen.
  - Obergrenzen: 10 behaltene Ergebnisse je Tab, 10 000 Zeilen je Ergebnis.
  - „Alle Zeilen“ (Export, Werte der Spalte kopieren) nimmt auf einem behaltenen Ergebnis nur die behaltenen Zeilen und heißt im Menü auch so – nie die Abfrage noch einmal.
  - Über dem Ergebnis eine einklappbare Kopfzeile mit dem SQL-Text (`SqlCode`), Bind-Werten (auch auf Prod, im Editor stehen sie ohnehin), Uhrzeit, Zeilenzahl und „In den Editor übernehmen“ – auch für nicht behaltene Ergebnisse. „In den Editor übernehmen“ markiert das Statement, wenn es noch genau so im Editor steht, sonst fügt es am Cursor ein.
  - Hinweis in der Kopfzeile, wenn das Ergebnis in einer offenen Transaktion gelesen wurde (nach einem Rollback kann es Zeilen zeigen, die es nie gab).
  - Icon: Lucide `camera`.
  - LINQ-Konsole später (Backlog).
- Mit dem Plan freigegeben (2026-11-10): Logik als reine State-Klasse (z. B. `ResultShelf`: behalten, loslassen, schließen, umbenennen, neuen Lauf einsortieren, Obergrenze) mit Tests in `UI.Tests`; die Leiste zeigt sich, sobald es mehr als ein Ergebnis gibt. Nur Ergebnisse von Abfragen lassen sich behalten (kein DML/DDL, keine Fehler).
- **Entschieden (Nutzer, 2026-11-10, beim Review bestätigt):** nur im Speicher wie `Runs` (Zeilenwerte nicht in der Workspace-Datei); nach einem Neustart sind behaltene Ergebnisse weg.
- Neue `ShortcutAction` ohne Standardtaste, Texte Englisch/Deutsch, README, CHANGELOG, Protokoll.

Entscheidungen zum Plan (2026-11-10, „Ja, loslegen“): Ein elftes behaltenes Ergebnis wird mit Meldung abgelehnt (nicht das älteste verworfen); Tab schließen mit behaltenen Ergebnissen fragt nicht; „In den Editor übernehmen“ übernimmt nur den Text, die Bind-Werte des Tabs bleiben; nur Abfragen ohne Fehler lassen sich behalten.

## Gebaut
- **Core:** `SqlScript.Find` (`SqlSplitter`): das Statement mit genau diesem Text, zuerst an der alten Stelle, sonst das erste.
- **UI State:**
  - `ResultShelf` (State/Tabs, `SqlTab.Results` statt `Runs`/`SelectedRun`): behaltene Ergebnisse vor denen des letzten Laufs; `StartRun`, `Add`, `Show`, `Update`, `Keep` (`KeepRefusal.NotAResult`/`Full`), `Release` (bleibt mit seinen Zeilen bis zum nächsten Lauf), `Close` (Nachbar wird gezeigt), `Rename`, `Label` (Name, sonst Tabelle bzw. erstes Wort · Uhrzeit). Auswahl über `SqlRun.Id`, nicht über einen Index.
  - `HeldRows` (Spalten, Zeilen ab 0, `Complete`, `DataAsOf`); `SqlRun` bekommt `Id`, `Variables` (Bind-Werte des Laufs), `ReadInTransaction` (Beginn der offenen Schreib-Transaktion beim Lesen), `Held`, `Name`.
  - `ResultOrder` mit gehaltenen Zeilen: von Anfang an im Speicher, sortiert nie über die Datenbank, auch nicht für eine Ordnung, die der Speicher nicht kann; `All` gibt die Zeilen in der Ordnung der Abfrage.
  - `AllRowsExports.ForHeld` (Export aus dem Speicher, Namen über die Session des Workspaces neu nachgeschlagen), `ColumnValueCopies.ForLoaded` mit eigener Überschrift.
- **UI:**
  - `SqlResultGrid`: Parameter `Held`/`HeldHeading` (liest nie, Namen je Block nachgeschlagen, Fußzeile „≥“ bei unvollständigen), `Hold(max)` (alle Zeilen in Abfrage-Ordnung oder die lückenlos geladenen ab 0), `LoadAllAsync(max, token)` (der Rest in einem Lesen in der gezeigten Ordnung).
  - `SqlView`: Ergebnisleiste (behaltene mit Kamera, ✕, Doppelklick benennt um – `e.Detail ≥ 2`), Grid-Key aus `Id` und Zahl der gehaltenen Zeilen (Behalten baut das Grid aus den Zeilen neu auf), „Alle Zeilen laden“ mit Abbrechen, Tastenkürzel über `TabCommand.KeepResult`. Ein neuer Lauf bricht „Alle Zeilen laden“ ab.
  - `ResultHeader`: einklappbare Kopfzeile über jedem Abfrage-Ergebnis (Statement, Uhrzeit, aufgeklappt `SqlCode` und Bind-Werte), Behalten/Loslassen, „In den Editor übernehmen“ (`MonacoEditor.SelectAsync`, monaco.js `selectRange`), bei behaltenen Zeilenzahl und Hinweis auf offene Transaktion.
  - `ResultFooter`: „Alle Zeilen laden“ (nur SQL-Editor, nicht bei gehaltenen Zeilen). `ShortcutAction.KeepResult` ohne Standardtaste, `Icons.Camera` (Lucide `camera`).

## E2E
2026-11-10, eigener Container (`New-SampleDb.ps1 -Name ferret-e2e-wp81 -Port 1581`), Testinstanz mit `--data-dir` (deutsch), Verbindung „E2E WP81“ als `connections.json`, Passwort nicht gespeichert. Klicks mit echter Windows-Eingabe (Probe vorher bestanden): Behalten, Loslassen, ✕, Doppelklick zum Umbenennen, Kopf sortieren, „Alle Zeilen laden“, Rechtsklick in Zelle und Kopf; Tippen und Tasten per CDP. Dass ein behaltenes Ergebnis nicht neu liest, in `V$SQL` des Containers geprüft (Ausführungen bleiben beim Scrollen bis zum Ende und beim Sortieren gleich).
- Gefunden und behoben: Behalten eines schon ganz geladenen Ergebnisses leerte die Fußzeile (der Status des Grids wurde verworfen, das Grid aber nicht neu aufgebaut).
- Gefunden und behoben: Der Export behaltener Zeilen sagte im Tooltip „Liest alle Zeilen … neu“ (`AllRowsExport.Note`, `GridMenu_HeldRowsTitle`), „Werte der Spalte“ „Alle Zeilen des Ergebnisses sind geladen“ (`ColumnMenu_HeldValuesTitle`); Uhrzeit in der Kopfzeile war nicht gedämpft.
- Falle beim Testen: Scheitert `Start-TestInstance.ps1` an einem noch belegten Port, baut es nicht – die Instanz danach mit `-NoBuild` lief mit der alten DLL (das CSS kam trotzdem neu, es wird im Debug aus dem Quellordner geladen).
- Die Testinstanz wurde während des Wartens einmal von außen geschlossen („Main window closing by the user“), vermutlich von einer parallelen Session; danach neu gestartet.

## Nachtrag: Code-Review (2026-11-10)
Reviewer-Agent (`/branch-code-review`), drei Durchgänge. Entscheidung des Nutzers: „Alle beheben“ (erster Durchgang, acht Befunde) und „Alle beheben“ (zweiter Durchgang, A–E). Zu Befund 6 der Vorschlag „Hinweis bei allen gehaltenen Zeilen“; zu Befund 8 bestätigt: „B - Hiermit bestätigt.“ (nur im Speicher).
- 1 (M) „In den Editor übernehmen“ zerteilte ein Statement, wenn der Cursor darin stand (`Insert` beendet den Text davor): behoben, `SqlScript.InsertionPoint` – hinter dem Statement unter dem Cursor; Test.
- 2 (M) Behalten nach weitem Scrollen (Block 0 aus `RowBlocks` verdrängt) oder direkt nach einem Kopfklick behielt 0 Zeilen: behoben, `HeldRows.Of` nimmt dann die erste Seite des Laufs; Test.
- 3 (M) „Alle Zeilen laden“ hing an der Ansicht (Fortschritt und Abbrechen beim gezeigten Ergebnis, Fußzeile eines anderen geleert): behoben, Ladezustand je Ergebnis (`_loadingAllOf`); im E2E geprüft. Teil c (Sortierung während des Ladens) bleibt, siehe „Offen“.
- 4 (K) Snapshot-Wechsel beim Halten und Laden verloren: behoben, `HeldRows.SnapshotMoved`, `HeldRows.With`; Test.
- 5 (K) Das Kürzel behielt ein hinter einem Problem verborgenes Ergebnis: behoben (nicht im E2E, das Kürzel hat keine Standardtaste).
- 6 (K) Transaktionshinweis nur bei behaltenen Ergebnissen: behoben, bei allen gehaltenen Zeilen; im E2E geprüft.
- 7 (K) Name ohne Längengrenze: behoben, 60 Zeichen (`ResultShelf.MaxNameLength`), Chip mit „…“; Test, E2E.
- 8 (K) Doku/Tests: Entscheidungen im Protokoll nachgetragen, Tests für zweites Behalten und Schließen während eines Laufs.
- A (K) Cursor zwischen Statement und seinem `;`/`/`: das Endzeichen blieb lose stehen – behoben, Leerraum bis dahin zählt zum Statement; Test.
- B (K) Rückschritt durch 2: ein leeres Ergebnis, vor dem ersten Block behalten, hieß „Erste 0 Zeilen behalten“ – behoben; Test.
- C (K) Der Transaktionshinweis kannte nur das erste Lesen – behoben, beim Behalten und Laden erfasst (konservativ); im E2E geprüft.
- D (K) Kürzen konnte ein Surrogatpaar teilen, der volle Name stand nirgends – behoben, Name im Tooltip des Chips; Test (Tooltip nicht im E2E gelesen: `tooltip.js` hatte ihn beim Hover übernommen).
- E (K) Dieser Nachtrag.
- G (K, dritter Durchgang, „Ja, beheben“) Der Fix zu A kopierte für jedes Statement vor dem Cursor das Skript bis dorthin – bei sehr großen Skripten sekundenlanges Einfrieren: behoben, Prüfung über einen Span ohne Kopie.

## Offen / Backlog
- LINQ-Konsole: Ergebnisse behalten (Backlog, Entscheidung des Nutzers).
- Ein Grid, das zu gehaltenen Zeilen wechselt (Behalten, Alle Zeilen laden), wird neu aufgebaut: Scrollposition und eine Sortierung über die Datenbank gehen verloren (die Zeilen bleiben in der gezeigten Ordnung).
- Fehlt nach einer Sortierung über die Datenbank Block 0, behält „Behalten“ die erste Seite in der Ordnung der Abfrage vom Zeitpunkt des Laufs, nicht die sortierten Zeilen von jetzt (Review, sehr selten). Ebenso geht eine Sortierung über die Datenbank verloren, die während „Alle Zeilen laden“ geklickt wird.
