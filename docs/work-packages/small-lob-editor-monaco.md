# Klein: LOB-Editor mit Monaco (`feat/lob-editor-monaco`) → Release 3.56.0

Branch `feat/lob-editor-monaco` (von `main`), 2026-10-29.

## Anlass
Rückmeldung eines Kollegen des Nutzers zum CLOB-Dialog (WP-10), mit Screenshot eines einzeiligen JSON-Telegramms:
1. Ein Schalter, der lange Zeilen bewusst umbricht, auch wenn der Text keine Zeilenumbrüche hat.
2. JSON-Anzeige mit Färbung, „vielleicht mehr“.
3. Doppelklick markiert den ganzen Text statt eines Worts (wie früher im SQL-Editor).
4. Esc schließt den Dialog nicht, solange der Fokus im Textfeld ist.
5. Bei großen CLOBs bleibt „Lädt …“ stehen, bis man klickt; ebenso leere Ergebniszeilen nach einem FK-Sprung (siehe „Offen“).

Vorgabe des Nutzers: zuerst ohne E2E-Lauf (er arbeitete selbst mit der App), der E2E-Lauf folgte am 2026-10-30.

## Gebaut
- Der Text eines CLOB/NCLOB steht in einem **Monaco-Editor** statt einer `<textarea>` (`MonacoEditor` mit `Class="lob-text"`). Damit erledigen sich 3 und 4: Monaco bekommt den Doppelklick über `dropRepeatedMouseDown` (das doppelte Mouse-down des WPF-Hosts machte daraus einen Dreifachklick = ganze Zeile, und die Zeile war der ganze Text), und Esc, das Monaco nicht selbst braucht (keine Auswahl, kein Suchfeld offen), erreicht `ModalFrame`. Vorher hielt `@onkeydown:stopPropagation` am Textfeld jede Taste auf, weil der getippte Text .NET erst beim Verlassen (`change`) erreichte.
- **Esc mit Änderung** schließt weiter nicht (eine lange Eingabe darf nicht verloren gehen), sagt jetzt aber, wie es geht: „Der Wert wurde geändert – „Abbrechen“ verwirft die Änderung, „Übernehmen“ behält sie.“ Vor Esc, Übernehmen und „Als Datei speichern“ holt der Dialog den Text aus dem Editor (`GetTextAsync`) – Monaco meldet Tippen erst nach 250 ms. „Geändert“ heißt: anderer Text als beim Öffnen des Editors (Tippen und Rückgängig bis zum Ausgangstext ist keine Änderung) oder NULL/Datei/Text für NULL.
- **Zeilen umbrechen**: Schalter in der Kopfzeile des Dialogs, nur Anzeige (`wordWrap`). Text ohne Zeilenumbruch (das einzeilige JSON) beginnt immer umbrochen (Entscheidung des Nutzers, 2026-10-30); für Text mit Zeilenumbrüchen gilt der gemerkte Schalter `AppSettings.LobWordWrap` (wie `ChangesPanelOpen` ohne Zeile auf der Einstellungsseite). Umschalten bei einzeiligem Text gilt nur für diesen Dialog – er beginnt ja ohnehin umbrochen.
- **Färbung Text / JSON / XML**: automatisch nach dem ersten Zeichen (`LobContent.DetectFormat`: `{`/`[` JSON, `<` XML), umschaltbar. JSON färbt eine eigene Monarch-Sprache `fs-json` (Schlüssel und Werte getrennt, Theme-Farben über `tokenPostfix: '.json'`): Monacos eigenes `json` hätte seinen Sprachdienst geladen, und der braucht einen Worker, den der getrimmte Build nicht mitbringt (`lib/monaco/README.md`). XML über Monacos Monarch-`xml`.
- **Einrücken** (nur JSON): `LobContent.IndentJson` prüft mit `JsonDocument` und rückt dann auf Textebene ein – Strings und Zahlen bleiben Zeichen für Zeichen, wie sie sind (`System.Text.Json` würde Umlaute escapen und Zahlen neu schreiben). Ungültiges JSON meldet Zeile und Position. Bearbeitbar: eine Änderung im Editor (Rückgängig nimmt sie zurück, gespeichert erst mit „Übernehmen“); nur lesen: eine Ansicht in einem neuen Editor, der Wert (Zeichenzahl, Datei) bleibt der geladene.
- Globale Kürzel, die zugleich Monaco-Tasten sind (Ctrl+F = „Spalte suchen“), fing `shortcuts.js` auch im Dialog ab – Monacos Suche wäre im LOB-Dialog tot gewesen. Jetzt bleiben `editorKeys` (Suchen, Ersetzen, Rückgängig, Zwischenablage) in einem Monaco innerhalb eines Dialogs beim Editor; F5 und die übrigen werden weiter abgefangen.
- `MonacoEditor`: Parameter `WordWrap`, `ReadOnlyMessage` (vorher fest der PL/SQL-Hinweis), `LineFeed`, Methoden `SetWordWrapAsync`, `SetLanguageAsync`.
- Im E2E gefunden und behoben:
  - **Zeilenende CRLF:** Für einen Text ohne Zeilenumbruch nimmt Monaco das Zeilenende der Plattform (`
` unter Windows) – „Einrücken“ und jedes Enter schrieben CRLF in den Wert, die `<textarea>` schrieb immer `
`. `LineFeed` setzt das Modell dann auf `
`; ein Text mit `
` behält es (in der DB geprüft: der eingerückte Wert hat nur LF).
  - **Editor verschwand beim Einrücken:** Ob der Wert zu groß für den Editor ist (10 MB), entschied jede Eingabe neu – das Einrücken des 6,5-MB-JSON (10,3 Mio. Zeichen mit CRLF) nahm den Editor mitten in der Bearbeitung weg, ohne Rückgängig. Jetzt entscheidet das Öffnen (bzw. eine geladene Datei) darüber (`_tooLarge`).
