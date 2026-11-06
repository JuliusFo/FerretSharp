# Klein: Zieltabelle eines neuen FK mit Suche und Vorschlag

Branch `feat/fk-target-picker` (von `main`), 2026-11-06.

## Anlass
Rückmeldung des Nutzers: Das Anlegen eines FK im Designer ist fummelig. Die Zieltabelle war ein natives `<select>` mit allen Tabellen des Schemas (Tippen springt nur zum Anfang einer Beschriftung), und aus dem Namen einer einzelnen Spalte ließe sich die Zieltabelle samt PK finden.

## Entscheidungen des Nutzers
- Beide Vorschläge umsetzen: Auswahl mit Suche **und** Vorbelegung aus dem Spaltennamen.
- Vorbelegt wird nur bei einem eindeutigen Treffer; bei mehreren stehen sie oben in der Liste. Eine selbst gewählte Tabelle wird nie überschrieben.
- Andere lange Listen (Spalten im Designer) nicht in dieser Änderung, sondern in den Backlog. Eine Tabellenliste als `<select>` gab es nur hier.

## Gebaut
- Core `Design/ForeignKeyTargets`: Vorschläge ohne Roundtrip aus dem Schema-Cache (Tabellen, `PrimaryKeyOf`, `OutgoingOf`). Gründe, nach Verlässlichkeit:
  1. Beziehung ohne Constraint (C#-Modell) auf genau diesen Spalten; die Zielspalten in der Reihenfolge der Schlüsselspalten.
  2. Einspaltiger PK mit dem Namen der Spalte (`KUNDE_NR`). Ein Name, der PK von mehr als drei Tabellen ist (`ID`), zählt nicht (`SpecificKeyTables`).
  3. Namenskonvention: `KUNDE_ID`, `KUNDEID`, `ID_KUNDE` → `KUNDE`, `KUNDEN`, `KUNDES`, `KUNDEE`, `KUNDEES` (und umgekehrt), Unterstriche egal; nur mit einspaltigem PK.
  4. Wie 3, nach einem Präfix (`T_KUNDE`).
  `Clear` nimmt den einzigen Treffer des besten Grundes – mit zweien gleich guten wählt der Nutzer. Typen werden nicht verglichen (die Spalten der Zieltabelle sind noch nicht geladen); ORA-02267 meldet der Plan, sobald sie es sind.
- UI State: `TableDesignerDraft.TargetsFor` (setzt die Komponente) und `Suggestions`; `FollowColumns` bei Spalte hinzu/weg und Typwechsel: Ein FK ohne gewählte Tabelle (`DesignConstraint.ReferenceChosen`) übernimmt den eindeutigen Vorschlag mit dessen Schlüssel, ohne Spalten hat er keine Tabelle; ohne eindeutigen Vorschlag bleibt eine vorgeschlagene Tabelle (eine zweite Schlüsselspalte nennt selten eine eigene Tabelle). Gewählt ist eine Tabelle aus der Auswahl und die aus „Als Constraint anlegen“. `Rebase` behält `TargetsFor`.
- `TablePickList`: Vorschläge oben, dann alle Tabellen (ohne Suche in Listenreihenfolge) bzw. die Treffer nach `ObjectSearch.FindTables` (wie die Befehlspalette, auch nach Entity-Name); höchstens 100 Zeilen, der Rest gezählt.
- `TablePicker` (Components/Schema): Knopf im Aussehen eines Feldes, gestrichelt mit „vorgeschlagen“, solange die Tabelle nicht gewählt ist; Menü mit Suchfeld (`ctx-filter`, ↑ ↓ über `menuKeys`), Enter im Feld nimmt die erste Zeile, PK neben dem Namen, Tabellen ohne PK blass mit Tooltip (ORA-02270), danach Fokus zurück auf den Knopf.
- `MenuButton.BelowButton`: Menü unter dem Knopf statt an der Maus – auch beim Öffnen per Enter/Leertaste (sonst ohne Mausposition oben links); passt es unten nicht, geht es über den Knopf (`PopoverMenu.Above`, dom.js `menuKeys`/`keepInViewport`: einmal beim ersten Messen entschieden, danach wächst und schrumpft es vom Knopf aus nach oben). Dazu `Disabled`, `AriaLabel`, `FocusAsync`, `type="button"`. dom.js `placeBelow`.
- Texte EN/DE, CSS `.table-pick*`.
- Tests: `ForeignKeyTargetsTests`, `TablePickListTests`, `TableDesignerDraftTests.A_foreign_key_takes_the_clear_suggestion_until_a_table_is_chosen`.

## Offen
- Kein Weg zurück zu „keine Tabelle“ in der Auswahl (das alte `<select>` hatte die Leerzeile); Backlog mit den Spaltenlisten.
- Tabellen ohne PK, aber mit eindeutigem Schlüssel erscheinen blass – UKs kennt der Cache ohne Abfrage nicht.

## E2E
Eigene Testinstanz (`--lang=de`) mit eigenem Container (Beispielschema, Port 1533) und zwei Zusatztabellen (`BANKEN` neben `BANK`, `E2E_FK` mit `KUNDE_ID`, `LAGER_ID`, `BANK_ID`); Öffnen und Wählen mit echter Windows-Maus, Tastatur per CDP.
- Gefunden und behoben: Unten im Designer passte das Menü nicht unter den Knopf, `menuKeys` schob es nach oben über den Knopf – es verdeckte das eigene Feld. Jetzt über dem Knopf (siehe oben), geprüft: Unterkante 2 px über dem Knopf, beim Filtern bleibt sie dort.
- Enter auf dem Knopf öffnet per CDP nur mit `text: ""` im `keyDown` (sonst kein `click`) – eine Eigenheit von CDP, keine der App.
- Nicht geprüft: helles Theme (nur Tokens), Öffnen per Leertaste.
