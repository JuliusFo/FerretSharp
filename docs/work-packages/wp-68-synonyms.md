# WP-68 Synonyme anlegen und löschen → Release offen

Branch `wp/68-synonyms`, 2026-10-31. Frage eines Kollegen, als Paket eingeplant vom Nutzer am 2026-10-30. Vorher ging das nur über den SQL-Editor (`CREATE`/`DROP [PUBLIC] SYNONYM` laufen dort seit WP-22 als DDL, eigens getestet war es nicht).

## Planung (aus `docs/roadmap.md`)
- **Ansicht „Synonyme“ je Objekt** (Tabelle, View, MView, PL/SQL): private und öffentliche Synonyme auf das Objekt aus `ALL_SYNONYMS` (ohne DB-Link) mit Owner, Name und „öffentlich“; `ISchemaReader`-Methode und Integrationstest.
- **Anlegen** in der Ansicht: Name (Vorschlag: Name des Objekts), privat im eigenen Schema oder öffentlich; Name über `OracleIdentifier.Problem`; ein belegter Name wird vorab gemeldet (private Synonyme teilen den Namensraum mit Tabellen, Views, Sequenzen und PL/SQL, öffentliche haben einen eigenen); kein `OR REPLACE` ungefragt.
- **Löschen** je Zeile; bei einem öffentlichen Synonym sagt die Bestätigung, dass es für alle Benutzer der Datenbank gilt.
- **Ausführen** über den DDL-Baustein aus WP-23 (Bestätigung, Transaktion nur nach Rückfrage verwerfen, Prod nur freigeschaltet), daneben „Kopieren“ und „Im SQL-Editor öffnen“. DDL-Text nur in `Core/Ddl`, immer mit Owner.
- **Liste aller privaten Synonyme des Schemas** zum Aufräumen, je Zeile Ziel und Status, eigener Tab über die Befehlspalette.
- **Öffentliche Synonyme** anlegen und löschen wird angeboten; **Rechte** nicht vorab abfragen, bei ORA-01031 das fehlende Recht nennen.
- **Explorer:** seit WP-70 „Synonym löschen“ im Objektmenü von Einträgen mit „SYN“ und die Ansicht unter den Chips.

## Entscheidungen des Nutzers (2026-10-30/31, Roadmap)
1. Liste nur der **privaten** Synonyme des Schemas (öffentliche wären beim Nutzer rund 28.000).
2. **Öffentliche Synonyme** anlegen und löschen anbieten; Schutz sind der Bestätigungstext und Prod nur freigeschaltet.
3. **Rechte** nicht vorab prüfen (kein Roundtrip auf `SESSION_PRIVS`), bei ORA-01031 das fehlende Recht nennen.
4. Kein eigenes Kontextmenü für das Paket; seit WP-70 trägt es sich ins Objektmenü ein.
5. Plan bestätigt am 2026-10-31 („Okay, lass uns so loslegen.“) – mit den drei offenen Punkten unten.

## Gemessen gegen Oracle 23 (eigener Container, vor dem Bau der Status-Abfrage)
- Ein Synonym, dessen Ziel **gelöscht** wurde, steht in `ALL_OBJECTS` auf **INVALID**. Eines auf ein Ziel, das **nie existierte**, bleibt **VALID** – ebenso eines auf ein Ziel **ohne Recht** darauf. `ALL_OBJECTS` zeigt beide Ziele nicht. „Ziel fehlt“ und „kein Zugriff“ sind deshalb nur teilweise zu trennen.
- `ALL_SYNONYMS` zeigt private Synonyme **eines anderen Schemas** nur, wenn die Session das Ziel lesen darf (gemessen: von fünf Synonymen eines zweiten Users nur das mit Grant, nicht einmal eines auf ein eigenes Objekt des Login-Users). Ins Leere zeigende fehlen dort.
- `ALL_OBJECTS.NAMESPACE = 1` ist der gemeinsame Namensraum von Tabellen, Views, Sequenzen, PL/SQL, Typen und privaten Synonymen; öffentliche Synonyme sind die Objekte von `PUBLIC`.
- Der gvenzl-App-User hat `CREATE SYNONYM`, aber nicht `CREATE PUBLIC SYNONYM` – damit testet die Integration ORA-01031.

