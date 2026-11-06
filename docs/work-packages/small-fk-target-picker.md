# Klein: Zieltabelle eines neuen FK mit Suche und Vorschlag → Release 3.61.0

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
- Enter auf dem Knopf öffnet per CDP nur mit `text: "\r"` im `keyDown` (sonst kein `click`) – eine Eigenheit von CDP, keine der App.
- Nicht geprüft: helles Theme (nur Tokens), Öffnen per Leertaste.

## Nachtrag: Code-Review (2026-11-06)
Getrennter Reviewer über den Branch (vor dem Merge von `main` mit WP-74), zweiter Durchgang über die Korrekturen. Entscheidung des Nutzers: „Lass uns mal alle mitmachen, auch 3.“; nach dem zweiten Durchgang „Ja, beide“. Bei Nr. 6 meine Empfehlung umgesetzt (der Nutzer hat keine andere genannt).
1. M Eine Vorschlagszeile „Beziehung“ zeigte ihren Schlüssel, die Wahl nahm den PK (`REFERENCES KUNDE (ID)` statt des eindeutigen Schlüssels, mehrspaltig vertauscht). **Behoben** (`ab026f37`): `TableDesignerDraft.Reference` übernimmt den Schlüssel eines Vorschlags. Test scheitert ohne den Fix.
2. M Eine Beziehung auf eine Tabelle außerhalb der Liste (View, anderer Owner) wurde sofort übernommen, der Knopf zeigte „Tabelle …“, das DDL scheiterte erst beim Ausführen. **Behoben** (`dae1f716`): `ForeignKeyTargets.Suggest` nimmt nur Beziehungen auf angebotene Tabellen. Test scheitert ohne den Fix.
3. M Zwei Auswahlen mit Suche nach dem Merge (FK-Ziel und „Weg zu …“ aus WP-74) mit verschiedener Rangfolge, Grenze, Leertext und Bedienung. **Behoben** (`c53f9896`): „Weg zu …“ über `TablePicker` (neue Parameter `Class`, `ButtonContent`, `Title`, `SearchPlaceholder`, `ShowKeys`; `KeyOf` optional), `RelationPathsState.TargetChoices` statt `Candidates`; Nachtrag im WP-74-Protokoll.
4. K Ein nach halbem Lauf wieder angelegter FK galt als vorgeschlagen und konnte seine Tabelle an einen Namensvorschlag verlieren. **Behoben** (`2f2e0be1`): `ReferenceChosen = true` in `TakeOverReplacements`. Test scheitert ohne den Fix.
5. K Enter im Suchfeld nahm die Liste des letzten Renderns (Verdacht: schnelles Tippen/Einfügen + Enter). **Behoben** (`44ca98c1`): die Liste wird beim Enter aus dem aktuellen Text berechnet. Ohne Test – nicht ohne Rendern prüfbar (kein bUnit); nicht nachgestellt.
6. K Mit Suchtext nahm Enter einen schwach passenden Vorschlag vor dem genau getippten Namen. **Behoben** (`388681ab`): ein Vorschlag bleibt nur oben, solange er mindestens so gut passt wie der beste andere Treffer (`ObjectSearch.Rank`). Test scheitert ohne den Fix.
7. K „no PK“/„kein PK“ doppelt (`ModelViewText.Report_NoKey`). **Behoben** (`455e4d39`): `CommonText.NoPrimaryKeyShort` für Auswahl und Modellbericht.
- Zweiter Durchgang, A (K): Ein bei der Suche nach unten gerutschter Vorschlag zeigte den PK, die Wahl nahm den Schlüssel der Beziehung. **Behoben** (`df7245cf`): die Zeile behält Schlüssel und Grund. Test scheitert ohne den Fix.
- Zweiter Durchgang, B (K): WP-74-Protokoll nannte das entfernte `Candidates`; „Weg zu …“ nach dem Umbau ohne E2E. **Behoben**: Protokoll angepasst, E2E unten.
- Vorher schon da, in den Backlog: ein FK aus „Als Constraint anlegen“ auf eine Tabelle außerhalb der Liste (zeigt den Platzhalter, wie im alten `<select>`); zweite FK-Spalte auf eine Tabelle mit einspaltigem PK bekommt deren erste Spalte; nach Esc in einem Menü geht der Fokus bei keinem Menüknopf zurück.
- Konfliktauflösung des Merges geprüft (je 643 Schlüssel in beiden resx, nichts verloren oder doppelt); ein dabei verlorenes `</data>` war schon vor dem Push ergänzt.

E2E nach den Korrekturen (eigene Instanz und eigener Container, wieder entfernt; Log ohne `[ERR]`/`[WRN]`): „Weg zu …“ mit echter Maus geöffnet (unter dem Knopf, Start nicht in der Liste, keine PK-Spalte), Enter auf einem Treffer (Wege gefunden, Knopf hervorgehoben, Fokus auf dem Knopf), ✕ zurück. Nicht geprüft: schmales Fenster, ein Vorschlag aus einer Beziehung in der App (kein verknüpftes C#-Projekt; Unit-Tests).

