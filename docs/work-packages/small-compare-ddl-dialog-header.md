# Klein: Kopfzeile des DDL-Vorschlags (Schema-Vergleich)

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
