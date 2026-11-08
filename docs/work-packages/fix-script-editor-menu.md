# Fix: Rechtsklick-Menü der Editoren, Speichern der Auswahl, Beschreibungsfeld

Branch `fix/script-editor-menu` (von `main`), 2026-11-08, nach Release 3.65.0 (WP-77).

## Anlass
Nutzerfeedback zu den SQL-Skripten (mit Screenshots):
1. Im Dialog „Als Skript speichern“ stand der Text im Feld „Beschreibung“ nicht mittig.
2. Das Rechtsklick-Menü auf eine Markierung im SQL-Editor hatte keinen Befehl zum Speichern und sah „sehr komisch“ aus.

## Ursachen
- **Beschreibung:** Das `<textarea>` trug die Klasse `field` (Höhe 30px, Innenabstand oben/unten 0) – eine Zeile hoch, Text an der Oberkante. Der Schrift-Reset `button, input, select { font: inherit }` ließ `textarea` aus, das Feld zeigte die Monospace-Schrift des Browsers.
- **Menü:** Monaco 0.57 legt das Kontextmenü in eine Shadow Root, deren Host (`.shadow-root-host`) es **neben** den Editor in unser `.monaco-host` hängt. Die Theme-Farben (`--vscode-menu-background` usw.) setzt Monaco nur auf `.monaco-editor, .monaco-diff-editor, .monaco-component`; Variablen erben zwar in die Shadow Root, der Host lag aber außerhalb. Folge: kein Hintergrund, kein Rahmen, keine Trennlinien – in allen vier Editoren (SQL, LINQ, PL/SQL, LOB).
- **Speichern:** Das Menü kannte nur Monacos eigene Einträge.

## Gebaut
- `MonacoEditor`: Host trägt `monaco-component` (im Markup, damit Blazor die Klasse nie überschreibt). Neuer Parameter `ContextActions` (`EditorAction(Id, Label, Run)`), in `monaco.js` per `editor.addAction` in die Gruppe `0_ferret` (vor Monacos Gruppen); die Wahl kommt über `OnContextAction` zurück und läuft in `GuardAsync`.
- `SqlView`: Eintrag „Auswahl als Skript speichern…“ (`ScriptViewText.Menu_SaveSelection`, derselbe Weg wie im Menü neben „Speichern“; ohne Auswahl das Statement am Cursor). Er läuft im JS-Callback und rendert die Ansicht danach selbst (`Rendering`), sonst erschiene der Dialog erst beim nächsten Rendern.
- CSS: `textarea.field` (Höhe nach Zeilen, Innenabstand 5px 8px, `resize: vertical`), `textarea` im Schrift-Reset. Das Formular-Textfeld (`.ff-input`, 12,5px) behält seine Größe, die Klasse gewinnt gegen den Element-Selektor.

## Geprüft
- Testinstanz (dunkel, Deutsch) gegen eigenen Container: Menü mit Hintergrund, Trennern und Schatten; Eintrag oben; echter Windows-Klick auf den Eintrag öffnet den Dialog „Auswahl als Skript speichern“ sofort; gespeicherte Datei enthält nur die markierte Zeile. Beschreibungsfeld zweizeilig in App-Schrift.
- Nicht geprüft: helles Theme (die Farben kommen aus Monacos Theme wie beim Editor selbst), LINQ/PL/SQL/LOB-Menü einzeln (gleicher Host, gleiche Klasse).

## Offen
- Das Menü nimmt Monacos Farben (`#3c3c3c` im Dunkelmodus), nicht die Tokens der App (`PopoverMenu`). Angleichen ginge über die Farben des Monaco-Themes – Frage an den Nutzer.
- Weitere Einträge im Rechtsklick (z. B. „Ausführen“) – nicht gewünscht, nur bei Bedarf.