- BLOBs (Bild, Hex) unverändert.

## E2E-Lauf (2026-10-30)
Eigener Container, Testinstanz mit `--data-dir` im Scratchpad, Tabelle mit einzeiligem JSON (3,7 KB), mehrzeiligem Text, XML, 6,5-MB-JSON in einer Zeile, NULL und ungültigem JSON.
- Doppelklick mit echter Windows-Maus (`SendInput`): `mousedown` kam mit `detail` 1, 2, 3 an (das Duplikat des Hosts), markiert war genau das Wort bzw. die Zahl; Dreifachklick markiert die Zeile.
- Esc: mit Auswahl nur die Auswahl weg, danach schließt es; Tippen und sofort Esc (innerhalb der 250 ms) zeigt den Hinweis; Rückgängig bis zum Ausgangstext, dann Esc schließt.
- Ctrl+F öffnet Monacos Suche, Esc schließt nur die Suche; Ctrl+Z bleibt im Editor (keine Änderung im Undo-Stapel des Workspaces).
- Tippen und sofort „Übernehmen“: der Wert in der Zelle hat den ganzen Text.
- Umbruch: einzeilig an, mehrzeilig nach Schalter, der Schalter landet in `settings.json`. Färbung JSON und XML hell und dunkel; Einrücken bearbeitbar (Rückgängig nimmt es zurück) und nur lesen (Prod-Verbindung: Ansicht, Tippen zeigt den Hinweis); ungültiges JSON meldet Zeile 1, Position 8.
- 6,5 MB: lädt lokal sofort, über einen Proxy mit 50 ms je Paket in rund 13 s; Scrollen ans Ende 5 ms, Einrücken rund 1 s (660 000 Zeilen). Eine einzelne Zeile über 20 000 Zeichen färbt Monaco nicht (`maxTokenizationLineLength`), nach dem Einrücken schon.
- Log der Testinstanz ohne `[ERR]`/`[WRN]`.

