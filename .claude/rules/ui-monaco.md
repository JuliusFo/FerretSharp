---
paths:
  - "src/FerretSharp.UI/Components/Sql/MonacoEditor*"
  - "src/FerretSharp.UI/Components/Sql/SqlView*"
  - "src/FerretSharp.UI/Components/Sql/LinqView*"
  - "src/FerretSharp.UI/Components/PlSql/PlSqlSourceView*"
  - "src/FerretSharp.UI/Components/Grid/LobEditorDialog*"
  - "src/FerretSharp.UI/wwwroot/js/monaco.js"
---

# Monaco (ADR 0011)

- Lokal unter `wwwroot/lib/monaco/` (0.57), für LINQ-Konsole, SQL-Editor, PL/SQL-Quelltext und den Text im LOB-Dialog; SQL-Anzeige sonst über den eigenen Highlighter `SqlCode`.
- Monacos eigene Sprachdienste (JSON, CSS, HTML, TS) sind aus dem Build getrimmt – `json` würde einen fehlenden Worker starten. Färbung ohne Dienst als eigene Monarch-Sprache in `monaco.js` (`plsql`, `fs-json` mit `tokenPostfix: '.json'` für die Theme-Farben), registriert über `registerLanguage` beim `create` und beim `setLanguage`.
- Der Text gehört dem Editor und kommt nach 250 ms über `OnChange`: Wer vor einer Entscheidung (Esc, Übernehmen, Speichern) den aktuellen Text braucht, holt ihn selbst. Esc, das Monaco nicht selbst braucht (keine Auswahl, kein Suchfeld), erreicht den `ModalFrame` – kein `stopPropagation` um den Editor.
- **Ein Wert im Editor (LOB-Dialog) nie über Textvergleich:** Monaco schreibt den Text schon beim Anlegen um – eine Art Zeilenende (gemischte und einzelne `\r` werden vereinheitlicht), ein führendes BOM steht außerhalb des Texts. `OnEdit` statt `OnChange` (`reportEdits`: nur „bearbeitet“ und Länge, über Monacos `alternativeVersionId` seit `create`, Rückgängig bis zum Start zählt nicht) und vor einer Entscheidung `GetEditedTextAsync` (null = unverändert → den geladenen Wert nehmen; sonst mit BOM). Text für `SetTextAsync` ohne BOM, Monaco hängt sein eigenes wieder an (Review des LOB-Editors).
- `unusualLineTerminators: 'off'` für alle Editoren: `'prompt'` fragte mit einem englischen Browser-Dialog und entfernte U+2028/U+2029.
- Zeilenende: Ein Text ohne Zeilenumbruch bekommt in Monaco das der Plattform (`\r\n`), auch für eingefügten Text mit `\n`. Wo der Text ein Wert ist (LOB-Dialog), `LineFeed` setzen – ein Text mit `\r\n` behält es.
- Monacos eigene Texte folgen der UI-Sprache: `monaco.js` lädt `vs/nls/lang/de.js` nur bei deutscher Oberfläche (die Datei ist kein AMD-Modul → als normales Script laden).
- Offsets sind UTF-16; Text und Offsets immer aus demselben `getRunContext`.
- Nur-Lesen (PL/SQL-Quelltext): Marker erscheinen nur mit `renderValidationDecorations: 'on'` (Standard `'editable'` blendet sie aus). `executeEdits` (also `SetTextAsync`) tut in einem Nur-Lesen-Editor nichts → neuer Text über einen neuen Editor (`@key` auf die Daten).
- Ein `RevealAsync`, bevor der Editor erzeugt ist, merkt sich `MonacoEditor` und führt es nach `create` aus; `reveal` ruft vorher `layout()`, weil die Ansicht gerade erst sichtbar geworden sein kann.
- Doppelklick: Das doppelte Mouse-down des WPF-Hosts (`ui.md`) machte aus jedem Doppelklick einen Dreifachklick (Zeile statt Wort). `dropRepeatedMouseDown` verwirft es vor Monaco (gleiche Taste, noch kein Mouse-up, < 50 ms); Monaco zählt selbst weiter, der Dreifachklick markiert weiter die Zeile. Neue Monaco-Instanzen nur über `create`, sonst fehlt das.
- Monaco behält seine Tasten: Kürzel, die nur im Grid/Formular gelten, nicht global in `shortcuts.js` registrieren. Globale Kürzel fängt `shortcuts.js` auch hinter einem Dialog ab (F5 lüde sonst die WebView neu); Monacos eigene Tasten (`editorKeys`: Suchen, Ersetzen, Rückgängig, Zwischenablage) bleiben in einem Monaco **in** einem Dialog beim Editor – sonst wäre Ctrl+F („Spalte suchen“) im LOB-Dialog tot.
