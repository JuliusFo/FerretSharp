# Fix: Neue leere SQL- und LINQ-Tabs am Ende der Tab-Leiste

Branch `fix/new-tabs-at-end` (von `main`), 2026-11-06, unveröffentlicht.

## Anlass
Nutzerfeedback: Neue SQL- und LINQ-Tabs gingen hinter dem aktiven Tab auf (WP-13, WP-17), erwartet wurde ganz rechts – dort stehen die Knöpfe „+ SQL“ und „+ LINQ“.

Entscheidung des Nutzers: „Nur leere Tabs“. Alles andere, was hinter dem aktiven Tab aufgeht (FK-Sprung, Datensatz-Diagramm, Designer, PL/SQL, Synonyme, Grants, Duplizieren), passt so.

## Gebaut
- `ShellState.OpenSql`/`OpenLinq`: ohne Text bzw. Code (Knopf, Tastenkürzel) ans Ende (`AddAtEnd`), mit Inhalt („Im SQL-Editor öffnen“ aus SQL-Vorschau, DDL-Entwurf und Diagramm-Abfrage, Trigger im PL/SQL-Menü, Objektmenü, LINQ aus dem Filter, DDL-Vorschlag des Schemavergleichs in der Zielverbindung) weiter hinter den aktiven Tab. Unterschieden wird nur an `text`/`code` == null.
- `InsertAfterActive` und `AddAtEnd` teilen sich `Insert` (nie zwischen die angehefteten Tabs, WP-34).
- Test: `ShellStateTabsTests.Empty_sql_and_linq_tabs_open_at_the_end_and_ones_with_content_after_the_active_tab` (ersetzt den Test der alten Reihenfolge).

## Nicht geprüft
- Kein E2E. Zu prüfen: „+ SQL“/„+ LINQ“ und die Kürzel bei aktivem Tab in der Mitte, die Leiste scrollt zum neuen Tab ganz rechts.
