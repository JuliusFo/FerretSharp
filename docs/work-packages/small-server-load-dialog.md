# Klein: Serverlast – beschriftete Achsen, Statement-Dialog, Rang und Abdeckung in „Herkunft im Code“ → Release 3.64.0

Branch `feat/server-load-details-dialog` (von `main`, nach dem Merge von WP-76), 2026-11-08. Baut auf WP-75 und WP-76.

## Anlass (Wünsche des Nutzers)
- Die Diagramme der Serverlast bekommen eine beschriftete Y-Achse.
- Die Details eines Statements gingen am Ende der Liste auf; bei einer langen Liste muss man jedes Mal nach unten scrollen. Stattdessen ein Dialog mit ein- und ausklappbaren Bereichen (SQL, Code, Plan): SQL standardmäßig offen, alles andere zu.
- Bei den Code-Stellen eine „visuelle“ Anzeige, wie wahrscheinlich die Stelle ist bzw. wie das Ranking ist – Vorschlag erwünscht.

## Entscheidungen des Nutzers
- Plan samt Vorschlag zur Anzeige der Code-Stellen freigegeben („Klingt sehr geil, super! Setze das gerne so um, mitsamt den neuen Buttons.“), 2026-11-08:
  - Y-Achse links neben jedem Diagramm (0 / Mitte / oben), oben bei den Sessions ein runder Wert.
  - Dialog mit Kennzahlen der Zeile im Kopf; Bereiche SQL-Text (offen), Herkunft im Code (zu, im Kopf Zahl und bester Kandidat), Plan und Werte (zu). Der Auf/Zu-Zustand gilt für die Laufzeit der App.
  - Knöpfe „Vorheriges / Nächstes Statement“ im Kopf des Dialogs.
  - Je Kandidat Rang (gleicher Score → gleicher Rang mit „=“) und ein Balken mit fünf Segmenten für die **Abdeckung**: Anteil der Merkmale des Statements (Tabellen, Filterspalten, bei DML gesetzte Spalten), die die Methode trifft; Tooltip mit „trifft x von y“, „fehlt“, „zusätzlich in der Methode“. Kein Balken relativ zum Besten (der Erste stünde immer auf 100 %).

## Gebaut
- **Core:** `CodeOriginFit` (Getroffen/Gesamt, `Share`, `Missing` als `CodeOriginReason`, `Extra` = Filterspalten und Navigations der Methode, die das Statement nicht hat) an jedem `CodeOriginCandidate`, berechnet in `CodeOriginFinder` neben dem Score. Merkmale je Tabelle des Modells: die Tabelle selbst (berührt: abgefragt, über Navigation erreicht, geschrieben oder gefiltert), jede Filterspalte, bei DML jede gesetzte Spalte, die eine Property hat. Die Reihenfolge bleibt die des Scores. Vorschlag (nicht bestätigt): die Gewichtung „jedes Merkmal zählt gleich“.
- **UI State:**
  - `LoadChart.SessionsMax` rundet auf einen Wert, dessen Hälfte auch rund ist (`RoundUp`: 1, 1,5, 2, 2,5, 3, 4, 5, 6, 8 × 10ⁿ; vorher „× 1,25“), Spielraum 10 % (Vorschlag, nicht bestätigt).
  - `CodeOriginRows`: `Ranks` (gleicher Score teilt den Rang des Ersten, als gleichauf markiert), `Filled`/`Tone` (Vorschlag, nicht bestätigt – Segmente: alle nur bei voller Abdeckung, mindestens eins; grün nur bei voller Abdeckung ohne zusätzliche Filter/Navigations der Methode – im E2E stand ein Kandidat mit Rang 3 so grün da wie der erste –, Akzent ab der Hälfte, sonst grau), `FitTitle`.
  - `ServerLoadView.Neighbours` (Vor/Zurück in der Liste, wie sie gezeigt wird; nichts für ein Statement außerhalb), `ServerLoadService.OpenParts`/`TogglePart` (`StatementPart`).
