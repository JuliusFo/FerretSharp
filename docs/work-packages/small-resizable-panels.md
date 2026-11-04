# Klein: Explorer und Editoren in der Größe ziehen → Release offen

Branch `feat/resizable-panels` (von `main`), 2026-11-04.

## Anlass
Die Breite der Änderungsübersicht ließ sich ziehen, die des Explorers und die Höhe von SQL-Editor bzw. Ergebnis nicht. Wunsch des Nutzers: beides ziehbar machen.

Ohne Rückfrage entschieden (einfachste Variante, Fragen unten):
- **Eine Größe für die ganze App**, in den Einstellungen wie `ChangesPanelWidth`: `AppSettings.ExplorerWidth` (gilt für den Explorer jeder Verbindung), `SqlEditorHeight` (jeder SQL-Tab), `LinqEditorHeight` (Abfrage-Editor jeder LINQ-Konsole). Nicht je Tab wie die Formularbreite: Das brächte die Höhe ins Workspace-JSON.
- **Die LINQ-Konsole mit dabei:** Sie ist gebaut wie der SQL-Editor; ohne Griff verhielte sie sich anders. Gezogen wird nur der Abfrage-Editor, das Variablen-Feld bleibt 62 px.
- **Doppelklick stellt den Standard her**, an allen vier Griffen (auch Formular und Änderungsübersicht): Die Einstellung bzw. `Tab.FormWidth` wird null, der Standard steht wieder im CSS bzw. in der Komponente.
- Grenzen: Explorer 200 px bis 50 % von `.body` (Standard 280 px), SQL-Editor 80 px bis 75 % der Ansicht, LINQ-Editor 60 px bis 60 %. Im CSS bleibt zur Sicherheit `max-height: 70vh` bzw. `max-width: 50%`, falls das Fenster nach dem Ziehen kleiner wird.

## Gebaut
- `ResizeHandle` (Common) für alle vier Griffe: `Edge` (links/rechts/unten), `Min`, `MaxFraction`, wahlweise `Variable` (CSS-Variable statt eigener Größe), `Measure`/`Within` (Selektoren für das gezogene Element und die Grenze); meldet `OnResized` und `OnReset`. Doppelklick über `@onclick` mit `Detail >= 2` (kein `dblclick` im WPF-Host).
- `dom.js`: `dragWidth` → `dragSize` (Breite oder Höhe, Rand links/rechts/unten) auf `trackDrag` (Ende wie bisher auch bei verlorenem Capture, A27). **Neu: erst ab 3 px Bewegung wird gezogen, sonst kommt 0 zurück** – ein Klick, auch jede Hälfte eines Doppelklicks, speichert keine Größe und kommt dem Zurücksetzen nicht in die Quere. `boxParent` überspringt `display: contents` (`.scope-explorer`, um jeden Explorer).
- Explorer, SQL- und LINQ-Editor: Die Größe steht als CSS-Variable (`--explorer-width`, `--sql-editor-height`, `--linq-editor-height`, `PanelSizes`) im `style` von `.app`. Die Shell rendert sie aus den Einstellungen und rendert bei `Settings.Changed` nur neu, wenn sich eine Größe geändert hat. Beim Ziehen setzt `dragSize` dieselbe Variable an `.app`. Damit folgen die Explorer aller Verbindungen und alle Editor-Tabs, ohne selbst zu rendern. Beim Zurücksetzen nimmt Blazor das ganze `style`-Attribut zurück, auch den Wert aus JS.
- Monaco folgt der Höhe von selbst (`automaticLayout`).
- Formular und Änderungsübersicht auf `ResizeHandle` umgestellt. `GridText.Form_ResizeWidth` ist ersetzt durch `CommonText.Resize_Width`/`Resize_Height` (mit Hinweis auf den Doppelklick).

## Prüfung
- E2E 2026-11-04 mit echter Maus (`SendInput`, Testinstanz 1440×900, eigener Container), Ereignisse per CDP mitgeschnitten:
  - Explorer: Der Griff liegt bis 3 px über dem Inhalt rechts daneben oben. Ziehen auf 461 px, Grenze bei 712 px (50 % von 1424), Minimum 200 px. Eine zweite Verbindung öffnet mit derselben Breite, und nach dem Ziehen dort hat auch die erste die neue Breite.
  - SQL-Editor: Monaco folgt (Container 452, Layout 450), Grenze 560 px (75 % von 747), Minimum 80 px, auch mit offenem Verlauf. LINQ-Editor: folgt, Grenze 448 px (60 % von 747), das Variablen-Feld bleibt 62 px.
  - Doppelklick an allen vier Griffen: Standard wieder da (280 px, 30vh, Formular 440, Änderungsübersicht 380), die Einstellung ist entfernt. Der WPF-Host schickt wie erwartet zweimal `mousedown` (`detail` 2 und 3); dabei wird keine Größe gespeichert. Auch der `click` nach jedem Ziehen speichert nichts.
  - Neustart: Explorer, SQL, LINQ, Änderungsübersicht (Einstellungen) und Formular (Workspace) haben dieselben Größen. Log ohne `[ERR]`/`[WRN]`.
- **Gefunden und behoben:** Die Verlaufsspalte war höchstens `clamp(180px, 42vh, 560px)` hoch (`.sql-history`). Ist der Editor höher (bis 75 %), endete ihr Rand neben dem Editor. Mit vielen Einträgen wäre der Bereich bei kleinem Editor dagegen auf 42vh gewachsen. Entscheidung des Nutzers: Die Spalte ist immer so hoch wie der Editor, die Liste scrollt. Umgesetzt mit `contain: size` statt `max-height`: Die Spalte trägt nichts zur Höhe bei und wird auf die Höhe der Zeile gestreckt. Im E2E mit 30 Einträgen geprüft: 352, 560 und 80 px Editor, die Spalte jeweils gleich hoch. Bei 80 px bleiben das Suchfeld und ein Eintrag sichtbar.

## Fragen an den Nutzer
- Reicht eine Größe für alle Tabs, oder soll die Editorhöhe je Tab gelten (wie die Formularbreite)?
