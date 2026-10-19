# Klein: Verweise über Basistabellen im FK-Sprung

Branch `feat/fk-jumps-base-tables` (von `main`), 2026-10-19. Noch nicht released.

## Anlass
Frage des Nutzers: Wird „Referenziert von“ im Kontextmenü bei Tabellen mit vielen Verweisen abgeschnitten? Am Screenshot geklärt: nein, das Menü zeigte alles. Gemeint war ein fehlender Verweis bei Basis- und Erweiterungstabellen mit gemeinsamem Primärschlüssel (Table per Type). Beispiel hier und in den Tests (neutral, nicht das Schema des Nutzers): `FAHRZEUG` (PK `FAHRZEUG_ID`), `PKW` und `LKW` mit PK `FAHRZEUG_ID` und FK auf `FAHRZEUG.FAHRZEUG_ID`; `WARTUNG` verweist auf `FAHRZEUG.FAHRZEUG_ID`. In einer `PKW`-Zeile fehlte `WARTUNG`, obwohl der Verweis fachlich dieselbe Zeile meint.

Entscheidungen des Nutzers:
- Die Verweise der Basistabelle erscheinen auch in der Erweiterungstabelle.
- Die anderen Erweiterungstabellen derselben Basis (Geschwister, z. B. `LKW` in einer `PKW`-Zeile) werden **ausgeblendet**: Ein Datensatz hat im Normalfall nur eine Ausprägung, der Eintrag stünde fast immer mit 0 da.

## Gebaut
- **Erkennung** (`Core/Schema/BaseTables`): Eine Beziehung, deren Spalten genau der Primärschlüssel ihrer Tabelle sind, macht die Tabelle zur Erweiterung der referenzierten (`IsExtension`). Gilt für jede FK-Quelle, also auch für 1:1-Beziehungen aus dem C#-Modell ohne Constraint. `ReferencesOf` geht die Basistabellen nach oben (auch mehrere Stufen, bereits besuchte beenden den Weg) und liefert je eingehender Beziehung der Basis ein `InheritedReference`: die Beziehung, die Basistabelle (`Via`) und die Spalten der eigenen Tabelle, die den referenzierten Schlüssel tragen (Spalten über die FK-Paare umbenannt, auch mehrspaltig). Ausgelassen: Beziehungen, die selbst Erweiterungen sind (Geschwister und die eigene), und Beziehungen auf einen anderen Unique Key der Basis (dessen Wert steht nicht in der Zeile). Selbstbezüge der Basis (`FAHRZEUG.ZUG_ID → FAHRZEUG`) bleiben drin.
- **Primärschlüssel aller Tabellen** lädt der Schema-Cache beim Verbinden mit (`ISchemaReader.GetPrimaryKeysAsync`, eine Abfrage je beteiligtem Schema neben den FKs, auch deaktivierte PKs). Ohne sie ließen sich Geschwister nicht erkennen; die Details einer Tabelle lädt der Cache erst beim Öffnen. Kostet einen Roundtrip je Schema beim Verbinden. `SchemaCache.PrimaryKeyOf`, `InheritedIncomingOf`.
- **Sprünge** (`FkNavigation`): `FkJump.Inherited` und `RowColumns` (aus welchen Spalten der Zeile der Sprung seine Werte nimmt; die Markierung „über die geklickte Spalte“ nutzt sie jetzt in Grid und SQL-Ergebnis). `JumpsFor` hängt die geerbten Sprünge hinter die direkten, je Basistabelle gruppiert, nächste Basis zuerst. Zählung und Sprung laufen unverändert (Filter auf die FK-Spalten der referenzierenden Tabelle).
- **UI**: Zellmenü im Grid, Menü des SQL-Ergebnisses und Formular zeigen unter „Referenziert von“ eine eigene Gruppe „Referenziert über FAHRZEUG“ je Basistabelle, Tooltip der Überschrift erklärt den gemeinsamen Primärschlüssel (`FkJumpLists.Groups`). Die Schätzung der Menühöhe zählt die zusätzlichen Überschriften.

## E2E
Eigener Container mit Basistabelle, zwei Erweiterungen, einer Tabelle, die auf die Basis verweist, einer, die auf eine Erweiterung verweist, und einem Selbstbezug der Basis; App mit `--data-dir`, Klicks und Rechtsklicks per CDP. Zellmenü, Formular und SQL-Ergebnis zeigen in einer Zeile der Erweiterung die Gruppe „Referenziert über …“ mit Selbstbezug und Verweis, ohne die zweite Erweiterung; Zählungen stimmen, der Sprung öffnet die verweisende Tabelle gefiltert. Log ohne `[ERR]`.
- Nicht per E2E: Rechtsklick mit echter Windows-Eingabe (der Weg zum Menü ist unverändert, nur sein Inhalt neu), mehrere Stufen und Beziehungen aus dem C#-Modell (Unit-Tests).

## Offen
- „Verweist auf“ der Basistabelle (z. B. `FAHRZEUG.HERSTELLER_ID`) fehlt in der Erweiterung weiterhin: Die Werte stehen nur in der Basiszeile, es bräuchte eine Abfrage. Im Backlog.
