# Fix: Neue leere SQL- und LINQ-Tabs am Ende der Tab-Leiste

Branch `fix/new-tabs-at-end` (von `main`), 2026-11-06, unveröffentlicht.

## Anlass
Nutzerfeedback: Neue SQL- und LINQ-Tabs gingen hinter dem aktiven Tab auf (WP-13, WP-17), erwartet wurde ganz rechts – dort stehen die Knöpfe „+ SQL“ und „+ LINQ“.

Entscheidung des Nutzers: „Nur leere Tabs“. Alles andere, was hinter dem aktiven Tab aufgeht (FK-Sprung, Datensatz-Diagramm, Designer, PL/SQL, Synonyme, Grants, Duplizieren), passt so.

## Gebaut
- `ShellState.OpenSql`/`OpenLinq`: ohne Text bzw. Code (Knopf, Tastenkürzel) ans Ende (`AddAtEnd`), mit Inhalt („Im SQL-Editor öffnen“ aus SQL-Vorschau, DDL-Entwurf und Diagramm-Abfrage, Trigger im PL/SQL-Menü, Objektmenü, LINQ aus dem Filter, DDL-Vorschlag des Schemavergleichs in der Zielverbindung) weiter hinter den aktiven Tab. Unterschieden wird nur an `text`/`code` == null.
- `InsertAfterActive` und `AddAtEnd` teilen sich `Insert` (nie zwischen die angehefteten Tabs, WP-34).
- Test: `ShellStateTabsTests.Empty_sql_and_linq_tabs_open_at_the_end_and_ones_with_content_after_the_active_tab` (ersetzt den Test der alten Reihenfolge).

- `dom.js scrollableTabs` (aus dem Review, K1): Wird der letzte Tab aktiv, holt `reveal` auch die Knöpfe dahinter samt ihrem Rand ins Bild; vorher nur den Tab selbst.

## Prüfung
- E2E 2026-11-06 (nach dem Code-Review) mit echter Maus (`SendInput`, Testinstanz 1440×900, eigener Container):
  - Vier Tabellen-Tabs, „AKTE“ (zweiter) per Klick aktiv, Klick auf „+ SQL“: „SQL 1“ öffnet ganz rechts. Dasselbe mit Ctrl+Shift+Q (per CDP): „SQL 2“ ganz rechts.
  - Volle Leiste (19 Tabs, 1144 px), ganz rechts gescrollt, zweimal „+ SQL“ an derselben Stelle geklickt: „SQL 3“ und „SQL 4“ ganz rechts, der Knopf bleibt bei 1081–1136 px stehen, die Leiste am Ende. Log ohne `[ERR]`/`[WRN]`.
- Nicht in der App geprüft: „+ LINQ“ (ohne verknüpftes Projekt kein Knopf; derselbe Weg wie „+ SQL“, Unit-Test) und Tabs mit Inhalt (Unit-Test).

## Nachtrag: Code-Review (2026-11-06)
Ein unabhängiger Reviewer-Agent (`/branch-code-review`), zwei Durchgänge; kein S/M. Entscheidungen des Nutzers: „Alle drei“, danach „Beide“.
- **K1 Volle Leiste: neuer Tab am Ende schob „+ SQL“/„+ LINQ“ aus dem Bild:** behoben. `reveal` scrollte nur, bis der Tab selbst sichtbar war. In einer statischen Seite mit echtem `ferretsharp.css` und `dom.js` nachgestellt (600 px, zehn Tabs, ganz rechts): vorher lagen die Knöpfe bei 610/686 px, danach sichtbar. Kein automatischer Test: `dom.js` braucht das DOM (ADR 0031).
- **K2 `DuplicateTab` baute das Einfügen nach:** behoben, über `Insert`.
- **K3 Kommentar „after the tab it came from“ stimmte nicht für den DDL-Vorschlag des Schemavergleichs** (`OpenSqlIn`, andere Verbindung): Kommentar und Protokoll korrigiert. Das Verhalten bleibt (mit Text hinter dem aktiven Tab).
- **K4 Doku zum K1-Fix:** behoben – `architecture.md` (volle Tab-Leiste), CHANGELOG („after the active tab“), dieser Nachtrag.
- **K5 Letzter Knopf bündig am Rand:** behoben, `reveal` rechnet seinen `margin-right` mit (in der Nachstellung 592 von 600 px wie beim Scrollen von Hand, Leiste ganz am Ende).
- Learnings: keine neue Regel – K1 hing an dieser Änderung, K2–K5 waren Einzelfälle.
