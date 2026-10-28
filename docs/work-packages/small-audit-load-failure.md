# Klein: Audit-Zustand im Designer nicht gelesen

Branch `fix/audit-load-failure` (von `main`), 2026-10-28.

## Anlass
Im Code-Review von `small-audit-create-setting.md` als „vorher schon da“ gefunden: Scheiterte im Tabellen-Designer das Lesen von Audit-Tabelle und Trigger (Verbindung weg), galt das wie „keine Audit-Tabelle“ (`AuditState.None`). Aus der Audit-Zeile geöffnet und mit „Audit-Tabellen anlegen“ an plante der Designer dann `CREATE TABLE` für die vorhandene Audit-Tabelle – das Skript scheiterte mit ORA-00955, nachdem die Schritte der Basistabelle schon gelaufen waren. Außerdem setzte `WithAudit ??= false` den Haken für den ganzen Entwurf fest auf aus.

Entscheidung des Nutzers: gleich nach dem Merge von #81 als eigener Fix, Variante a – „Haken gesperrt mit Tooltip, daneben Neu laden“ (statt b: Haken bedienbar, Problem im Plan sperrt „Ausführen“).

## Gebaut
- UI State `ColumnDesigner`: `hasAuditTable` ist `bool?`, null = nicht gelesen. `CanAudit`/`AuditTakesPart` sind dann false, `AuditAtStart` liefert null (der Haken bleibt unentschieden und startet, sobald gelesen ist).
- `TableDesigner`: `_auditUnread`, wenn `AuditLoader.LoadAsync` nicht gelingt; `HasAuditTable` null. Reiter „Audit“: Haken gesperrt, Hinweis mit Warnsymbol und Tooltip `Audit_DesignerUnread`, daneben „Neu laden“ (`CommonText.Reload`, liest erneut mit der Explorer-Session).
- Text `Audit_DesignerUnread` Englisch und Deutsch.
- Test: UI `ColumnDesignerTests.An_audit_state_that_could_not_be_read_keeps_the_box_out_and_undecided`.

## E2E
2026-10-28, eigener Container, Standardvorlage, CDP. Den Lesefehler genau beim Audit stellt ein TCP-Proxy im Scratchpad nach, der die Verbindung beim ersten Paket mit `all_trigger_cols` trennt (nur, solange eine Flag-Datei liegt) – Constraints und Indizes liest der Designer nicht aus dem Cache, ein Abbruch per `KILL SESSION` trifft deshalb erst sie.
- AUFTRAG mit Audit-Tabelle, Designer über „Abgleichen“: Haken leer und gesperrt, Hinweis und „Neu laden“, Skript 0 Schritte (vorher: `CREATE TABLE AUDIT_AUFTRAG`). „Neu laden“ ohne Verbindung: bleibt so, keine Fehlerseite.
- Nach „Neu verbinden“ (baut die Tabs neu auf, ein Designer ohne Änderungen kommt nicht wieder) und erneutem „Abgleichen“: Haken an, 16 Schritte.
- Nicht geprüft: „Neu laden“ mit Erfolg im selben Designer – ein abgebrochenes Lesen beendet die Session, und „Neu verbinden“ baut den Designer neu auf. Der Weg ist derselbe wie beim Öffnen.

## Offen
- Der Haken heißt im Fehlerfall „Audit-Tabelle und Trigger anlegen“ (wie ohne Audit-Tabelle); der Hinweis daneben sagt, warum er gesperrt ist.
