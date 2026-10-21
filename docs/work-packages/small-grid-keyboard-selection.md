# Klein: Zeilenauswahl folgt dem Fokus → Release offen
Anlass (2026-10-21): Bugreport eines Kollegen – Entf auf einer neuen Zeile löschte auch die zuletzt angeklickte geladene Zeile (Fix in PR #28, `fix/delete-new-row-spares-selection`). Ursache dort: AG Grid wählt angeheftete Zeilen nie aus, und die Auswahl blieb stehen, während der Fokus wanderte. Auswahl und Fokus liefen auch mit den Pfeiltasten auseinander: Entf, Ctrl+C und Alt+Enter trafen die Zeilen des letzten Klicks, nicht die sichtbar fokussierte. Branch `feat/grid-keyboard-selection`.

Entscheidungen des Nutzers:
- Die Auswahl folgt dem Fokus wie im Windows Explorer; Shift+Pfeiltasten erweitern sie.
- Eine Ctrl+Klick-Mehrfachauswahl fällt beim nächsten Pfeil weg (Explorer-Verhalten), statt bis zum nächsten Klick zu bleiben.

Gebaut (nur `grid.js` und der Schalter `select` an `FerretGrid.FocusRowAsync`):
- ↑ ↓, Bild↑/↓, Tab über das Zeilenende, Ctrl+↑/↓: nur die neue Zeile ist ausgewählt. ← → in derselben Zeile ändern nichts.
- Shift+↑ ↓ und Shift+Bild↑/↓: Bereich vom Anker bis zum Fokus, schrumpft auf dem Rückweg. Anker ist der letzte Klick ohne Shift (auch Rechtsklick, der neu auswählt) oder der letzte Schritt ohne Shift; ist der Anker nicht mehr ausgewählt (neu geladen, sortiert, per Ctrl+Klick abgewählt), beginnt der Bereich an der zuvor fokussierten Zeile. Shift+Ctrl+Ende nicht: Für nicht geladene Blöcke gibt es keine Zeilen; Zeilen eines Blocks, der noch lädt, bleiben im Bereich aus.
- Fokus auf einer neuen Zeile oben (Klick, Pfeil, neue Zeile anlegen) leert die Auswahl.
- ▲ ▼ des Formulars und der Sprung aus der Änderungsübersicht wählen die Zeile mit; Formular öffnen und schließen lassen die Auswahl stehen (eine Mehrfachauswahl für Export oder Vergleich bleibt).
- Klicks bleiben AG Grids eigene Auswahl.
- Einstellungen › Tastenkürzel listet Shift+↑/↓ unter den festen Tasten.

Fallen:
- AG Grid ruft `onCellFocused` asynchron auf, nach dem `keyup` (Pfeil: wenige ms, Bild↓ nach dem Scrollen ~10 ms). Die Taste gilt deshalb bis 250 ms nach dem Loslassen; eine Maustaste oder ein Fokuswechsel aus .NET (`focusRow`, `startEditing`, `jumpToColumn`) verwirft sie – sonst würde ein .NET-Fokuswechsel kurz nach Shift+↓ den Bereich erweitern (im Test gefunden). Den Merker nicht beim ersten Fokus-Ereignis verbrauchen: Bei gehaltener Taste stehen mehrere Ereignisse an, das zweite fände ihn sonst leer.
- Die Zeilen des Grids liegen in AG Grid 36 in `.ag-grid-scrolling-container`, nicht mehr in `.ag-center-cols-container`; weggescrollte Zeilen fehlen im DOM – Auswahl im Test über `agGrid.getGridApi(element).getSelectedNodes()` prüfen.

E2E: `grid.js` mit AG Grid und einem Fake-.NET auf einer Testseite im eingebauten Browser (CDP-Eingabe, Stufe 2): Pfeile, Shift-Bereich hin und zurück, ← →, Bild↑/↓ mit Shift, Ctrl+↑, Tab am Zeilenende, Ctrl- und Shift-Klick mit anschließendem Pfeil, Klick und Pfeil in neue Zeilen, `focusRow` mit und ohne `select`, schnelles Wiederholen. Nicht geprüft: in der App selbst (Tastatur durch den WPF-Host, ▲ ▼ des Formulars, Sprung aus der Änderungsübersicht).
