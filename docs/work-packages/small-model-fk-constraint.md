# Klein: Beziehungen aus dem C#-Modell als FK-Constraint anlegen → Unreleased

Branch `feat/model-fk-constraint` (von `main`), 2026-11-02.

## Anlass
Frage des Nutzers: Gibt es in der Übersicht der Tabelle eine Funktion, fehlende FKs anzulegen, die bisher nur über das Modell gelten? Gab es nicht: Der Abschnitt „Beziehungen aus dem C#-Modell“ der Constraints-Ansicht zeigte sie nur an, im Designer ließ sich ein FK nur von Hand eintragen. Geplant war es als Teil von WP-43 („Constraint anlegen“ bei 0 Verwaisten).

## Entscheidungen des Nutzers
- Den Teil von WP-43 vorziehen: ein Knopf je Zeile im Modell-Abschnitt, der den Designer mit einem vorausgefüllten FK öffnet.
- Nur an Beziehungen ohne Constraint, also an den Zeilen des Modell-Abschnitts (dort stehen deklarierte FKs ohnehin nicht doppelt) – nicht an der Constraints-Tabelle oder „Referenziert von“.

## Gebaut
- UI State: `TableDesignerDraft.AddForeignKey(ForeignKeyInfo)` legt einen FK mit den Spalten der Beziehung auf beiden Seiten an (Zielspalten bleiben, auch wenn die Zieltabelle danach bekannt wird und ihr PK eine andere Reihenfolge hat), Name per Vorschlag, mit Index, wechselt auf den Reiter Constraints; dieselbe Beziehung ein zweites Mal kommt nicht doppelt. `UnknownReferences()` nennt die Zieltabellen neuer FKs, deren Struktur noch fehlt (ohne die eigene Tabelle).
- `ModelConstraintOffers.Of` entscheidet den Knopf ohne Roundtrip: beide Seiten Tabellen, wie sie der Designer als Ziel anbietet (keine View, keine Tabelle hinter einem Synonym, geladen) – sonst ausgegraut mit Grund; Zielspalten ≠ PK (`SchemaCache.PrimaryKeyOf`) → Hinweis im Tooltip, dass Oracle dort einen eindeutigen Schlüssel braucht. Unique Keys kennt die Ansicht ohne Abfrage nicht; der Designer prüft sie nach dem Laden der Zieltabelle (`ForeignKeyNeedsKey`, ORA-02270).
- `TableTab.DesignerForeignKey` neben `OpenDesignerAt`: Die Spalten-Ansicht öffnet den Designer damit (`TableDesigner.AddForeignKey`) oder fügt den FK dem offenen Entwurf hinzu. Ausgehende Beziehung → diese Tabelle; eingehende → der Tab der anderen Tabelle (`ShellState.OpenTable` + `ShowTab`), denn dort liegt der FK.
- `TableDesigner`: Zieltabellen neuer FKs lädt jetzt `OnAfterRender` (je Tabelle einmal, auch für eine neue Tabelle) statt der Auswahl-Handler – so auch für den vorausgefüllten FK. Ein gescheitertes Laden wird nicht wiederholt (der Designer prüft dann Schlüssel und Typen nicht, Oracle schon).
- Texte EN/DE, `.link-btn:disabled`.
- Tests: `TableDesignerDraftTests.A_foreign_key_from_a_relationship_keeps_its_columns_and_comes_once` (bis zum Plan mit `FOREIGN KEY … REFERENCES` und `CREATE INDEX`), `ModelConstraintOffersTests` (PK in anderer Reihenfolge, kein PK, View, Synonym, nicht geladen).

## Offen
- Verwaiste Werte zählt erst WP-43; bis dahin scheitert das Anlegen mit ORA-02298 (der Tooltip sagt es).

## E2E
Eigene Testinstanz (`--lang=de`) mit eigenem Container (Beispielschema, Port 1531) und verknüpftem `samples`-Projekt; Klicks auf „Als Constraint anlegen“ und „Ausführen“ mit echter Windows-Eingabe (`Invoke-RealClick.ps1`).
- AUFTRAG → Constraints: Abschnitt „Beziehungen aus dem C#-Modell“ zeigt `(BEARBEITER_ID) → MITARBEITER (ID)` mit dem Knopf, Tooltip wie geplant. Klick öffnet den Designer auf dem Reiter Constraints mit `FK_AUFTRAG_MITARBEITER`, `BEARBEITER_ID → MITARBEITER (ID)`, „mit Index“; Skript `ADD CONSTRAINT … FOREIGN KEY … REFERENCES` und `CREATE INDEX IX_AUFTRAG_BEARBEITER_ID`. Ausgeführt: FK aktiv in der Constraints-Tabelle, der Modell-Abschnitt verschwindet (Beziehung jetzt deklariert).
- FK im Container wieder gelöscht, Schema neu geladen; MITARBEITER → Constraints: eingehende Beziehung von AUFTRAG mit Knopf, Klick wechselt in den Tab AUFTRAG mit demselben Entwurf. Ein zweiter Klick bei offenem Designer fügt nichts doppelt hinzu (Reiter zeigt weiter 3).
- Log ohne Warnungen und Fehler.
- Nicht geprüft: Scheitern an verwaisten Werten (ORA-02298) – Weg wie bei jedem FK aus dem Designer.
