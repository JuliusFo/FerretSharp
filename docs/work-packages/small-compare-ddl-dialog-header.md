# Klein: Kopfzeile des DDL-Vorschlags (Schema-Vergleich) → Release 3.68.1

Branch `fix/compare-ddl-dialog-header` (von `main`), 2026-11-10. Baut auf WP-20/WP-22 und `small-compare-ddl-selection`.

## Anlass (Meldung des Nutzers, 2026-11-10)
- Der Dialog „DDL-Vorschlag“ wirkte rechts abgeschnitten, ein Schließen-Knopf fehlte scheinbar.
- Bewertung: Der Knopf war da. Titel, „X angleichen an Y“ (zwei Auswahllisten bis je 260 px), „Im SQL-Editor öffnen“ und ✕ standen in einer Kopfzeile ohne Umbruch; `.modal` schneidet mit `overflow: hidden` ab – die Knöpfe rechts fielen weg.
- Nebenbefund: `.sql-dialog { width: min(900px, 94vw) }` greift nie, die spätere `.dialog { width: min(640px, 94vw) }` hat dieselbe Spezifität und gewinnt. SQL-Vorschau, Fehlerdialog und DDL-Vorschlag sind deshalb 640 px breit.

## Entscheidung des Nutzers (2026-11-10)
- Variante A: Kopfzeile wie in jedem Dialog (Titel, „Im SQL-Editor öffnen“, ✕), die beiden Seiten in einer eigenen Zeile darunter. Verworfen: Umbruch der Kopfzeile (Knöpfe wandern je nach Breite) und schrumpfende Auswahllisten (Namen unlesbar).

## Gebaut
- `SchemaDdlDialog`: Knöpfe in den `header`, „Align … to …“ in `div.compare-ddl-sides`.
- CSS: `.compare-ddl-sides` (Flex mit Umbruch, Trennlinie), Auswahllisten bis `min(380px, 100%)` mit `min-width: 0`.
- Breite (Entscheidung des Nutzers, 2026-11-10: „Ja, beide breiter“): `.dialog.sql-dialog { width: min(900px, 94vw) }` – die vorgesehene Breite gilt jetzt für alle drei Dialoge mit `sql-dialog` (DDL-Vorschlag, SQL-Vorschau, Fehlerdialog). Zuerst nur für den DDL-Vorschlag gesetzt, dann auf Wunsch für alle.

## Prüfung
- Build mit `-warnaserror` und alle Tests grün (ein erster Gesamtlauf hatte 23 Fehler in den Integrationstests, der Einzellauf danach 361/361 – unabhängig von dieser Änderung).
- Layout im eingebauten Browser mit dem echten `ferretsharp.css` und dem Markup des Dialogs (dunkel, lange Verbindungsnamen): bei 1300 px alles in einer Zeile, Dialog 900 px, ✕ innen; bei 780 px Dialog 734 px, zweite Auswahlliste bricht in eine neue Zeile, Knöpfe bleiben oben rechts.
- Nicht geprüft: in der laufenden App mit echtem Vergleich (keine Mausinteraktion geändert); SQL-Vorschau und Fehlerdialog in der neuen Breite nicht angesehen (nur Spezifität der Regel).

## Nachtrag: Code-Review (2026-11-10)
Eigener Reviewer-Agent, danach jeder Befund nachgeprüft; keine S/M. Entscheidung des Nutzers: „Ja, alles“ – alle drei K und den vorbestehenden Punkt (kein scrollender Body) gleich mit.
- K1 behoben: `.sql-dialog header { justify-content: flex-start }` verlor wie die Breite gegen das spätere `.dialog header` (`space-between`); unsichtbar wegen des `spacer`, jetzt `.dialog.sql-dialog header`.
- K2 behoben: Der CHANGELOG sprach von „again“ – die 900 px galten nie (schon in d54f948e stand `.dialog` hinter `.sql-dialog`).
- K3 behoben (Verdacht, im Browser reproduziert: 780×480, umbrochene Seitenzeile, zwei Hinweise → 48 px des SQL-Blocks unter der Box, auch per Scrollen nicht erreichbar): Alles unter der Kopfzeile (im DDL-Vorschlag unter „X angleichen an Y“) liegt in `div.sql-dialog-body` (`flex: 1; min-height: 0; overflow-y: auto`) – in DDL-Vorschlag, SQL-Vorschau und Fehlerdialog. Damit auch der vorbestehende Punkt erledigt: eine lange Tabelle der Bind-Variablen wurde unten abgeschnitten (40 Zeilen nachgestellt, jetzt bis zum Ende erreichbar). Im Normalfall (900 px hoch) kein äußeres Scrollen, der SQL-Block scrollt wie bisher selbst.
- Kein Layout-Test (Höhe, Scrollen), geprüft mit Messungen im eingebauten Browser mit echtem `ferretsharp.css`; in der laufenden App nicht angesehen.
- Zweiter Durchgang: alle Fixes gedeckt, nichts kaputt (Selektoren, Fokus/`trapFocus`, Textmarkierung, Tests). Drei neue K; Entscheidung des Nutzers: „Wie vorgeschlagen“.
  - K4 so gelassen: In sehr niedrigen Fenstern stehen zwei Scrollbalken nebeneinander (SQL-Block und Bereich darunter), alles erreichbar. Ein Balken hieße: Kopier-Knopf scrollt bei langem SQL weg, Dialog wächst auch im Normalfall bis 92vh.
  - K5 behoben: Die neue Zeile in `ui-css.md` beschreibt die Falle genau (nur Regeln **vor** `.dialog` verlieren; `.plan-dialog` steht dahinter) und nennt `.dialog.x-dialog` als „Vorschlag (nicht bestätigt)“.
  - K6 behoben: `DialogStyleTests` liest die Klassen aller `ModalFrame` und schlägt an, wenn eine Regel `.klasse` bzw. `.klasse header` vor `.dialog` bzw. `.dialog header` steht und eine ihrer Eigenschaften setzt. Scheiterte vor dem Fix an `.work-clone-dialog`.
  - Dritter Durchgang: Test, Fix und Doku gedeckt (Nachbau des Tests meldet auf `main` `.sql-dialog`, `.sql-dialog header`, `.work-clone-dialog`). Zwei K zu den Grenzen des Tests; Entscheidung des Nutzers: „Wie vorgeschlagen“. K7 behoben: Der Test sammelt alle allgemeinen Selektoren (`.modal`, `.dialog`, `.dialog …` wie `footer`) aus dem CSS statt zwei fest zu nennen (Gegenprobe mit einer eingeschobenen `.work-clone-dialog footer { padding }` schlug an). K8 nur vermerkt: Kurzschreibweise gegen Einzeleigenschaft (`padding-top` vor `padding`) erkennt der Test nicht – im Kommentar des Tests genannt.
  - Vorher schon da, behoben: `.work-clone-dialog { width: min(640px, 92vw) }` griff aus demselben Grund nie (sichtbar nur unter ~680 px Fensterbreite: 94vw statt 92vw), jetzt `.dialog.work-clone-dialog`.