## Entscheidungen in der Umsetzung
- **Status** (`SynonymTargetState`, `SchemaSynonym.StateOf`): DB-Link → „über DB-Link“ (nicht geprüft); Ziel sichtbar → „ok“ (auch ein Synonym auf ein Synonym); Synonym INVALID, Ziel-Owner gibt es nicht mehr, oder Ziel im Schema des Login-Users (dort ist alles sichtbar) → „Ziel fehlt“; sonst „fehlt oder kein Zugriff“ mit Tooltip, warum es sich nicht trennen lässt. Abweichung vom Roadmap-Text („kein Zugriff“), im Plan angekündigt.
- **Fremdes Schema im Profil:** Die Liste sagt in einem Hinweis, dass Oracle dann nur Synonyme mit lesbarem Ziel zeigt. Ein privates Synonym landet im **angezeigten** Schema (das Statement nennt den Owner); das braucht dort `CREATE ANY SYNONYM`, die Meldung nennt es (`SynonymDdl.RequiredPrivilege` nach Login-User, eigener vs. fremder Owner, öffentlich).
- **DDL-Text:** `OracleDdl.CreateSynonym`/`DropSynonym` (Abschnitt wie die Trigger), öffentliche Fassade `SynonymDdl` liefert `DdlStep` der neuen Art `DdlStepKind.Synonym` (Stufe 13 wie PL/SQL-Objekte).
- **Belegter Name:** eine kleine Abfrage (`GetNameTakenAsync`, Namensraum 1 bzw. `PUBLIC`), erst wenn das Tippen 400 ms ruht (`SynonymDraft.CheckDelay`, über VPN nicht je Taste); bis zur Antwort steht „Prüfe, ob der Name frei ist …“ als Problem und „Ausführen“ ist gesperrt; eine ältere Antwort zählt nie für einen neueren Namen. Scheitert die Prüfung, sperrt sie nichts – Oracle meldet dann ORA-00955.
- **Ausführen** über `DdlDraftPanel` wie Index-Entwurf und Designer (Bestätigung, Prod, offene Transaktion, „Im SQL-Editor öffnen“, Speichern). Der Hinweis „gilt für alle Benutzer“ steht bei öffentlichen Synonymen im Panel und in der Bestätigung.
- **„Synonym löschen“ im Objektmenü** öffnet den Tab des Objekts in der Ansicht „Synonyme“ mit dem DROP im Panel und **gleich der Bestätigung** (`DdlDraftPanel.ConfirmAtOnce`, nur wenn es laufen darf). Abbrechen lässt das Statement stehen. Kein Dialog auf Shell-Ebene: Fortschritt, Fehler und Sperrhalter zeigt so das Panel wie überall.
- **Ansicht für PL/SQL** nur bei Packages, Prozeduren und Funktionen – über den Namen eines Triggers erreicht man nichts (`PlSqlTab.ViewsOf`).
- **Links** lassen die Ansicht „Synonyme“ weg wie „Verwendung“ und „Beziehungen“: Ein älteres FerretSharp lehnte den ganzen Link sonst ab.
- **Tab der Liste** (`SynonymListTab`, einer je Workspace, gespeichert mit „Nur ohne Ziel“), Aktion `ShortcutAction.SchemaSynonyms` ohne Standardtaste (Befehlspalette, Abschnitt Navigation); F5 lädt neu. Icon Lucide `signpost`.
- Kein ADR: kein neuer Weg zur Datenbank (Lese- und Schemaweg wie bisher).

