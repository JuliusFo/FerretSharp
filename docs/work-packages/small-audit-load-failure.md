# Klein: Audit-Zustand im Designer nicht gelesen

Branch `fix/audit-load-failure` (von `main`), 2026-10-28.

## Anlass
Im Code-Review von `small-audit-create-setting.md` als „vorher schon da“ gefunden: Scheiterte im Tabellen-Designer das Lesen von Audit-Tabelle und Trigger (Verbindung weg), galt das wie „keine Audit-Tabelle“ (`AuditState.None`). Aus der Audit-Zeile geöffnet und mit „Audit-Tabellen anlegen“ an plante der Designer dann `CREATE TABLE` für die vorhandene Audit-Tabelle – das Skript scheiterte mit ORA-00955, nachdem die Schritte der Basistabelle schon gelaufen waren. Außerdem setzte `WithAudit ??= false` den Haken für den ganzen Entwurf fest auf aus.

Entscheidung des Nutzers: gleich nach dem Merge von #81 als eigener Fix, Variante a – „Haken gesperrt mit Tooltip, daneben Neu laden“ (statt b: Haken bedienbar, Problem im Plan sperrt „Ausführen“).

## Gebaut
- UI State `ColumnDesigner`: `hasAuditTable` ist `bool?`, null = nicht gelesen. `CanAudit`/`AuditTakesPart` sind dann false, `AuditAtStart` liefert null (der Haken bleibt unentschieden und startet, sobald gelesen ist).
- `TableDesigner`: Zustand „nicht gelesen“, wenn `AuditLoader.LoadAsync` nicht gelingt (seit dem Review in `AuditRead.Unread`); `HasAuditTable` null. Reiter „Audit“: Haken gesperrt, Hinweis mit Warnsymbol und Tooltip `Audit_DesignerUnread`, daneben „Neu laden“ (`CommonText.Reload`, liest erneut mit der Explorer-Session).
- Text `Audit_DesignerUnread` Englisch und Deutsch.
- Test: UI `ColumnDesignerTests.An_audit_state_that_could_not_be_read_keeps_the_box_out_and_undecided`.
- Nach dem Code-Review (Nachtrag unten): `AuditRead` (Generation, Lesen, Fehlschlag), `ColumnDesigner.HasAuditTable(newTable, templateMissing, read)`, Warnung mit gelbem Reiterpunkt, kein Skript während des Lesens.

## E2E
2026-10-28, eigener Container, Standardvorlage, CDP. Den Lesefehler genau beim Audit stellt ein TCP-Proxy im Scratchpad nach, der die Verbindung beim ersten Paket mit `all_trigger_cols` trennt (nur, solange eine Flag-Datei liegt) – Constraints und Indizes liest der Designer nicht aus dem Cache, ein Abbruch per `KILL SESSION` trifft deshalb erst sie.
- AUFTRAG mit Audit-Tabelle, Designer über „Abgleichen“: Haken leer und gesperrt, Hinweis und „Neu laden“, Skript 0 Schritte (vorher: `CREATE TABLE AUDIT_AUFTRAG`). „Neu laden“ ohne Verbindung: bleibt so, keine Fehlerseite.
- Nach „Neu verbinden“ (baut die Tabs neu auf, ein Designer ohne Änderungen kommt nicht wieder) und erneutem „Abgleichen“: Haken an, 16 Schritte.
- „Neu laden“ mit Erfolg im selben Designer: erst im Nachtrag geprüft (unten) – ein abgebrochenes Lesen beendet die Session, und „Neu verbinden“ baut den Designer neu auf.

## Offen
- Der Haken heißt im Fehlerfall „Audit-Tabelle und Trigger anlegen“ (wie ohne Audit-Tabelle); der Hinweis daneben sagt, warum er gesperrt ist.
- Vorher schon da (Review): `LoadAuditAsync` leert `_auditCounting` nicht – eine Zählung aus dem vorigen Lesen kann danach in die geleerten Fakten schreiben (Verdacht, geringe Wirkung). Kein eigenes Abbrechen, wenn das Lesen über ein stilles VPN hängt (nur Schließen des Designers).

