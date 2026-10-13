---
paths:
  - "src/FerretSharp.UI/Components/MonacoEditor*"
  - "src/FerretSharp.UI/Components/SqlView*"
  - "src/FerretSharp.UI/Components/LinqView*"
  - "src/FerretSharp.UI/Components/PlSqlSourceView*"
  - "src/FerretSharp.UI/wwwroot/js/monaco.js"
---

# Monaco (ADR 0011)

- Lokal unter `wwwroot/lib/monaco/` (0.57), für LINQ-Konsole, SQL-Editor und PL/SQL-Quelltext; SQL-Anzeige sonst über den eigenen Highlighter `SqlCode`.
- Monacos eigene Texte folgen der UI-Sprache: `monaco.js` lädt `vs/nls/lang/de.js` nur bei deutscher Oberfläche (die Datei ist kein AMD-Modul → als normales Script laden).
- Offsets sind UTF-16; Text und Offsets immer aus demselben `getRunContext`.
- Nur-Lesen (PL/SQL-Quelltext): Marker erscheinen nur mit `renderValidationDecorations: 'on'` (Standard `'editable'` blendet sie aus). `executeEdits` (also `SetTextAsync`) tut in einem Nur-Lesen-Editor nichts → neuer Text über einen neuen Editor (`@key` auf die Daten).
- Ein `RevealAsync`, bevor der Editor erzeugt ist, merkt sich `MonacoEditor` und führt es nach `create` aus; `reveal` ruft vorher `layout()`, weil die Ansicht gerade erst sichtbar geworden sein kann.
- Monaco behält seine Tasten: Kürzel, die nur im Grid/Formular gelten, nicht global in `shortcuts.js` registrieren.
