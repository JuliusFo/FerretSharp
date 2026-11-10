# Klein: Objekte für den DDL-Vorschlag des Schema-Vergleichs auswählen

Branch `feat/schema-compare-ddl-selection` (von `main`), 2026-11-10. Baut auf WP-20 (Schema-Vergleich, DDL-Vorschlag) und WP-22 (Ausführen im SQL-Editor).

## Anlass (Wunsch des Nutzers, 2026-11-10)
- Der DDL-Vorschlag nahm immer alle abweichenden Objekte. Wunsch: nur bestimmte Tabellen/Views für das DDL auswählen.
- Bewertung vorab: `SchemaDdl.Align` hatte seit WP-20 einen Parameter `rows` (Objektzeilen), unbenutzt und ungetestet. Auswahl je Spalte/Constraint/Index (Abhängigkeiten zwischen Spalte, Schlüssel, Index, DROP/ADD) und je Statement verworfen – je Objekt reicht.

## Entscheidungen des Nutzers (2026-11-10)
- Auswahl direkt in der **Matrix**, nicht im Dialog; **nichts vorausgewählt**, er wählt gezielt.
- Ohne Auswahl ist „DDL-Vorschlag …“ gesperrt (kein Weg mehr zu „alles“ ohne Anhaken).
- Häkchen im Spaltenkopf wählt alle Treffer der aktuellen Filter und Suche (auch hinter „Mehr anzeigen“), mit Zwischenzustand.
- Objekte ohne Unterschied auf allen Seiten: Häkchen gesperrt.
- Auswahl wird mit gespeicherten Vergleichen **nicht** gespeichert.
- Als kleines Thema, ohne WP-Nummer und ohne ADR.

## Gebaut
- **Core:** `SchemaDdl.Align` mit `rows`: `DdlProposal.Selected` (Zahl der gewählten Objekte, Kopfzeile im Skript „Nur die n in der Matrix ausgewählten Objekte“) und `Unchanged` (gewählte Objekte, die zwischen Referenz und Ziel gleich sind – sie weichen nur auf anderen Seiten ab). Ein FK auf eine Tabelle, die im Ziel fehlt und nicht gewählt ist, bekommt eine Warnung (ORA-00942, `DdlWriter` mit `notCreated`); ohne Auswahl entsteht jede fehlende Tabelle, also keine Warnung. Texte `CompareText.ScriptSelectedOne/Other`, `ReferencedTableNotChosen`.
- **UI:** `SchemaCompareService.Chosen`/`ChosenRows`/`Choose`/`CanChoose`: Schlüssel der Objektzeilen; bleibt beim Filtern und über einen neuen Vergleich derselben Seiten (Objekte, die verschwunden oder überall gleich sind, fallen heraus – etwa nach dem Ausführen des Skripts), leert sich bei anderen Seiten, neuem oder geöffnetem Vergleich. `SchemaComparePage`: Häkchen-Spalte (Label füllt die Zelle, Klick klappt die Zeile nicht auf), Kopf-Häkchen über die sichtbaren wählbaren Objekte (Zwischenzustand über `dom.js setIndeterminate` – nur als Property, kein Attribut), Knopf „DDL-Vorschlag (n) …“, Hinweis „n ausgewählt, davon m ausgeblendet“. `SchemaDdlDialog` bekommt `Rows`, nennt gewählte, aber im Paar gleiche Objekte bzw. „Die ausgewählten Objekte sind zwischen diesen beiden Seiten gleich.“
- **Tests:** Core `SchemaDdlTests.Rows_limit_the_proposal_…` (erweitert um `Selected`, `Unchanged`, Kopfzeile), `Foreign_key_to_a_table_the_choice_leaves_out_and_the_target_lacks_warns`; UI `SchemaCompareServiceTests.Chosen_objects_stay_over_a_new_comparison_…`.
