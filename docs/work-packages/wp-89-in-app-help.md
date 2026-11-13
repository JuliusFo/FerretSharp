# WP-89 Hilfe in der App, zuerst für SQL-Skripte → noch ohne Release

Branch `wp/89-in-app-help` (von `main`), 2026-11-13, noch ohne Release. ADR 0051.

## Planung
Wunsch des Nutzers (2026-11-13), als eigenes Paket gewollt („ich finde das wichtig“): Anlass war ein `-- @target`, das im SQL-Editor getippt nie wirkte (`fix-script-header-in-editor.md`). Eine Hilfe gab es in FerretSharp bisher nirgends – was hier entsteht, ist das Muster für andere Bereiche. Braucht den Fix der Kopfzeilen im Editor (3.71.1).

Plan am 2026-11-13 vom Nutzer so freigegeben („Ja, so umsetzen“), mit Claudes Vorschlägen zu den offenen Punkten:
- **Form:** Panel neben dem Editor wie *Skripte* und *Verlauf* (gebraucht beim Tippen; eine Seite verdeckte den Tab, ein Dialog sperrte den Editor). Einstiege: Knopf in der Leiste des SQL-Tabs, Link in der Kopf-Meldung nach dem Speichern und im Speichern-Dialog, Explorer › *Skripte*, Befehlspalette.
- **Nur Skripte**, das Panel aber mit Thema (`HelpTopic`); Filter, Audit-Vorlagen, Tastenkürzel in den Backlog.
- **„Ziel beim Speichern vorschlagen“ bleibt im Backlog** (Aliase in `WHERE t.x = :x` auflösen ist eine eigene Funktion); als kleiner Teil davon die Completion hinter `-- @target` (Binds, Tabellen, Spalten).
- **Führende Kopfzeilen über dem Ergebnis und im Verlauf ausblenden**; ausgeführt wird unverändert (Offsets von Markern und Fehlern).

## Gebaut
- **Core `Scripts/ScriptHeaderHelp`:**
  - `Markers` und `TargetMarkersAsync`: Marker mit Offsets, die ohne Schema sofort, die der Ziele über den Schema-Cache (Code-Review). Was das Speichern meldet, kommt aus `ScriptHeaders.ForSave` (neu: `ScriptSave.Errors` mit Zeile) – dieselben Regeln, kein zweiter Prüfer. Dazu unbekannte Kopfzeilen (Hinweis; bis zwei Tippfehler von einer bekannten als Warnung mit Vorschlag, `Similar`), bekannte Kopfzeilen unter dem Kopf (Warnung, „wirkt nicht“) und mit Schema Tabelle/Spalte eines `@target` (fehlende Tabelle Hinweis, fehlende Spalte Warnung; Dictionary-Fehler lassen die Schema-Marker weg).
  - `HoverAsync`: Markdown je Kopfzeile; bei `@target` gefunden/nicht gefunden und die Spalten, in deren Zellmenü das Skript erscheint (`ScriptTargets.ReachedBy`, rückwärts über dieselben Beziehungen und Schritte wie `Reached`; höchstens acht genannt). Namen nur in Code-Spans ohne Backticks.
  - `CompleteAsync`: nur auf einer Zeile `-- @…`, über der nur Kopfzeilen stehen; sonst `null`, dann antwortet die SQL-Completion. Neue Art `SqlCompletionKind.Variable` für Binds.
  - `WithoutHeader`: das Statement ohne führende Kopfzeilen.
  - `Lines` teilt wie `string.ReplaceLineEndings` (CR, LF, CRLF, NEL, LS, FF, PS), damit Zeilennummern zu `ScriptHeaders` passen; Offsets beziehen sich auf den Text, wie Monaco ihn schickt.
