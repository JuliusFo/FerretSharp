# ADR 0051: Hilfe in der App – am Ort und als Panel neben der Ansicht

- Status: akzeptiert
- Datum: 2026-11-13
- Paket: WP-89 (`docs/work-packages/wp-89-in-app-help.md`)

## Kontext

FerretSharp hatte bisher nirgends eine Hilfe. Anlass war ein `-- @target`, das im SQL-Editor getippt nie wirkte (`fix-script-header-in-editor.md`): Der Nutzer konnte nicht sehen, was der Kopf eines Skripts kann, wo er stehen muss und warum eine Zeile nichts tut. Die interne Doku (`docs/sql-scripts.md`) ist für Entwickler geschrieben und auf Deutsch; die App ist zweisprachig.

## Entscheidung (Plan vom Nutzer bestätigt, 2026-11-13)

- **Hilfe am Ort zuerst.** Wo etwas getippt wird, erklärt der Editor es selbst: Completion nach `-- @` (Kopfzeilen, Typen von `@param`, `list`, Binds des SQL, Tabellen und Spalten für `@target`), Hover über einer Kopfzeile (was sie tut; bei `@target`, ob die Verbindung Tabelle und Spalte hat und in welchen Zellmenüs das Skript erscheint) und Marker beim Tippen statt erst nach dem Speichern.
- **Alles Wissen in .NET** (`Core/Scripts/ScriptHeaderHelp`): Prüfungen, Texte und Vorschläge. Die Marker nehmen dieselben Regeln wie das Speichern (`ScriptHeaders.ForSave`, Fehler mit Zeile), damit Editor und Speichern nie auseinanderlaufen. `monaco.js` bekommt nur einen Hover-Provider, `@` als Auslöser (nur für `sql`), Vorschläge auch in Kommentaren (`quickSuggestions.comments`, nur der SQL-Editor) und Marker je Besitzer – keine Logik, keine Texte.
- **Hilfeseite als Panel neben der Ansicht,** wie *Skripte* und *Verlauf* im SQL-Tab: Die Hilfe wird gebraucht, während getippt wird; eine eigene Seite verdeckte den Tab, ein Dialog sperrte den Editor. Erreichbar über den Knopf in der Leiste des SQL-Tabs, die Kopf-Meldung nach dem Speichern, den Speichern-Dialog (der dafür schließt), Explorer › *Skripte* und die Befehlspalette (Aktion `ScriptHelp`, ohne Standardtaste). Ohne aktiven SQL-Tab öffnet ein neuer.
- **Ein Panel, viele Themen:** `HelpPanel` nimmt ein `HelpTopic`; ein weiteres Thema ist ein weiterer Inhalt (Komponente plus Texte), kein neues Panel. In WP-89 nur *SQL-Skripte*; Filter, Audit-Vorlagen und Tastenkürzel stehen im Backlog.
- **Eigene Texte für Nutzer** in `HelpText` (UI, Englisch und Deutsch), keine Kopie der internen Doku. Code (Kopfzeilen, Beispiel) bleibt unübersetzt (`localization.md`).

## Folgen

- Hover-Texte sind Markdown und werden **untrusted** gezeigt (kein HTML, keine Command-Links); Namen aus dem Schema oder dem Text stehen nur in Code-Spans, deren Backticks ersetzt werden.
- Die `@target`-Prüfung liest Tabellenstrukturen über den Schema-Cache (einmal je Tabelle, dann aus dem Cache); ein Dictionary-Fehler lässt die Schema-Marker weg, statt das Tippen zu stören. Eine fehlende Tabelle ist nur ein Hinweis – das Skript kann für ein anderes Produkt sein (WP-84); eine fehlende Spalte einer vorhandenen Tabelle ist eine Warnung.
- „Erscheint im Zellmenü von …“ rechnet rückwärts über dieselben Beziehungen und Schritte wie das Zellmenü (`ScriptTargets.ReachedBy` ↔ `Reached`); ein Test hält fest, dass beide übereinstimmen.
- Die Zeile über dem Ergebnis, die Chips und der Verlauf zeigen das Statement ohne führende Kopfzeilen (Entscheidung des Nutzers); ausgeführt wird unverändert, damit Offsets von Markern und Fehlern stimmen.
