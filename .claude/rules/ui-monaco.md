---
paths:
  - "src/FerretSharp.UI/Components/Sql/MonacoEditor*"
  - "src/FerretSharp.UI/Components/Sql/SqlView*"
  - "src/FerretSharp.UI/Components/Sql/LinqView*"
  - "src/FerretSharp.UI/Components/PlSql/PlSqlSourceView*"
  - "src/FerretSharp.UI/Components/Grid/LobEditorDialog*"
  - "src/FerretSharp.UI/wwwroot/js/monaco.js"
  - "src/FerretSharp.UI/Components/Filter/ConditionBar*"
---

# Monaco (ADR 0011)

- Lokal unter `wwwroot/lib/monaco/` (0.57), für LINQ-Konsole, SQL-Editor, PL/SQL-Quelltext und den Text im LOB-Dialog; SQL-Anzeige sonst über den eigenen Highlighter `SqlCode`.
- Monacos eigene Sprachdienste (JSON, CSS, HTML, TS) sind aus dem Build getrimmt – `json` würde einen fehlenden Worker starten. Färbung ohne Dienst als eigene Monarch-Sprache in `monaco.js` (`plsql`, `fs-json` mit `tokenPostfix: '.json'` für die Theme-Farben), registriert über `registerLanguage` beim `create` und beim `setLanguage`.
- Der Text gehört dem Editor und kommt nach 250 ms über `OnChange`: Wer vor einer Entscheidung (Esc, Übernehmen, Speichern) den aktuellen Text braucht, holt ihn selbst; eine Entscheidung ohne Rundreise (Tab schließen) fragt `shortcuts.js typing()`, das auch Editoren mit noch nicht gemeldeten Tasten kennt (Review WP-77: sofort geschlossen, war das Getippte weg). Esc, das Monaco nicht selbst braucht (keine Auswahl, kein Suchfeld), erreicht den `ModalFrame` – kein `stopPropagation` um den Editor.
- Nach einem `await` (Datei schreiben, lesen) den Editortext nur ersetzen, wenn der Editor noch den Text von vor dem `await` hält (`ScriptSave.TextFor`, `ReloadAsync` mit `quietlyOver`) – sonst ist, was der Nutzer währenddessen tippte, still weg; dasselbe für Variablen: nur ändern, was die Aktion ändert, keinen Schnappschuss zurückschreiben (Review fix-script-header-in-editor).
- **Ein Wert im Editor (LOB-Dialog) nie über Textvergleich:** Monaco schreibt den Text schon beim Anlegen um – eine Art Zeilenende (gemischte und einzelne `\r` werden vereinheitlicht), ein führendes BOM steht außerhalb des Texts. `OnEdit` statt `OnChange` (`reportEdits`: nur „bearbeitet“ und Länge, über Monacos `alternativeVersionId` seit `create`, Rückgängig bis zum Start zählt nicht) und vor einer Entscheidung `GetEditedTextAsync` (null = unverändert → den geladenen Wert nehmen; sonst mit BOM). Text für `SetTextAsync` ohne BOM, Monaco hängt sein eigenes wieder an (Review des LOB-Editors).
- `unusualLineTerminators: 'off'` für alle Editoren: `'prompt'` fragte mit einem englischen Browser-Dialog und entfernte U+2028/U+2029.
- Zeilenende: Ein Text ohne Zeilenumbruch bekommt in Monaco das der Plattform (`\r\n`), auch für eingefügten Text mit `\n`. Wo der Text ein Wert ist (LOB-Dialog), `LineFeed` setzen – ein Text mit `\r\n` behält es.
- Monacos eigene Texte folgen der UI-Sprache: `monaco.js` lädt `vs/nls/lang/de.js` nur bei deutscher Oberfläche (die Datei ist kein AMD-Modul → als normales Script laden).
- Offsets sind UTF-16; Text und Offsets immer aus demselben `getRunContext`.
- Nur-Lesen (PL/SQL-Quelltext): Marker erscheinen nur mit `renderValidationDecorations: 'on'` (Standard `'editable'` blendet sie aus). `executeEdits` (also `SetTextAsync`) tut in einem Nur-Lesen-Editor nichts → neuer Text über einen neuen Editor (`@key` auf die Daten).
- Ein `RevealAsync`, bevor der Editor erzeugt ist, merkt sich `MonacoEditor` und führt es nach `create` aus; `reveal` ruft vorher `layout()`, weil die Ansicht gerade erst sichtbar geworden sein kann.
- Doppelklick: Das doppelte Mouse-down des WPF-Hosts (`ui.md`) machte aus jedem Doppelklick einen Dreifachklick (Zeile statt Wort). `dropRepeatedMouseDown` verwirft es vor Monaco (gleiche Taste, noch kein Mouse-up, < 50 ms); Monaco zählt selbst weiter, der Dreifachklick markiert weiter die Zeile. Neue Monaco-Instanzen nur über `create`, sonst fehlt das.
- Das Kontextmenü liegt in einer Shadow Root, deren Host Monaco **neben** den Editor in `.monaco-host` hängt – außerhalb von `.monaco-editor`, wo die Theme-Farben (`--vscode-*`) gesetzt sind. `.monaco-host` trägt deshalb `monaco-component` (sonst Menü ohne Hintergrund und Trenner, `fix-script-editor-menu.md`). Eigene Einträge über `ContextActions` (`addAction`, Gruppe `0_ferret`); sie laufen im JS-Callback, der Besitzer rendert danach selbst.
- Monaco behält seine Tasten: Kürzel, die nur im Grid/Formular gelten, nicht global in `shortcuts.js` registrieren. Globale Kürzel fängt `shortcuts.js` auch hinter einem Dialog ab (F5 lüde sonst die WebView neu); Monacos eigene Tasten (`editorKeys`: Suchen, Ersetzen, Rückgängig, Zwischenablage) bleiben in einem Monaco **in** einem Dialog beim Editor – sonst wäre Ctrl+F („Spalte suchen“) im LOB-Dialog tot.
- Ein einzeiliges Feld (`Field`, WP-79, `makeField` in `monaco.js`): Höhe = `getContentHeight()` + Rahmen (border-box), Abstand über Monacos `padding`, nicht über CSS am Host. Enter bringt den Text mit (`OnFieldEnter`) – die Entprellung hielt ihn sonst noch 250 ms zurück.
- **`editor.addCommand` bindet die Taste für alle Editoren** (gemeinsamer Keybinding-Service, die zuletzt registrierte gewinnt, nichts zu entsorgen): Eine Taste nur für einen Editor braucht einen eigenen Context-Key dieses Editors in der `when`-Bedingung (`createContextKey`, Name je Editor eindeutig). Ohne ihn wendete Enter im SQL-Editor die Bedingung des zuletzt erzeugten Felds an (Review WP-79).
- Text in ein SQL-Skript einfügen: `SqlScript.Insert` am Cursor beendet den Text davor mit `;` – steht der Cursor in einem Statement, zerteilt es das. Erst `SqlScript.InsertionPoint` (hinter das Statement unter dem Cursor), dann `Insert` (Review WP-81).
