# ADR 0010: Das C#-Modell in der Oberfläche – Präsentationsschicht, Wertetabellen, Modell-Beziehungen

- Status: akzeptiert
- Datum: 2026-10-05

## Kontext

WP-11 hat das EF-Core-Modell geladen und als Annotation-Schicht (`ClrModelMapping`) neben das Schema gelegt. WP-12 bringt es in die Oberfläche: Entity- und Property-Namen, Enums und konvertierte Bools mit ihren C#-Namen, Navigations als Beziehungen. Vorgaben aus CLAUDE.md (Abschnitt 2): Schema-Records bleiben reine DB-Sicht, Zellwerte und Spaltenbeschriftungen laufen über einen austauschbaren Präsentations-Service, FK-Quellen sind ein Enum, das Filtermodell bleibt 1:1 in LINQ übersetzbar.

Entscheidungen des Nutzers: Oracle-Name vorne, C#-Name dezent daneben, umschaltbar „aus · daneben · vorne“; Enums als „Gewerbe (2)“, Bools mit Converter als `true`/`false`; Filter und Editieren über Auswahllisten; Modell-Beziehungen ohne Constraint sind häufig und gleichwertig zu deklarierten FKs.

## Entscheidung

- **`TablePresentation`** (Core, `ClrModel/`) ist die Präsentationsschicht pro Tabelle: Beschriftung (`ColumnLabel`: Hauptname, anderer Name, C#-Typ), Zellanzeige (`Present`, sonst `CellFormatter`), Wertoptionen, Entity-Name. Ohne Modell (`Plain`) ist sie genau die bisherige DB-Sicht. Die UI holt sie über den `PresentationService` (UI/State), der Modell und Einstellung bündelt und nur bei echter Änderung `Changed` meldet. Ein eigenes Interface (`IColumnPresentation`) gibt es nicht: Es gibt genau eine Implementierung, und die Klasse ist ohne Mocks testbar.
- **`ValueTable`** bildet DB-Werte auf Members ab, mit den vom Projekt-Converter berechneten Werten aus dem Export (kein Code des Nutzers in FerretSharp). Zahlen werden als `decimal` verglichen, CHAR ohne Blank-Padding. Werte ohne Member bleiben roh und werden markiert, Flags-Enums (als Zahl gespeichert) werden zerlegt.
- **Filter und Edits tragen DB-Werte.** Die Auswahllisten schreiben den gespeicherten Wert (`2`, `J`) in die Filterzeile bzw. den Editor; QueryBuilder, SQL-Vorschau, ChangeTracker und DML bleiben unverändert. Gespeicherte Filter funktionieren damit auch ohne geladenes Modell. Für LINQ (WP-15) übersetzt die Wertetabelle zurück. Enum-Spalten bieten `=`, `≠`, „in“ und die NULL-Prüfungen; einen schon gesetzten anderen Operator behält die Zeile.
- **Modell-Beziehungen** werden zu `ForeignKeyInfo` mit `FkSource.ClrModel` (`ClrModelMapping.ForeignKeys`, Name = Navigation, z. B. `Auftrag.Bearbeiter`). Der `SchemaCache` nimmt sie als weitere Quelle auf (`SetForeignKeys`); `OutgoingOf`/`IncomingOf` liefern alle Quellen. Ein deklarierter FK über dieselben Spaltenpaare verdrängt den Modell-FK – auch nach einem Schema-Refresh, wenn der Constraint inzwischen angelegt wurde. Alle Nutzer (Grid-Badges, Kontextmenü, Spaltenansicht) sehen die Beziehungen ohne eigenen Code; markiert werden sie über `Source`. Der Index-Hinweis („Fremdschlüssel ohne Index“) bleibt bei deklarierten FKs.
- Die Einstellung „aus“ blendet **Namen** aus (Beschriftung und Suche), nicht die Enum-Werte – die bleiben, solange ein Projekt verknüpft ist.
- Das Grid wird bei Modell- oder Einstellungsänderung nicht neu aufgebaut: `updateColumns` in `grid.js` ändert die Spalten-Metadaten an Ort und Stelle, danach lädt .NET die Zeilen neu.

## Konsequenzen

- SQL, Export (CSV, INSERT, Zwischenablage) und Fehlertexte bleiben bei DB-Namen und DB-Werten.
- AG Grid kopiert einfache Objekte der Column-Defs tief (`headerComponentParams`); veränderliche Metadaten gehen deshalb als Funktion (`getMeta`) an den Header. Die Kopfhöhe ist ein Theme-Parameter: als Grid-Option gesetzt, fällt die Zeilenhöhe auf den AG-Grid-Standard (42 px) zurück.
- Weitere Modell-Infos (Sprung zur Entity in Visual Studio, „Namen kopieren“) hängen sich an dieselbe Schicht.
