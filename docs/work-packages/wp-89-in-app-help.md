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
  - `MarkersAsync`: Marker mit Offsets. Was das Speichern meldet, kommt aus `ScriptHeaders.ForSave` (neu: `ScriptSave.Errors` mit Zeile) – dieselben Regeln, kein zweiter Prüfer. Dazu unbekannte Kopfzeilen (Hinweis; bis zwei Tippfehler von einer bekannten als Warnung mit Vorschlag, `Similar`), bekannte Kopfzeilen unter dem Kopf (Warnung, „wirkt nicht“) und mit Schema Tabelle/Spalte eines `@target` (fehlende Tabelle Hinweis, fehlende Spalte Warnung; Dictionary-Fehler lassen die Schema-Marker weg).
  - `HoverAsync`: Markdown je Kopfzeile; bei `@target` gefunden/nicht gefunden und die Spalten, in deren Zellmenü das Skript erscheint (`ScriptTargets.ReachedBy`, rückwärts über dieselben Beziehungen und Schritte wie `Reached`; höchstens acht genannt). Namen nur in Code-Spans ohne Backticks.
  - `CompleteAsync`: nur auf einer Zeile `-- @…`, über der nur Kopfzeilen stehen; sonst `null`, dann antwortet die SQL-Completion. Neue Art `SqlCompletionKind.Variable` für Binds.
  - `WithoutHeader`: das Statement ohne führende Kopfzeilen.
  - `Lines` teilt wie `string.ReplaceLineEndings` (CR, LF, CRLF, NEL, LS, FF, PS), damit Zeilennummern zu `ScriptHeaders` passen; Offsets beziehen sich auf den Text, wie Monaco ihn schickt.
- **Monaco:** Hover-Provider je Sprache (fragt `OnHover`, Markdown untrusted), `@` als Auslöser nur für `sql`, `quickSuggestions.comments` nur für den SQL-Editor (`SuggestInComments`), Marker je Besitzer (`setOffsetMarkers(…, owner)`, Schwere `info`): Kopf-Marker und Laufzeitfehler löschen sich nicht gegenseitig.
- **SQL-Tab:** Kopf-Marker nach jeder Textänderung (eigener Zähler, nur der neueste zeigt) und beim Anlegen des Editors; Hilfe-Knopf in der Leiste; eine Seitenleiste zur Zeit (Skripte, Verlauf, Hilfe). `SqlTab.HelpOpen` (nicht gespeichert), `TabCommand.ShowHelp`, `ShellState.ShowScriptHelp` (aktiver SQL-Tab, sonst ein neuer). Kopf-Meldung nach dem Speichern mit Link „Hilfe“; der Speichern-Dialog schließt für die Hilfe (er verdeckte sie). Ergebnis-Kopf, Chips, Laufanzeige und Verlauf zeigen `WithoutHeader`.
- **Hilfe-Panel:** `Common/HelpPanel` (Thema als Parameter), Inhalt `Sql/ScriptHelp` mit neutralem Beispiel; Texte in `HelpText` (neu, Englisch und Deutsch). Icon `Help` (Lucide `circle-question-mark`). Aktion `ScriptHelp` („Hilfe: SQL-Skripte“, ohne Standardtaste) für die Befehlspalette; Link unter Explorer › *Skripte*.
- Tests: `ScriptHeaderHelpTests` (Marker mit CRLF, Tippfehler, Kopfzeile unter dem Kopf, Schema-Marker, Hover mit Zellmenü-Spalten, Completion je Stelle, `WithoutHeader`, Zeilenteilung wie der Leser), `ScriptTargetsTests.The_columns_reaching_a_target_are_exactly_those_whose_cell_menu_offers_it` (`ReachedBy` ↔ `Reached` für jede Spalte), `ScriptTabsTests.The_help_on_scripts_opens_beside_the_active_sql_tab_or_a_new_one`.

## E2E
(folgt)

## Fallen
- Das Write-Werkzeug macht aus `' '` in C#-Quelltext das Zeichen selbst – in einem Zeichenliteral ist das ein Zeilenumbruch (CS1010). Nach dem Schreiben solcher Escapes die Datei per Skript prüfen.