## Gebaut
- **Core:** `SynonymDdl` (Create, Drop, RequiredPrivilege), `OracleDdl.CreateSynonym`/`DropSynonym`, `DdlStepKind.Synonym`; `ISchemaReader.GetSynonymsAsync`/`GetSchemaSynonymsAsync`/`GetNameTakenAsync` in `OracleSchemaReader.Synonyms.cs`, durchgereicht im `SchemaCache`; `SchemaSynonym`, `SynonymTargetState`; `TabMode.Synonyms`, `PlSqlView.Synonyms`, `TabState.SynonymList`; `ShortcutAction.SchemaSynonyms`; `TableLink` ohne die neue Ansicht.
- **UI-State:** `SynonymDraft` (Name, Art, verzögerte Prüfung), `SynonymListTab`, `ISynonymTab.DropRequested` an `TableTab`/`PlSqlTab`, `ShellState.OpenSynonymList`/`RequestSynonymDrop`, `ShellActions`.
- **UI:** `SynonymsView` (Tabelle und PL/SQL), `SynonymListView`, Eintrag in `TableMenuBody`/`PlSqlMenuBody`, `TabBar`, `Shell.razor`, `DdlDraftPanel.ConfirmAtOnce`, CSS `.synonym-kind`.
- **Tests:** Core `SynonymDdlTests`, `TableLinkTests`, `ReadOnlyTests` (neue Lesemethoden); Integration `SynonymTests` (Synonyme auf ein Objekt, Status aller Fälle, belegte Namen, Anlegen/Löschen über den Schemaweg, ORA-01031) mit erweitertem `SynonymSchema`; UI `SynonymDraftTests`, `SynonymListTabTests`.

## Nebenbei behoben
- **Explorer nach DDL ohne Tab-Änderung:** Er renderte nur neu, wenn sich die offenen Tabs änderten (`ShellKey`); ein angelegtes oder gelöschtes Synonym – ebenso eine View aus dem SQL-Editor – blieb bis zur nächsten anderen Änderung unsichtbar. Der Schlüssel enthält jetzt den Ladezeitpunkt des Schemas (`SchemaCache.LoadedAt`). Gefunden im E2E.

## E2E (2026-10-31)
Eigener Container `ferret-wp68` (Port 1533, Beispielschema plus Messobjekte), eigene Instanz über `tools/e2e` (`--data-dir`, Port 9333), deutsch/dunkel, Passwort nicht gespeichert. Probe für eingeschleuste Eingaben ging durch.
- **Mit echter Windows-Eingabe:** Rechtsklick auf `S_GRANTED` (SYN) im Explorer öffnet das Objektmenü mit „Synonym S_GRANTED löschen“; der Klick darauf öffnet den Tab in „Synonyme“ mit der Bestätigung (abgebrochen).
- **Per CDP:** Ansicht „Synonyme“ einer Tabelle (`S_OWN`); „Synonym anlegen“ mit Vorschlag `M_OWN` → „ist im Schema FERRET schon vergeben (TABLE)“, nach Tippen „Prüfe …“, dann frei; Anlegen über die Bestätigung, Liste lädt neu; öffentlich → ORA-01031 und „Dafür braucht es das Recht CREATE PUBLIC SYNONYM.“; Löschen über die Zeile (ohne automatische Bestätigung); „Synonym löschen“ aus dem Menü mit sofortiger Bestätigung, Abbrechen lässt das DROP stehen, Ausführen entfernt den Eintrag aus dem Explorer (nach dem Fix); Befehlspalette „>synonym“ → Tab mit allen Zuständen wie gemessen, „Nur ohne Ziel“, Löschen aus der Liste; Trigger ohne Ansicht, Package mit Ansicht und angelegtem Synonym; nach Neustart Synonym-Tab samt Filter und Ansicht „Synonyme“ der Tabelle wiederhergestellt.
- Log: nur die erwartete Warnung zu ORA-01031.
- **Nicht geprüft:** Prod-Verbindung (gesperrter Workspace: Panel zeigt die Sperre, keine sofortige Bestätigung – Logik wie im Designer), englische Oberfläche und helles Theme, Profil mit fremdem Schema (Hinweis in der Liste nur per Code).
- **Bekannt:** Ein Tab, der über ein gelöschtes Synonym geöffnet war, heißt bis zum nächsten Verbinden weiter wie das Synonym (Backlog).