- **UI:**
  - `ServerLoadPage`: Y-Achse (`load-plot`, `load-y`) als HTML neben dem SVG – im SVG (`preserveAspectRatio="none"`) würde Text mitgestreckt; Mittellinie jetzt auch bei den Sessions; „· oben X“ und das „50 %“ der Zeitachse entfallen (Texte `ServerLoad_AxisTop`, `ServerLoad_AxisHalf` entfernt). Ein Klick auf eine Zeile oder auf eine SQL_ID der aktiven Sessions öffnet den Dialog (vorher: nochmal Klicken schloss die Details).
  - `ServerLoadStatementDialog` (neu): `ModalFrame` (Esc, Klick daneben), Kopf mit App-Markierung, SQL_ID, Schema · Modul · Pläne, ↑/↓, „Im SQL-Editor öffnen“ (schließt den Dialog vorher), ✕; darunter die Kennzahlen der Zeile mit Modus (Zeitfenster/Seit Laden) bzw. „nicht in der Liste“; drei `FoldSection`s.
  - `FoldSection` (Common, neu): Kopf mit Chevron, Titel und Zusammenfassung, Inhalt nur offen.
  - `CodeOrigins`: als `FoldSection` (Parameter `Open`/`OnToggle`); Kopf mit Zahl, Info-Tooltip und – zugeklappt – Balken und Name des besten Kandidaten; Liste als Raster Rang · Balken · Methode · Stelle · Gründe.
  - Texte EN/DE (`ServerLoadText`, `ModelViewText`), CSS unter „Server load“.
- **Tests:** Core `CodeOriginFinderTests` (Abdeckung bei Abfrage und DML, fehlende Tabelle); UI `LoadChartTests` (Rundung als Theory), `CodeOriginRowsTests` (Ränge, Balken, Tooltip), `ServerLoadServiceTests` (Vor/Zurück).

## Offen (Vorschlag, nicht bestätigt)
- Tastenkürzel für Vor/Zurück im Dialog (bräuchte eine `ShortcutAction` ohne Standardtaste).
- Ob der Dialog nach „Im SQL-Editor öffnen“ geschlossen bleiben soll (so gebaut) oder beim Zurückkehren wieder aufgehen soll.

## E2E
2026-11-08, eigener Container (`New-SampleDb.ps1 -Name ferret-e2e-sld -Port 1577`, `SELECT_CATALOG_ROLE` für FERRET), Testinstanz mit `--data-dir` (deutsch, dunkel und hell); Klicks mit echter Windows-Eingabe (`Invoke-RealClick.ps1`, Probe vorher bestanden), Esc per CDP. Als Code eine Kopie des `samples`-Modells im Scratchpad mit einer Konsolen-App (sieben Methoden auf `Auftrag`/`AuftragPosition`, auch `Include`, `Count` und `SaveChanges`) und eigener `.slnx`, gegen den Container laufen lassen.
- Klick auf eine Zeile öffnet den Dialog; Kennzahlen der Zeile laufen mit jedem Blick mit; SQL offen, Herkunft zu mit Zahl und vollem Balken des besten Kandidaten im Kopf; Aufklappen per Klick; Plan-Bereich mit Werten und Plan; ↓/↑ wechseln das Statement samt Markierung in der Liste (ein Statement ohne Modelltabelle zeigt keine Herkunft); Esc schließt und hebt die Auswahl auf; „1 weitere“ klappt auf. Eine neue Instanz beginnt wieder nur mit SQL offen.
- Rang und Balken am echten SQL von EF Core 8: `WHERE STATUS = :status_0` → `ByStatus` und `CountOpen` „=1“ (2 von 2), `OpenOrdersOf` Rang 3, `NotesOf`/`Annotate` „=4“ (1 von 2, Tooltip „Fehlt: filtert nach STATUS · Zusätzlich in der Methode: AUFTRAG.KUNDE_ID“). Statement mit `KUNDE_ID` und `STATUS` → `OpenOrdersOf` Rang 1 grün, drei Kandidaten „=2“.
- Y-Achse: 100 % / 50 % / 0 % und 1 / 0,5 / 0 neben den Diagrammen, hell und dunkel.
- Gefunden und behoben: Der Dialog sprang beim Aufklappen und bei jedem Statementwechsel (vertikal zentriert) – jetzt oben verankert. `OpenOrdersOf` (deckt `STATUS` ab, filtert zusätzlich nach `KUNDE_ID`) stand auf Rang 3 so grün da wie Rang 1 – grün jetzt nur ohne Zusätzliches. Methodennamen standen zentriert in ihrer Spalte.
- Log beider Läufe ohne Warnungen und Fehler.
- Nicht geprüft: SQL_ID einer aktiven Session (keine lief während eines Blicks), „Im SQL-Editor öffnen“ aus dem Dialog (Weg wie vorher, schließt nur vorher den Dialog).

