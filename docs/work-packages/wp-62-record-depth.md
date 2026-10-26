# WP-62 Datensatz-Diagramm: mehrere Ebenen

Branch `wp/62-record-depth`, begonnen am 2026-10-25. Nachtrag zu ADR 0034. Baut auf `RecordDiagram` und `RecordDiagramState` aus WP-60 auf; das Wahlfeld folgt dem „Ebenen“-Feld des Beziehungsdiagramms (WP-46).

## Planung (aus `docs/roadmap.md`)
- **Ziel:** beim Öffnen und beim Aufklappen mehr als eine Ebene laden, z. B. Kunde (Ebene 0) → Adressen (1) → Land (2). Die Adresse ist ein Kind des Kunden, das Land ein Elternteil der Adresse: Eine Ebene ist ein Schritt entlang einer Beziehung, in beide Richtungen.
- **Abbiegeregel:** Ein Pfad darf von abwärts (Kinder) nach aufwärts (Eltern) abbiegen, nie von aufwärts wieder abwärts – sonst kämen Geschwister herein (Land → alle Adressen des Landes). Links der Startzeilen also nur Eltern bis zur Tiefe n; rechts Kinder, und jede über ein Kind erreichte Zeile bekommt in der nächsten Ebene auch ihre Eltern (Kunde → Aufträge → Positionen und → Mitarbeiter). Wie beim Kopieren (WP-42), dort nur ohne Tiefe. Als Nachtrag zu ADR 0034.
- **Core:** Breitensuche in `RecordDiagram` über `ExpandSideAsync`: Ausgangspunkt jeder Ebene sind die Zeilen, die die vorige neu gezeigt hat, je mit „darf noch abwärts“; eine Abfrage je Beziehung für alle Zeilen der Ebene (wie heute je Seite). Bremsen bleiben: `_followed` (Zyklen), Gruppen ab 12 Zeilen werden nicht weiter verfolgt (auch nicht nach dem Öffnen), die Grenze von 2 000 Zeilen (`TooMany` je Beziehung – über alle Zeilen der Ebene, gröber als heute je Zeile). `RecordStep` bekommt `Depth` (Standard 1, alte Workspace-Dateien bleiben gültig); `ReplayAsync` sucht auf den aktuellen Daten neu, statt erreichte Zeilen zu speichern. Fortschritt nennt die Ebene.
- **UI:** Wahlfeld „Ebenen“ (1–4) in der Kopfzeile des Diagramms, Standard 1 (wie heute), gespeichert im Tab (`RecordTabState`). Es gilt beim Öffnen, für „Zurücksetzen“ und für die Griffe „+n“ (die Zahl am Griff bleibt die der offenen Beziehungen der ersten Ebene); einer einzelnen Beziehung aus dem Kontextmenü wird eine Ebene weit gefolgt. `InitialSteps`/`Changed` erkennen die beiden ersten Schritte auch mit Tiefe. Texte Englisch und Deutsch.
- **Kosten:** jede Ebene eine Abfrage je verschiedener Beziehung ihres Ausgangspunkts; mit vielen Nachschlagetabellen können Ebene 2–3 über das VPN 50–150 Abfragen sein. Abbrechen und „Session trennen“ über `RunProgress` wie bisher; abgebrochen bleibt, was schon da ist.
- **Tests:** Core `RecordDiagramTests` (Kunde → Adresse → Land bei Tiefe 2, keine Geschwister über einen Elternteil, links nur aufwärts, Gruppen stoppen, Zyklus, `TooMany` in einer Ebene, Replay mit Tiefe, Schritt ohne `Depth` aus der Datei = 1); UI `RecordDiagramStateTests` (Tiefe beim Öffnen und Zurücksetzen, `Changed`), `RecordTabTests` (Tiefe gespeichert und wiederhergestellt). E2E mit eigenem Container der Beispiel-DB.
- Offen: Was eine geänderte Tiefe bei einem Diagramm tut, das schon wächst (Vorschlag: unverändertes Diagramm – nur die beiden ersten Schritte – lädt gleich mit der neuen Tiefe neu; sonst gilt sie für die nächsten Griffe und „Zurücksetzen“); Obergrenze 4 oder mehr; ein Standard für neue Diagramme in den Einstellungen (Vorschlag: später).