- **Monaco:** Hover-Provider je Sprache (fragt `OnHover`, Markdown untrusted), `@` als Auslöser nur für `sql`, `quickSuggestions.comments` nur für den SQL-Editor (`SuggestInComments`), Marker je Besitzer (`setOffsetMarkers(…, owner)`, Schwere `info`): Kopf-Marker und Laufzeitfehler löschen sich nicht gegenseitig.
- **SQL-Tab:** Kopf-Marker nach jeder Textänderung (eigener Zähler, nur der neueste zeigt) und beim Anlegen des Editors; Hilfe-Knopf in der Leiste; eine Seitenleiste zur Zeit (Skripte, Verlauf, Hilfe). `SqlTab.HelpOpen` (nicht gespeichert), `TabCommand.ShowHelp`, `ShellState.ShowScriptHelp` (aktiver SQL-Tab, sonst ein neuer). Kopf-Meldung nach dem Speichern mit Link „Hilfe“; der Speichern-Dialog schließt für die Hilfe (er verdeckte sie) und gibt seine Eingaben dem nächsten Dialog des Tabs – nur bei „Als Skript speichern“ des ganzen Fensters, aus Auswahl und Verlauf nicht (Code-Review). Ergebnis-Kopf, Chips, Laufanzeige und die Liste des Verlaufs zeigen `WithoutHeader`; gespeichert wird im Verlauf der ganze Text (Code-Review).
- **Hilfe-Panel:** `Common/HelpPanel` (Thema als Parameter), Inhalt `Sql/ScriptHelp` mit neutralem Beispiel; Texte in `HelpText` (neu, Englisch und Deutsch). Icon `Help` (Lucide `circle-question-mark`). Aktion `ScriptHelp` („Hilfe: SQL-Skripte“, ohne Standardtaste) für die Befehlspalette; Link unter Explorer › *Skripte*.
- Tests: `ScriptHeaderHelpTests` (Marker mit CRLF, Tippfehler, Kopfzeile unter dem Kopf, Schema-Marker, Hover mit Zellmenü-Spalten, Completion je Stelle, `WithoutHeader`, Zeilenteilung wie der Leser), `ScriptTargetsTests.The_columns_reaching_a_target_are_exactly_those_whose_cell_menu_offers_it` (`ReachedBy` ↔ `Reached` für jede Spalte), `ScriptTabsTests.The_help_on_scripts_opens_beside_the_active_sql_tab_or_a_new_one`.

## E2E
Eigene Testinstanz (`--lang=de`, eigener Datenordner, Skripte im Scratchpad) gegen einen eigenen Container mit dem Beispielschema. Klicks mit echter Windows-Eingabe (Probe bestanden), Tippen per CDP (`insertText`).
- Explorer › *Skripte* › „Wie funktionieren Skripte?“ (echter Klick) öffnet einen neuen SQL-Tab mit Hilfe; der Hilfe-Knopf in der Leiste schaltet sie um (echte Klicks, `aria-pressed`), *Verlauf* schließt sie und umgekehrt.
- Marker beim Tippen: `@tagret` (Warnung mit Vorschlag), `KUNDEN.NOPE` (Warnung), `GIBTSNICHT.ID` (Hinweis), `@param` ohne Bind und `@target` unter dem SQL (Warnung) – alle unter eigenem Besitzer.
- Hover (echte Mausbewegung) über `@target kunde_id KUNDEN.KUNDE_ID`: Erklärung, „Gefunden: KUNDEN.KUNDE_ID (NUMBER(10))“ und die drei Spalten, die darauf zeigen.
- Completion: `-- @` → drei Kopfzeilen (Auslöser `@`), `target k` → `kunde_id`, ` ku` → `KUNDEN` und die View, `.` → die Spalten mit Typ.
- Ausgeführt mit Kopf: Ergebnis-Kopf und Verlauf zeigen das SQL ohne Kopfzeilen.
- Speichern-Dialog: der Link schließt ihn und öffnet die Hilfe (echter Klick); nach dem Speichern mit unbrauchbarem `@target` steht „Hilfe“ in der Meldung. Befehlspalette: „>Hilfe“ findet „Hilfe: SQL-Skripte“, Enter öffnet sie.
- Gefunden und behoben: Der Link im Panel war um das Padding von `.link-btn` eingerückt; das Beispiel brach im schmalen Panel um (jetzt waagrecht scrollbar). Hell und dunkel geprüft; Log ohne Warnungen.
- Offen (Frage an den Nutzer): Das Panel ist so hoch wie der Editor (wie *Skripte* und *Verlauf*); bei kleinem Editor ist der Lesebereich knapp – über die Ergebnis-Fläche hinweg wäre mehr Platz, aber ein anderer Aufbau des SQL-Tabs.