## Offen: „Lädt …“ / leere Zeilen bis zum Klick
Im E2E **nicht nachzustellen**: Über den Proxy (50 ms je Paket, wie VPN) wechselte der Dialog nach ~15 s von „Lade den ganzen Wert …“ zum Text, und die `PrintWindow`-Bilder zeigten ihn ohne jede weitere Eingabe – auch, als ein eigenes Fenster die App während des Ladens verdeckte. Ebenso zeigte ein FK-Sprung (Zellmenü → Kunden) die Zielzeile ohne Klick. `document.visibilityState` blieb `visible`. Die Vermutung (Chromium malt im `WebView2CompositionControl` nicht neu, bis eine Eingabe kommt) bleibt offen; es hängt wohl am Rechner des Kollegen (Grafiktreiber, Monitore, Remote-Sitzung). Nächste Schritte: `docs/backlog.md` („Bekannte Fehler und Lücken“).

## Entscheidungen des Nutzers
- Umbruch automatisch an, wenn der Text keine Zeilenumbrüche hat (2026-10-30).

## Nachtrag: Code-Review (2026-10-30)
Eigener Reviewer-Agent über `56f8c466..f77cbafe`, zweiter Durchgang über die Fix-Commits. Entscheidung des Nutzers: „Alle beheben“.
1. (S) Monaco schreibt Zeilenenden und BOM beim Anlegen um, der Dialog nahm diese Kopie als Wert („geändert“ ohne Eingabe, Übernehmen schrieb umgeschriebene Zeilenenden, Datei nicht mehr byte-genau) – **behoben:** Der Editor meldet nur „bearbeitet“ (Monacos Version seit `create`), der Text kommt erst nach einer Bearbeitung und mit BOM (`OnEdit`, `GetEditedTextAsync`). Im E2E: gemischte Zeilenenden und BOM ohne Hinweis, Übernehmen ohne Eingabe ohne Änderung, BOM nach Bearbeiten und Einrücken in der DB genau einmal.
2. (M) U+2028/U+2029 lösten Monacos Abfrage aus, OK entfernte sie – **behoben:** `unusualLineTerminators: 'off'`. Im E2E: keine Abfrage, Zeichen bleibt.
3. (M) Einrücken wuchs ohne Grenze und lief im UI-Thread – **behoben:** stoppt bei 10 MB („Zu groß zum Einrücken“), läuft per `Task.Run`, Knopf gesperrt solange. Test, im E2E mit 400 KB tief verschachteltem JSON.
4. (K) Gültiges JSON über 64 Ebenen hieß ungültig – **behoben:** `MaxDepth` 100 000, Test.
5. (K) Position im Fehlerhinweis in UTF-8-Bytes – **behoben:** Zeichenspalte wie im Editor, Test mit Umlauten.
6. (K) `editorKeys` wich von der Warnliste der Einstellungen ab (Strg+D, Strg+G …) – **behoben:** angeglichen, `DialogEditorKeysTests` hält beide im Gleichschritt.
7. (K) Ganzer Text nach jeder Tipp-Pause über Interop – **behoben** mit 1 (nur „bearbeitet“ und Länge).
- Im E2E dazu gefunden und behoben: JSON mit BOM galt als Text, Einrücken meldete Position 1 (`DetectFormat`/`IndentJson` überspringen das BOM, es bleibt vorn); nach Rückgängig zeigte die Zeichenzahl Monacos Länge.
- Zweiter Durchgang: N1 (nach Einrücken und Rückgängig zeigte die Zahl die eingerückte Länge) und N2 (BOM als unsichtbares Zeichen im Quelltext, jetzt `\uFEFF` bzw. `LobContent.Bom`) behoben; N3 (Regel und Protokoll) mit diesem Nachtrag und `ui-monaco.md`.
- Vorher schon da: Strg+Enter war im LOB-Editor „Filter anwenden“ (galt auch für die Textbox) und tat dort nichts, weil .NET Kürzel hinter einem Dialog ignoriert. Entscheidung des Nutzers: gleich mitnehmen statt Backlog – `ctrl+enter` steht in `editorKeys`, im Dialog-Monaco fügt es eine Zeile darunter ein (im E2E geprüft, im Grid wendet Strg+Enter weiter den Filter an).