## Entscheidungen des Nutzers (2026-10-25)
1. **Abbiegeregel:** Ein Pfad darf von abwärts nach aufwärts abbiegen, nie wieder zurück (Vorschlag aus der Prüfung der Umsetzbarkeit übernommen).
2. **Tiefe in der Kopfzeile** des Diagramms, gespeichert im Tab, **Standard 1** (wie bisher).
3. Mit „fang direkt an“ den Plan freigegeben; die offenen Punkte nach den Vorschlägen gebaut: Ein unverändertes Diagramm lädt beim Wechsel der Ebenen gleich neu, ein verändertes behält sich und nimmt die Ebenen für die nächsten Griffe und „Zurücksetzen“. Obergrenze 4. Ein Standard in den Einstellungen bleibt im Backlog.

## Gebaut
- **Core:**
  - `RecordDiagram.ExpandAsync(nodes, side, depth, …)` (die Überladung ohne Tiefe geht eine Ebene): Breitensuche in `ExpandSideAsync`. Jede Ebene liest einmal je Beziehung für alle Zeilen, die die vorige neu gezeigt hat; jede Zeile trägt mit, ob sie noch abwärts darf (nur eine als Kind erreichte). Die erste Ebene geht nur zur angefragten Seite, ab der zweiten biegen Kinder zu ihren Eltern ab. Zeilen einer Gruppe gehen nicht weiter. `MaxDepth = 4`, eine Tiefe außerhalb wird darauf gehalten.
  - `RecordStep.Depth` (Standard 1, ältere Dateien gehen eine Ebene), `ReplayAsync` sucht damit neu. `RecordTabState.Depth` (Standard 1); `Workspace.Normalized` hält beide Tiefen zwischen 1 und 4.
  - `TraversalProgress.Level` (`TraversalLevel`: Ebene n von m) nur bei mehr als einer Ebene; `OpenAsync` öffnet ein neues Diagramm nach beiden Seiten (Review).
- **UI:**
  - `RecordDiagramState.Depth`, `SetDepth` (true: unverändertes Diagramm, der nächste `LoadAsync` baut es neu), Öffnen und Griffe nehmen die Tiefe, „Zurücksetzen“ behält sie. `RecordTab` speichert und stellt sie wieder her.
  - `RecordDiagramView`: Wahlfeld „Ebenen“ 1–4 vorn in der Kopfzeile (Beschriftung `Relations_Levels`, Tooltip `Records_LevelsTitle` mit der Abbiegeregel), aus während eines Lesens. Fortschritt „Ebene 2 von 3: Lese MITARBEITER – bisher 4 Zeilen“ (`Records_ReadingLevel`).
- **Tests:** Core `RecordDiagramTests` (zwei Ebenen rechts mit Eltern der Kinder und drei Abfragen, links nur aufwärts bis Ebene 4, aufwärts bleibt aufwärts auch nach dem Abbiegen, Gruppen stoppen, Grenze in einer Ebene mit allen Zeilen der Ebene als Herkunft, Fortschritt mit Ebene, Abspielen mit Tiefe, Tiefe außerhalb), `WorkspaceStoreTests` (Datei ohne Tiefe, Tiefe von Hand außerhalb, Rundreise); UI `RecordDiagramStateTests` (neues Diagramm mit Tiefe 2, unverändert neu gebaut, verändert bleibt und der nächste Griff nimmt die Tiefe), `RecordTabTests` (Tiefe gespeichert und wiederhergestellt).

## E2E (2026-10-26)
Eigener Container mit der Beispiel-DB (`ferret-e2e62`, Port 1562, danach entfernt), eine Verbindung DEV ohne C#-Modell, Deutsch dunkel, nach dem Neustart Englisch hell. Startzeile KUNDEN 8 (ein Auftrag, ein Vertrag, Adresse 9); die Vertriebsmitarbeiter haben je 500 Verträge.
- **Geprüft (CDP):**
  - Ebene 1 wie bisher: Kunde, Adresse, Auftrag, Vertrag.
  - Wechsel auf 2 baut das unveränderte Diagramm neu: dazu die Eltern des Vertrags (zwei Mitarbeiter, Mandant, Preisliste, Projekt) links neben dem Vertrag, neun Zeilen. Die je 500 Verträge der Mitarbeiter kommen nicht herein, ihr Griff bietet sie an („+2“). „Zurücksetzen“ bleibt aus.
  - Ebene 3 mit Mitschnitt des Fortschritts: „Lese die Startzeilen“, dann „Ebene 1 von 3: Lese ADRESSE …“ bis „Ebene 2 von 3: Lese RECHNUNG …“; die dritte Ebene hat in der Beispiel-DB nichts mehr.
  - Griff eines Mitarbeiters mit Tiefe 3: zwei Gruppen „VERTRAG ×500“/„×499“, darunter keine weitere Ebene (1 008 Zeilen gelesen).
  - Wechsel auf 1 am veränderten Diagramm lässt es stehen; F5 baut es gleich wieder auf (die Schritte behalten Tiefe 3). „Zurücksetzen“ baut mit Ebene 1, der Wechsel auf 2 danach wieder neu.
  - Workspace-Datei: `depth` am Tab und an jedem Schritt. Nach dem Neustart Ebene 2 aktiv, dieselben neun Zeilen, englische Texte und Tooltip. Log ohne `[ERR]`/`[WRN]`.
