# Klein: Tippen im Enum-Editor, AltGr-Zeichen starten die Bearbeitung → nächstes Release
Anlass (2026-10-22): Rückmeldung eines Kollegen über den Nutzer: (1) Eingaben gingen nur per Doppelklick, Lostippen auf der fokussierten Zelle nicht; (2) Enum-Werte aus dem C#-Modell ließen sich nicht als Zahl eingeben (`0` für den ersten Member), nur über die Liste. Branch `fix/enum-editor-typing`. Entscheidung des Nutzers: „Variante A“ – Tippen im vorhandenen Editor, kein neues Eingabefeld mit gefilterter Liste.

Befund (nachgestellt mit dem echten `grid.js` und AG Grid 36.2 auf einer Testseite, CDP-Tastendrücke, Stand `main` und `wp/58-cell-selection`):
- Text- und Zahlspalten: Tippen startet die Bearbeitung und ersetzt den Wert, Enter und F2 öffnen mit dem vorhandenen Wert. Punkt (1) stimmt dort nicht.
- Enum- und Bool-Spalten (`FerretSelectEditor`): Die erste Taste öffnete nur die Liste und ging verloren (`params.eventKey` blieb unbeachtet). Danach suchte die native Liste nur am Anfang der Beschriftung (`Neu (0)`), Ziffern fanden nie etwas. Punkt (2) stimmt, und für diese Spalten auch (1).
- AltGr-Zeichen: Windows meldet AltGr als Ctrl+Alt, AG Grid startet bei Ctrl, Alt oder Meta keine Bearbeitung (`altKey||ctrlKey||metaKey ? false : key.length === 1` in der Lib). Auf deutscher Tastatur starteten `@ € \ { } [ ] ~ |` deshalb nichts.

Gebaut:
- **`cellTyping.js`** (rein, Tests in `tests/js/cellTyping.test.mjs`, ADR 0031): `typedCharOf` (druckbare Taste oder AltGr-Zeichen; Ctrl+Alt mit Buchstabe oder Ziffer bleibt ein Kürzel), `pickTyped` (getippter Text → Wert eines Members).
- **Reihenfolge der Suche:** Wert genau, Wert ohne Groß/Klein, Wert beginnt mit dem Text (`1` vor `10`), dann Beschriftung beginnt mit dem Text. NULL zählt bei den Beschriftungen zuletzt (`n` → `Neu`, `nu` → NULL). Ein einzelner Buchstabe oder derselbe noch einmal (`nnn`) geht zum nächsten Eintrag mit diesem Buchstaben, wie in den Listen von Windows. Eine Pause von 1 s beginnt neu, mehrere Ziffern hintereinander ergeben mehrstellige Werte.
- **Enum-Editor:** Die Taste, die die Bearbeitung startet, wählt wie getippt (vom ersten Eintrag an); weitere Zeichen wählen im offenen Editor, die native Suche der Liste ist dabei aus. Ein Leerzeichen gehört nur mitten im Tippen zum Text, allein öffnet es die Liste wie bisher. Enter übernimmt wie bisher; geprüft und geschrieben wird in .NET wie getippter Text.
- **AltGr auf einer Zelle** (`onAltGrKey`): startet die Bearbeitung mit dem Zeichen (`startEditingCell` mit `key`), nur auf einer fokussierten, bearbeitbaren Zelle, nicht auf LOB-Zellen.

Bewusst nicht:
- Flags-Editor: Tippen setzt dort weiter keine Häkchen (Backlog).
- Tasten, die getippt werden, bevor der Zellwert aus .NET da ist, gehen weiter verloren (Backlog, „Grid-Editor“); im Enum-Editor zählt nur die erste.
- Tottasten (`^ ´ \``) starten die Bearbeitung erst mit dem folgenden Zeichen, der Akzent fehlt dann.

E2E: `grid.js` mit AG Grid und einem Fake-.NET auf einer Testseite im eingebauten Browser, CDP-Eingabe (Stufe 2): `0` auf dem Member mit 2 wählt den mit 0, Enter schreibt `0`; `1` `0` schnell hintereinander → `10`; nach einer Pause `n` dreimal → nächster Eintrag mit N, NULL, wieder der erste mit N; zwei Anfangsbuchstaben → der Member mit diesem Anfang. AltGr als synthetisches Ctrl+Alt+`@` öffnet den Texteditor mit `@`, Ctrl+Alt+`q` nichts. Nicht geprüft: in der App selbst und damit AltGr mit echter Tastatur durch den WPF-Host.