## Nachtrag: Code-Review (2026-10-28)
Reviewer-Agent, vier Durchgänge (drei mit Befunden), nur kleine Befunde. Entscheidungen des Nutzers: erster Durchgang „Jo, mach beides.“ (Punkt 2 mit Punkt am Reiter und Warnung), zweiter „Jo, mach das so.“ (alle drei, den Verdacht erst im Test nachstellen, dazu „keine Schritte während des Lesens“).
- Behoben: Während „Neu laden“ galt der Zustand als „keine Audit-Tabelle“ – Haken frei, ein Haken dabei wurde zur Wahl des Nutzers, mehrere Klicks lasen parallel. Jetzt `ColumnDesigner.HasAuditTable` (null auch während des Lesens), „Neu laden“ gesperrt.
- Behoben: Außerhalb des Reiters „Audit“ fehlte jedes Zeichen. Jetzt gelber Punkt am Reiter (`.tab-dot.warn`) und die Warnung `Audit_DesignerUnreadWarning` (sperrt „Ausführen“ nicht).
- Behoben (zweiter Durchgang, Rückschritt des ersten Fixes): Ohne Vorlage beim Öffnen galt `AuditState.None`; wurde die Vorlage danach lesbar (sie wird bei Änderung neu gelesen), plante der Designer mit „keine Audit-Tabelle“. Jetzt bekommt `HasAuditTable` die Vorlage der aktuellen Darstellung: ohne Vorlage false (Problem „Vorlage kaputt“ wie bisher), eine aufgetauchte, nicht gelesene macht den Zustand unbekannt (Hinweis, „Neu laden“, Warnung). Der Startwert des Hakens wird erst mit Vorlage entschieden.
- Behoben (Verdacht, im Test nachgestellt): Zwei Lesevorgänge („Neu laden“ und das Laden nach einem halben Lauf) konnten sich überholen – das ältere, zuletzt fertige ließ den Stand von vor dem Lauf stehen, das zuerst fertige gab „Neu laden“ frei. Jetzt `AuditRead` mit Generation: nur das neueste Lesen zählt (`AuditReadTests`).
- Behoben (vorher schon da, mitgenommen): Während des Lesens zeigte das Skript die Tabellenschritte ohne Audit. Jetzt wie beim Zählen kein Skript, Chip „Audit-Tabelle und Trigger werden gelesen …“ (`Audit_DesignerReading`).
- Doku nachgezogen (Protokoll, CHANGELOG, `audit-templates.md`).
- Dritter Durchgang, Entscheidung des Nutzers „Jo, mach das so.“: Nach „Vorlage aufgetaucht“ verschwanden Hinweis und „Neu laden“ während des Neu-Lesens – `AuditRead.WithoutTemplate` gilt jetzt wie ein Fehlschlag als unbekannt (Test ergänzt). Kommentar an `AuditUnread` versprach auch „nicht nach der jetzigen Vorlage gelesen“ – korrigiert (Variante a), der allgemeine Fall (Vorlage mit anderen Namen, während der Designer offen ist; war vorher schon so) steht im Backlog. Veraltete Beschreibungen (`_auditRead`, `_auditUnread` im Protokoll) nachgezogen.
- E2E (eigener Container, Proxy mit Modus „cut“/„delay“): Nach Lesefehler gelber Punkt, Chip „1 Warnung“, „Ausführen“ mit einer Änderung frei. Vorlage beim Öffnen kaputt (Haken frei, Hinweis „Vorlage“), dann durch die Standardvorlage ersetzt: Haken gesperrt, Hinweis, „Neu laden“, Warnung, Skript 0. „Neu laden“ mit 8 s verzögertem Lesen: Haken gesperrt, Chip „werden gelesen …“, Skript leer, „Neu laden“ weg (Stand vor dem dritten Durchgang; seitdem bleiben Hinweis und gesperrtes „Neu laden“ stehen – nur im Unit-Test geprüft); danach Haken an, 16 Schritte. Beim ersten Öffnen zeigt der Designer nichts, bevor das Audit gelesen ist – das Lade-Fenster betrifft nur „Neu laden“.