## Fallen
- Das Write-Werkzeug macht aus `' '` in C#-Quelltext das Zeichen selbst – in einem Zeichenliteral ist das ein Zeilenumbruch (CS1010). Nach dem Schreiben solcher Escapes die Datei per Skript prüfen.

## Nachtrag: Code-Review (2026-11-13)
Eigener Reviewer-Agent über den Branch, zwei Durchgänge nach den Fixes. Entscheidung des Nutzers zu beiden Runden: „Ja, alle beheben“ – zu 1: der Verlauf blendet den Kopf nur in der Anzeige aus; zu 7: die Eingaben kommen beim nächsten Speichern zurück.

| # | Befund | Ergebnis |
|---|---|---|
| 1 (M) | Der Verlauf speicherte das SQL ohne Kopf; „Als Skript speichern“ und „Übernehmen“ aus dem Verlauf verloren `@target` | behoben: gespeichert wird der ganze Text, nur die Liste zeigt `WithoutHeader` |
| 2 | Spaltentyp und DB-Fehler standen roh im Hover-Markdown (Bild/Link aus einem Typnamen) | behoben: als Code-Span, Test |
| 3 | Marker beim Anlegen rechneten auf `Tab.Text`, Monaco vereinheitlicht Zeilenenden | behoben: Text aus dem Editor |
| 4 | Schema-Marker kamen erst nach dem Tippen bzw. veralteten nach einem Neuladen; ein hängendes Lesen hielt alle Marker auf | behoben: zwei Schritte mit 10 s Grenze, neu nach Laden des Schemas |
| 5 | Tippfehler-Distanz legte eine Matrix in Länge des Namens an (1 Mio. Zeichen aus einer geteilten Datei) | behoben: Längenfilter, Test |
| 6 | Hover und Completion zerlegten bei jedem Aufruf den ganzen Text | behoben: erst die Zeile am Cursor |
| 7 | Der Hilfe-Link im Speichern-Dialog verwarf die Eingaben; Abbrechen während des Schreibens ließ Fehler und „ersetzen?“ ins Leere laufen (vorher schon da) | behoben: Eingaben behalten (`ScriptSaveInputs`), Abbrechen während `Busy` gesperrt |
| 8 | Aktion fehlte in der Kürzel-Tabelle (`architecture.md`) | behoben |
| 9 | Platzhalter der Syntaxzeilen im Panel nicht übersetzt (Hover schon) | behoben: aus `HelpText` |
| 10 | `@` löst die Completion auch im Bedingungsfeld des Tabellen-Tabs aus | ohne Wirkung, so gelassen (ADR 0051) |
| N1 (M) | Fix 7 überschrieb eine nach dem Lesen der Hilfe in den Text getippte `@description` | behoben: der Text gewinnt, wenn er sich geändert hat; Test |
| N2 | Doc-Kommentar von `ScriptSaveDraft` stand über dem neuen Record | behoben |
| N3 | Ziel-Marker verschwanden bei jedem Tastendruck kurz (zwei Aufrufe) | behoben: aus dem Cache ein Aufruf; Core-Test hält „sofort fertig“ fest |
| N4 | Test zu 5 bestand auch ohne Fix | behoben: misst die Allokation; dabei die kleingeschriebene Kopie (2 MB) vor dem Filter entfernt |
| N5 | Der Unterordner kam ohne seine Wurzel mit | behoben: nur zusammen; Einschränkung auf das ganze Fenster oben vermerkt |

E2E nach den Fixes (Testinstanz, echte Klicks): Eingaben nach Hilfe-Link und zurück behalten, eine im Text getippte Beschreibung gewinnt, sonst die des Felds; beim Tippen mit Ziel aus dem Cache blieb der Ziel-Marker bei jedem Tastendruck stehen; Ergebnis-Kopf und Verlauf ohne Kopf, „Als Skript speichern“ aus dem Verlauf übernimmt die `@description`; nach Neuladen des Schemas kommen die Ziel-Marker wieder. Log ohne Warnungen.
