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
- **UI:** `SchemaCompareService.Chosen`/`ChosenRows`/`Choose`/`CanChoose`: Schlüssel der Objektzeilen; bleibt beim Filtern und über einen neuen Vergleich derselben Seiten (Objekte, die verschwunden oder überall gleich sind, fallen heraus – etwa nach dem Ausführen des Skripts), leert sich bei anderen Seiten, neuem oder geöffnetem Vergleich. `SchemaComparePage`: Häkchen-Spalte (die ganze Zelle wählt, Klick klappt die Zeile nicht auf), Kopf-Häkchen über die sichtbaren wählbaren Objekte (Zwischenzustand über `dom.js setIndeterminate` – nur als Property, kein Attribut), Knopf „DDL-Vorschlag (n) …“, Hinweis „n ausgewählt, davon m ausgeblendet“. `SchemaDdlDialog` bekommt `Rows`, nennt gewählte, aber im Paar gleiche Objekte bzw. „Die ausgewählten Objekte sind zwischen diesen beiden Seiten gleich.“
- **Tests:** Core `SchemaDdlTests.Rows_limit_the_proposal_…` (erweitert um `Selected`, `Unchanged`, Kopfzeile), `Foreign_key_to_a_table_the_choice_leaves_out_and_the_target_lacks_warns`; UI `SchemaCompareServiceTests.Chosen_objects_stay_over_a_new_comparison_…`.

## E2E
2026-11-10, eigener Container (`New-SampleDb.ps1 -Name ferret-e2e-ddlsel -Port 1545`, danach entfernt) mit zwei weiteren Benutzern FERRET_TEST und FERRET_PROD (je zwei Tabellen: `notizen` auf Dev und Test gleich, auf Prod mit Zusatzspalte; `GLEICH` überall gleich), drei Verbindungen als `connections.json`, Passwörter nur auf der Seite getippt; Testinstanz deutsch/dunkel, Klicks mit echter Windows-Eingabe (Probe bestanden).
- **Gefunden und behoben:** Ein Klick in den unteren Teil der Häkchen-Zelle ging ins Leere – ein `label` füllt eine Tabellenzelle nur in der Breite, nicht in der Höhe. Jetzt nimmt die Zelle (`td`/`th`) den Klick selbst, das Kästchen stoppt seinen eigenen.
- Geprüft: Zelle, Kästchen und Name (klappt auf, wählt nicht) mit echter Maus; Kopf-Häkchen wählt alle 49, mit Suche „AUFTRAG“ „49 ausgewählt, davon 47 ausgeblendet“ und Kopf nimmt die 2 Treffer heraus, danach Zwischenzustand; `GLEICH` gesperrt mit Tooltip; Dialog „Test an Dev“ mit AUFTRAG und `notizen`: Kopfzeile „Nur die 2 …“, Warnung am FK auf KUNDEN, Hinweis „gleich, daher kein Schritt: notizen“; „Im SQL-Editor öffnen“ bringt das Skript in den Tab von FERRET_TEST (nicht ausgeführt); Auswahl übersteht Seitenwechsel und erneuten Vergleich. Log ohne `[ERR]`/`[WRN]`.
- Nicht geprüft: das Skript auszuführen und danach erneut zu vergleichen (Herausfallen angeglichener Objekte nur im Unit-Test).

## Nachtrag: Code-Review (2026-11-10)
Eigener Reviewer-Agent, danach jeder Befund nachgeprüft. Entscheidung des Nutzers: „Alle beheben“ (K4 wie vorgeschlagen nur in den Backlog).
- M1 behoben: Fiel beim erneuten Vergleich eine Seite aus (VPN, Passwort), schnitt die Matrix der übrigen Seiten die Auswahl zu – Objekte, die nur gegen die ausgefallene Seite abwichen, waren dauerhaft weg. Zugeschnitten wird jetzt nur nach neuem Lesen aller Seiten; `IsChosen` zeigt dazwischen nur, was in der Matrix abweicht (Test `A_side_failing_on_comparing_again_…`, scheitert ohne Fix).
- K5 behoben (mit M1): „Spaltenreihenfolge“ aus und wieder an kostete still die Auswahl der Objekte, die nur in der Reihenfolge abwichen – eine Neuberechnung aus denselben Snapshots schneidet nicht mehr zu.
- K1 behoben: leere Häkchen-Zelle der Kindzeilen ohne Hand-Cursor.
- K2 behoben: Der Dialog nennt höchstens zehn gewählte, im Paar gleiche Objekte („und n weitere“), alle im Tooltip.
- K3 behoben: Tooltip des Kopf-Häkchens spricht von Treffern (wirkt auch hinter „Mehr anzeigen“), ohne wählbare Treffer kein Tooltip.
- K4 in den Backlog: FK auf einen Schlüssel oder eine Spalte, die erst mit einer nicht gewählten, im Ziel vorhandenen Elterntabelle entstünde, scheitert ohne Hinweis (ORA-02270/00904).
- K6 behoben: `DdlOutputTests` hält das Skript mit Auswahl fest; Backlog-Eintrag zum E2E mit zwei Schemas angepasst.
- Zweiter Durchgang über die Fixes (alle decken ihren Befund), zwei neue K. Entscheidung des Nutzers zu K7: „Kopf-Häkchen leert sie“.
  - K7 behoben: Nach einem Seitenausfall warteten gewählte Objekte unsichtbar und ohne eigenes Kästchen; „alle heraus“ ließ sie stehen, beim nächsten vollständigen Vergleich kamen sie ungefragt zurück. Das Kopf-Häkchen nimmt sie jetzt mit heraus (`ChooseAll`, Test `Taking_all_out_also_drops_…`).
  - K8 behoben: Test für K5 (`Column_order_off_hides_the_choice_and_on_brings_it_back`); beide neuen Tests scheitern ohne ihren Fix.
- Dritter Durchgang, zwei neue K; Entscheidung des Nutzers: „Ja, so umsetzen“.
  - K9 behoben: Bei zwei Seiten, deren Ziel ausfiel, wich nichts ab – das Kopf-Häkchen war gesperrt, die wartenden Objekte ließen sich nicht leeren (der Test rief den Service direkt, einen Weg, den die Seite dort nie nahm). Der Zustand des Kopf-Häkchens kommt jetzt aus dem Service (`Header` → `HeaderChoice`: an, Zwischenzustand, bedienbar, nächster Klick, Tooltip) und zählt wartende Objekte mit; nur noch wartende → bedienbar, ein Klick nimmt sie heraus. Das Kästchen schaltet nicht selbst um (`preventDefault`), die Zelle entscheidet – ein selbst umgeschaltetes Kästchen setzt Blazor nicht zurück, wenn sich sein Wert im Render-Baum nicht ändert.
  - K10 behoben: Der Tooltip nennt die wartenden Objekte, die „alle heraus“ mitnimmt („Die n Treffer und m wartenden Objekte …“), auch bei aktiver Suche.
- E2E danach (eigener Container wie oben, zwei Seiten, echte Maus): Kopf-Häkchen „alle hinein / alle heraus“ bleibt synchron mit dem Zustand; FERRET_TEST per `ALTER USER … ACCOUNT LOCK` ausfallen lassen (ORA-28000) → Kopf im Zwischenzustand mit Tooltip „Die 2 wartenden Objekte …“, Klick leert, nach dem Entsperren und erneuten Vergleich ist nichts gewählt. Log ohne `[ERR]`/`[WRN]`.
- Nicht per E2E nachgeprüft: K1 und K2 (Cursor der Kindzellen, gekürzte Liste) – nur Build und Unit-Tests.