## Nachtrag: Code-Review (2026-11-08)
Review per `/branch-code-review` (eigener Reviewer-Agent). Entscheidung des Nutzers vorab, bevor er schlafen ging: „Wenn du mit der Implementierung durch bist, mach bitte ein branch-code-review. Das wie immer iterativ, bis alles soweit behoben ist. Dabei kannst du gerne einfach deine Vorschläge umsetzen“ – alle Punkte umgesetzt, Verdachtsfälle vorher mit echten Klicks reproduziert.
1. M Am Listenende (↓ auf dem letzten Statement, oder das Statement fällt beim Blick aus dem Fenster) wurde der fokussierte Pfeil gesperrt, der Fokus fiel auf `BODY` – Esc schloss nicht mehr, Tab lief aus dem Dialog. **Reproduziert und behoben:** `ModalFrame.FocusIfLostAsync` (dom.js `focusIfLost`) nach dem Rendern, wenn ein Pfeil gesperrt wurde, nur wenn der Fokus wirklich verloren ist (zweiter Durchgang K2). E2E: Fokus auf der Box, Esc schließt.
2. M Jeder Schritt durch den Dialog stellte ein Nachschlagen an das Gate der Serverlast-Session, alle liefen bis zum Ende (über VPN: Plan erst nach allen verworfenen). **Behoben:** `ServerLoadView.Lookup` wird bei jeder neuen Auswahl abgebrochen, nur das jüngste Nachschlagen setzt das Ergebnis – dabei gefunden: dasselbe Statement zweimal gewählt, löschte das Ende des ersten den Spinner des zweiten. Test (ohne Fix rot).
3. M (vorher schon da, jetzt öfter erreichbar) Text im Dialog markieren und neben dem Dialog loslassen schloss ihn (Chromium schickt den Klick an den gemeinsamen Vorfahren). **Reproduziert und behoben** für alle Dialoge in dom.js `trapFocus`. E2E: Dialog und Auswahl bleiben, ein Klick daneben schließt weiter.
4. K Der Dialog ging beim Zurückkehren auf die Seite von selbst wieder auf. **Behoben:** `HideAsync` verwirft Auswahl und Nachschlagen. Test (ohne Fix rot).
5. K Zugeklappte Herkunft: kein Hinweis auf einen gescheiterten Index, der „beste“ von mehreren gleichauf ohne „=“, abgeschnittener Name ohne Tooltip. **Behoben** (Warnsymbol mit Tooltip und `aria-label`, Rang, `title`).
6. K Icon-Summaries von `ChevronUp`/`ChevronDown`, Vorschläge im Protokoll unmarkiert, 110 px doppelt gepflegt. **Behoben** (Summaries, „Vorschlag, nicht bestätigt“, Kommentar an beiden Stellen).
- Zweiter Durchgang: alle Fixes decken ihre Befunde, nichts gebrochen. K1 `pointerdown` in der Capture-Phase (ein Handler in der Box, der den Druck stoppt, ließ das alte Flag stehen) – **behoben**; K2 Fokus nur zurückholen, wenn er verloren ist – **behoben**; K3 der Abbruch-Test scheiterte ohne Fix erst nach 30 s – **behoben** (`WaitAsync` 5 s); K4 Regel in `ui.md`, dieser Nachtrag, `aria-label` – **behoben**.

