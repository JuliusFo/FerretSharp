# Klein: Beziehungen aus dem C#-Modell als FK-Constraint anlegen → Unreleased

Branch `feat/model-fk-constraint` (von `main`), 2026-11-02.

## Anlass
Frage des Nutzers: Gibt es in der Übersicht der Tabelle eine Funktion, fehlende FKs anzulegen, die bisher nur über das Modell gelten? Gab es nicht: Der Abschnitt „Beziehungen aus dem C#-Modell“ der Constraints-Ansicht zeigte sie nur an, im Designer ließ sich ein FK nur von Hand eintragen. Geplant war es als Teil von WP-43 („Constraint anlegen“ bei 0 Verwaisten).

## Entscheidungen des Nutzers
- Den Teil von WP-43 vorziehen: ein Knopf je Zeile im Modell-Abschnitt, der den Designer mit einem vorausgefüllten FK öffnet.
- Nur an Beziehungen ohne Constraint, also an den Zeilen des Modell-Abschnitts (dort stehen deklarierte FKs ohnehin nicht doppelt) – nicht an der Constraints-Tabelle oder „Referenziert von“.

## Gebaut
- UI State: `TableDesignerDraft.AddForeignKey(ForeignKeyInfo)` legt einen FK mit den Spalten der Beziehung auf beiden Seiten an (Zielspalten bleiben, auch wenn die Zieltabelle danach bekannt wird und ihr PK eine andere Reihenfolge hat), Name per Vorschlag, mit Index, wechselt auf den Reiter Constraints; nicht doppelt: ein neuer FK mit denselben Spaltenpaaren (in beliebiger Reihenfolge) wird zurückgegeben, ein behaltener der Tabelle ergibt null (Code-Review). `UnknownReferences()` nennt die Zieltabellen neuer FKs, deren Struktur noch fehlt (ohne die eigene Tabelle).
- `ModelConstraintOffers.Of` entscheidet den Knopf ohne Roundtrip: beide Seiten Tabellen, wie sie der Designer als Ziel anbietet (keine View, keine Tabelle hinter einem Synonym, geladen) – sonst ausgegraut mit Grund; Zielspalten ≠ PK (`SchemaCache.PrimaryKeyOf`) → Hinweis im Tooltip, dass Oracle dort einen eindeutigen Schlüssel braucht. Unique Keys kennt die Ansicht ohne Abfrage nicht; der Designer prüft sie nach dem Laden der Zieltabelle (`ForeignKeyNeedsKey`, ORA-02270).
- `TableTab.RequestForeignKey` neben `OpenDesignerAt` (Liste, `TakeForeignKeys`): Ein offener Entwurf nimmt die Anfragen sofort (`ColumnsView`), ein öffnender oder noch ladender nach dem Laden (`TableDesigner`); Schließen oder ein Ladefehler verwirft, was liegen blieb (Code-Review). Ausgehende Beziehung → diese Tabelle; eingehende → der Tab der anderen Tabelle (`ShellState.OpenTable` + `ShowTab`), denn dort liegt der FK.
- `TableDesigner`: Zieltabellen neuer FKs lädt jetzt `OnAfterRender` statt der Auswahl-Handler – so auch für den vorausgefüllten FK, auch für eine neue Tabelle. `ReferencedTableLoads` fragt je Tabelle einmal; ein gescheitertes Laden wiederholt es nicht von selbst (sonst nach jedem Rendern gegen eine tote Verbindung), aber eine erneute Wahl der Tabelle und ein Laden, das nicht starten konnte, fragen neu (Code-Review).
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

## Nachtrag: Code-Review (2026-11-03)
Getrennter Reviewer über den Branch, zweiter Durchgang über die Fixes. Entscheidung des Nutzers: „Alle beheben“, nach dem zweiten Durchgang „Beide beheben“.
- **M1 behoben** (`b6d39626`): Seit dem Laden in `OnAfterRender` wurde eine Zieltabelle je Designer nur einmal gefragt, auch wenn das Laden scheiterte – ein von Hand angelegter FK blieb ohne Zielspalten und blockierte bis zum Schließen (Entwurf weg). Jetzt `ReferencedTableLoads`: erneute Wahl und nicht gestartetes Laden fragen neu. Test `ReferencedTableLoadsTests` hält die Regel fest, die Verdrahtung in der Komponente nicht (kein Rendern in den Tests).
- **K1 behoben** (`1bea5043`): Ein zweiter Klick, während der Designer noch lud, ersetzte die erste Anfrage. Jetzt Liste am Tab (`RequestForeignKey`/`TakeForeignKeys`). Test `DesignerForeignKeyRequestsTests` (Datenstruktur; der Ablauf über die Komponenten ist lokal nicht nachzustellen, das Laden ist zu schnell).
- **K2 behoben** (`64bcd962`): Doppelprüfung über Spaltenpaare als Menge, auch gegen behaltene FKs der Tabelle (sonst ORA-02275 mitten im Lauf). Zwei Fälle in `TableDesignerDraftTests`, beide scheitern ohne den Fix.
- **K3 behoben** (`fd6f14bf`): Tooltip – Index nur, falls keiner die Spalten trägt; „Fremdschlüssel“; „lassen das Anlegen scheitern“.
- **K-a behoben** (dieser Commit): Roadmap, Index und „Gebaut“ nannten das entfernte `TableTab.DesignerForeignKey` und das alte Laden.
- **K-b behoben** (`270be190`, durch K1 entstanden): Ein Ladefehler des Designers hat kein „Schließen“ – die Anfrage blieb am Tab und tauchte bei einem späteren „Bearbeiten“ auf. Jetzt verwirft der Ladefehler sie.
- Vorher schon da (ohne Entscheidung, siehe Zusammenfassung): `FillReferenced` füllt nur leere Zielspalten nach; Absturz in `KeyColumn` bei einer Zieltabelle ohne Spalten (im E2E gefunden, unten).

### E2E nach dem Review
- Tooltip mit dem neuen Text in der App.
- K-b: Container gestoppt, „Als Constraint anlegen“ → Designer zeigt ORA-03135 ohne „Schließen“; nach Neuverbinden „Bearbeiten“ → Entwurf mit 2 Constraints, kein liegengebliebener FK. Nicht eindeutig: Neu verbinden baut die Tabs vermutlich neu auf.
- M1 ließ sich nicht nachstellen: Ein Ladefehler ohne Verbindungsverlust war nicht herzustellen. Der Versuch über eine gelöschte Zieltabelle, die der Cache noch kennt, fand einen Absturz, den es schon vorher gab: `GetDetailsAsync` liefert eine Struktur ohne Spalten, `TableDesignerDraft.KeyColumn` greift auf `Columns[0]` zu → Fehler im Tab („Neu laden“ stellt Tab und Entwurf wieder her). Auf `main` ebenso über den alten Auswahl-Handler; der vorausgefüllte Modell-FK ist nicht betroffen (Zielspalten gesetzt).