- **Nicht mit echter Eingabe geprüft:** Per `SendInput` kamen nur Bewegungen an, kein `mousedown` (der Fall aus `e2e-testing.md`, ein Hook filtert eingeschleuste Klicks) – das Wahlfeld ist ein einfacher Knopf wie im Beziehungsdiagramm. Abbrechen eines Lesens über mehrere Ebenen (lokal unter einer Sekunde).
- **Gesehen:** „bisher 1 Zeilen“ (Einzahl fehlte seit WP-60) und die Ebenen je Seite beim Öffnen – beide im Review behoben (Befunde 5 und 6).

## Review (2026-10-26), behoben (Vorschläge übernommen, Entscheidung des Nutzers)
1. Der Wechsel der Ebenen lief über `Reset()`: Das gezeigte Diagramm war weg, bevor das neue gelesen war – abgebrochen oder gescheitert blieb ein leerer Tab, und „Laden“ baute wieder mit der neuen Tiefe. Jetzt setzt `SetDepth` nur die Tiefe; `LoadAsync` öffnet ein Diagramm, das nur geöffnet wurde (`OnlyOpened`: zwei Schritte über die Startzeilen, beide Seiten), bei anderen Ebenen neu (`RecordDiagram.OpenAsync`). Das gezeigte, seine Anordnung und die Markierungen bleiben, bis das neue fertig ist.
2. Passte eine Beziehung über alle Zeilen einer Ebene nicht unter die Grenze, galt sie bei jeder dieser Zeilen als verfolgt – ein Auftrag mit drei Positionen bot sie nicht mehr an. Jetzt nur noch bei einer einzelnen Zeile; von mehreren bleibt sie jeder angeboten (auch beim Öffnen mehrerer Startzeilen).
3. Der Wechsel löschte die markierten Zeilen (über `Reset()`) – mit 1 behoben, sie bleiben, solange die Zeilen gezeigt werden.
4. Ohne geladenes Diagramm (erstes Laden gescheitert, gespeicherter Tab) merkte sich der Wechsel nur die Zahl, und das Laden spielte die alte Tiefe ab. Mit 1 behoben: Auch ein gespeichertes, nur geöffnetes Diagramm öffnet mit den Ebenen des Tabs.
5. „bisher 1 Zeilen“: Die Fortschrittstexte haben eine Einzahl (`Records_ReadingOne/Other`, `Records_ReadingLevelOne/Other` über `TextFormat.Plural`), auch der aus WP-60.
6. Beim Öffnen zählte der Fortschritt je Seite („Ebene 1 von 3“ zweimal). `OpenAsync` zählt beide Seiten zusammen („Ebene 4 von 6“), das Abspielen alle Schritte einer ganzen Seite.
7. Die Tiefe wurde an fünf Stellen begrenzt – jetzt `RecordDiagram.ClampDepth` an den Eingängen (Datei, Nutzer, `ExpandAsync`/`OpenAsync`), nicht mehr beim Abspielen.
- **Tests:** Core `Too_many_rows_from_several_rows_leave_the_relationship_to_each_of_them`, `Opening_counts_the_levels_of_both_sides_together_and_so_does_the_replay`; UI `Other_levels_keep_the_diagram_and_its_marks_if_the_load_fails`, `Other_levels_before_the_first_load_open_a_restored_diagram_at_them`, `A_restored_diagram_that_did_more_than_open_keeps_its_levels`.
