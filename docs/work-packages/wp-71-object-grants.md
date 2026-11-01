# WP-71 Berechtigungen an Objekten → Release offen

Branch `wp/71-object-grants`, 2026-11-01. Frage des Nutzers („Kann man für Tabellen/Views/etc schon Berechtigungen verwalten?“), als Paket eingeplant am selben Tag. Vorher gingen `GRANT`/`REVOKE` nur von Hand im SQL-Editor (seit WP-22 als DDL).

## Planung (aus `docs/roadmap.md`)
- **Ansicht „Berechtigungen“ je Objekt** (Tabelle, View, MView, Sequenz, PL/SQL-Package/-Prozedur/-Funktion; als `TabMode`/`PlSqlView` wie „Synonyme“): je Zeile Empfänger (User, Rolle oder `PUBLIC`), Recht, vergeben von, `WITH GRANT OPTION`, `WITH HIERARCHY OPTION`; Spaltenrechte (`INSERT`/`UPDATE`/`REFERENCES` auf einzelne Spalten) aus `ALL_COL_PRIVS` mit der Spalte. `ISchemaReader`-Methode, Integrationstest mit einem zweiten User und einer Rolle.
- **Sichtbarkeit:** `ALL_TAB_PRIVS` zeigt nur Grants, bei denen die Session Owner, Vergebender oder Empfänger ist (auch über eine aktive Rolle). Bei Objekten eines fremden Schemas fehlen also Grants an Dritte – die Ansicht sagt das in einem Hinweis. Rechte über Rollen werden nicht aufgelöst; die Rolle steht als Empfänger.
- **Vergeben:** Empfänger, ein oder mehrere Rechte (passend zur Objektart), optional `WITH GRANT OPTION`, bei `INSERT`/`UPDATE`/`REFERENCES` optional Spalten. Empfänger aus `ALL_USERS` plus `PUBLIC`; Rollen lassen sich ohne `DBA_ROLES` nicht auflisten → freie Eingabe. Ein Statement je Empfänger, immer mit Owner.
- **Entziehen** je Zeile; die Bestätigung nennt die Kaskade über `GRANT OPTION` und dass Spaltenrechte nur für das ganze Objekt entzogen werden.
- **Ausführen** über `DdlDraftPanel` wie bei den Synonymen; DDL-Text nur in `Core/Ddl`; ein `GRANT` an `PUBLIC` sagt, dass es für alle Benutzer gilt.
- **Rechte** nicht vorab prüfen: ORA-01031, ORA-01720 und ORA-01927 verständlich melden.
- Kein ADR erwartet; prüfen, dass der Schemaweg `REVOKE` wie `GRANT` durchlässt.

## Entscheidungen des Nutzers (2026-11-01)
1. **Grants im DDL-Tab** aus der Liste der Ansicht gebaut, nicht über `DBMS_METADATA.GET_DEPENDENT_DDL` (braucht für fremde Schemas `SELECT_CATALOG_ROLE`, ohne Grants ORA-31608).
2. **Objektmenü:** ein Eintrag „Berechtigungen“, der die Ansicht öffnet.
3. **Liste „Berechtigungen im Schema“** in diesem Paket: eigener Tab je Workspace, Filter nach Empfänger, Schalter „Objekte ohne Recht für X“ (direkt oder über PUBLIC; Rollen zählen nicht).
4. **Nicht in diesem Paket:** Rechte für mehrere Objekte auf einmal (Backlog); Systemrechte und Rollen vergeben (Benutzerverwaltung, bleibt beim SQL-Editor, auch nicht im Backlog).
5. Plan bestätigt am 2026-11-01 („Passt so denke Ich.“) mit zwei Abweichungen von der Roadmap: **Sequenzen** haben keinen Tab, also keine Ansicht je Objekt – sie stehen nur in der Liste; **Vergeben und Entziehen auch direkt in der Liste** (dort ohne Spaltenauswahl, ein Objekt je Ausführung).

## Gemessen gegen Oracle 23 (eigener Container, vor dem Bau)
- Eigene Objekte: `ALL_TAB_PRIVS`/`ALL_COL_PRIVS` zeigen alle Grants. Fremdes Schema: nur die, an denen die Session beteiligt ist – als Empfänger, Vergebender, über eine **aktive** Rolle oder `PUBLIC`. Grants des Owners an Dritte, deren Spaltenrechte und Grants an eine Rolle, die der Login-User nicht hat, fehlen.
- `ALL_TAB_PRIVS` hat keine Spaltenrechte; `ALL_COL_PRIVS` eine Zeile je Spalte. Eine MView steht dort mit `TYPE = TABLE`. Zeilen mit `TYPE = USER` (`INHERIT PRIVILEGES` auf einen User) tragen den Usernamen als `TABLE_NAME` – herausgefiltert.
- Rollen stehen nicht in `ALL_USERS`.
- `GRANT` an sich selbst (den Owner) läuft in 23 ohne Fehler und ohne Zeile (ältere Versionen: ORA-01749); ein zweites gleiches `GRANT` ebenso. `GRANT … TO PUBLIC WITH GRANT OPTION` läuft.
- Abgelehnt: ohne Grant Option auf ein fremdes Objekt ORA-01031; View über eine Tabelle, die der Owner nicht weitergeben darf, ORA-01720; Rolle mit Grant Option ORA-01926; unbekannter Empfänger (auch `"klein"` gequotet) ORA-01917; `REVOKE` eines Grants, den ein anderer vergab **oder der gar nicht besteht**, ORA-01927; `REVOKE REFERENCES` bei einem FK des Empfängers ORA-01981; `REVOKE UPDATE (spalte)` ORA-01750; `EXECUTE` auf eine Tabelle ORA-02224; `READ` auf eine Sequenz ORA-02205.
- `REVOKE UPDATE` nimmt alle Spaltenrechte dieses Rechts und kaskadiert: Was der Empfänger mit Grant Option weitergab, verschwindet mit.

## Entscheidungen in der Umsetzung
- **Rechte je Art** (`GrantPrivileges.For`): Tabelle SELECT, READ, INSERT, UPDATE, DELETE, REFERENCES, ALTER, INDEX, DEBUG, FLASHBACK; View statt ALTER/INDEX MERGE VIEW; MView SELECT, READ; Sequenz SELECT, ALTER; PL/SQL EXECUTE, DEBUG. Weggelassen: ON COMMIT REFRESH, QUERY REWRITE, UNDER. Ein Integrationstest vergibt und entzieht jedes angebotene Recht.
- **Spaltenrechte eines Rechts als eine Zeile** (`ObjectGrant.Columns`), gruppiert nach Empfänger, Recht, Vergebendem und Optionen – so wie Oracle sie entzieht.
- **Privilegien nur als Schlüsselwörter** in GRANT/REVOKE (`OracleDdl.Privilege`: Großbuchstaben und einzelne Leerzeichen, sonst Ausnahme) – sie kommen aus der festen Liste oder dem Dictionary, nie getippt. Empfänger gequotet wie im Dictionary, `PUBLIC` als Schlüsselwort (gequotet wäre es ein User dieses Namens).
- **Empfänger:** Vorschläge über eine `datalist` (PUBLIC, Rollen aus den Grants des Objekts bzw. des Schemas, User). Eine Rolle ist, wer in den Grants vorkommt und kein User ist, oder ein Name, der kein User ist – dann ist die Grant Option ein Problem (ORA-01926). Ein Name, der weder User noch bekannte Rolle ist, ist nur eine Warnung (es kann eine Rolle sein, die hier noch nirgends vorkommt). Konnten die User nicht gelesen werden, wird nichts geprüft.
- **Owner als Empfänger** ist ein Problem („hat alle Rechte daran“) – Oracle 23 schluckt es still.
- **Hinweise vor dem Entziehen** (`GrantDraft.RevokeNotes`, im Panel und in der Bestätigung): alle Spalten (auch wenn derselbe Vergebende das Recht noch auf das ganze Objekt gab), Kaskade bei Grant Option, PUBLIC, anderer Vergebender, REFERENCES mit FKs (ORA-01981; `CASCADE CONSTRAINTS` löscht Constraints, nur im SQL-Editor). **Nach einer Ablehnung** (`GrantDraft.ErrorHint`) ein Satz zu ORA-01031, ORA-01720, ORA-01927, ORA-01981, ORA-01926.
- **Objektmenü:** Der Chip „Berechtigungen“ unter „Ansichten“ (aus `TabModeLabels`/`PlSqlTab.ViewsOf`) ist der Eintrag – kein eigener Menüpunkt daneben.
- **Liste:** ohne Filter alle Rechte im Schema (Objekte ohne Rechte erscheinen nicht – sie kämen zu Tausenden), mit Empfänger nur dessen Rechte; „Nur Objekte ohne Recht dafür“ zeigt die Objekte, an denen er weder direkt noch über PUBLIC ein Recht hat, mit „Recht vergeben“ (Empfänger vorbelegt). Die Objekte: dieselbe Liste wie der Explorer (`TablesSql`) plus Sequenzen und Packages/Prozeduren/Funktionen, in **einer** Abfrage mit den Rechten (LEFT JOIN). Ein Objektname öffnet die Ansicht „Berechtigungen“ seines Tabs (nicht bei Sequenzen).
- **Empfänger-Filter der Liste:** Tippen filtert, sobald der Name ein bekannter Empfänger ist (auch aus den Vorschlägen gewählt) oder das Feld leer ist; Enter und Verlassen des Felds nehmen jeden Namen (eine Rolle, die hier noch nirgends vorkommt). Gefunden im E2E: Mit nur `@onchange` wirkte Enter nicht.
- **Links** lassen die Ansicht „Berechtigungen“ weg wie „Synonyme“ (eine ältere Version lehnte den Link sonst ab).
- Kein ADR: kein neuer Weg zur Datenbank (Lese- und Schemaweg wie bisher; `GRANT`/`REVOKE` liefen dort schon, jetzt mit Test).

## Gebaut
- **Core:** `GrantDdl` (Grant, Revoke, Script), `OracleDdl.Grant`/`Revoke`, `DdlStepKind.Grant`; `ISchemaReader.GetGrantsAsync`/`GetSchemaGrantsAsync`/`GetUserNamesAsync` in `OracleSchemaReader.Grants.cs`, durchgereicht im `SchemaCache`; `ObjectGrant`, `SchemaObjectGrants`, `GrantObjectKind`, `GrantPrivileges`; `TabMode.Grants`, `PlSqlView.Grants`, `TabState.GrantList`; `ShortcutAction.SchemaGrants`; `TableLink` ohne die neue Ansicht.
- **UI-State:** `GrantDraft` (Entwurf, `RevokeNotes`, `ErrorHint`, `RolesOf`), `GrantListTab`, `ShellState.OpenGrantList`, `ShellActions`.
- **UI:** `GrantsView` (Tabelle, View, MView, PL/SQL außer Trigger), `GrantDraftForm`, `GrantRevokeSection`, `GrantTable`/`GrantRow`, `GrantListView`, `GrantScript` im `DdlView`; `TabView`, `PlSqlTabView`, `TabBar`, `Shell.razor`; Icon Lucide `key-round`; CSS `.grant-choices`, `.grant-filter`.
- **Tests:** Core `GrantDdlTests`, `TableLinkTests`, `ReadOnlyTests` (neue Lesemethoden); Integration `GrantTests` mit `GrantSchema` (Sichtbarkeit, Liste des Schemas, User ohne Rollen, jedes angebotene Recht, Entziehen je Objekt, die gemessenen Fehlercodes); UI `GrantDraftTests`, `GrantListTabTests`.

## E2E (2026-11-01)
Eigener Container `ferret-wp71` (Port 1534, Beispielschema plus die Messobjekte), eigene Instanz über `tools/e2e` (`--data-dir`, Port 9333), deutsch/dunkel, Passwort nicht gespeichert. Probe für eingeschleuste Eingaben ging durch.
- **Mit echter Windows-Eingabe:** Rechtsklick auf `G_OWN` im Explorer → Chip „Berechtigungen“ → Tab in der Ansicht mit den gemessenen Rechten; „Recht vergeben“, SELECT und UPDATE, Spalte NAME, Grant Option → `GRANT SELECT, UPDATE ("NAME") … WITH GRANT OPTION`, Bestätigung, Liste lädt neu (Oracle stufte das vorhandene SELECT zur Grant Option hoch); „Entziehen“ der UPDATE-Zeile mit beiden Hinweisen in Panel und Bestätigung; in der Liste „Nur Objekte ohne Recht dafür“ für `G_OTHER`, „Recht vergeben“ an `G_SEQ` mit vorbelegtem Empfänger, danach fehlt die Sequenz; Klick auf `G_PROC` öffnet dessen PL/SQL-Tab in „Berechtigungen“.
- **Per CDP:** REVOKE REFERENCES mit FK → ORA-01981 und Hinweis; View über eine Tabelle ohne Grant Option → ORA-01720 und Hinweis; Rolle mit Grant Option, unbekannter Name, Owner als Probleme bzw. Warnung; DDL-Tab mit den GRANT-Statements; Befehlspalette „>berechtig“; nach Neustart Liste samt Filter und Ansicht wiederhergestellt.
- **Gefunden und behoben:** Enter im Empfänger-Filter wirkte nicht (siehe oben); Objektnamen als Link standen eingerückt gegenüber Sequenzen ohne Link; der Hinweis zu den Spalten stand über den Checkboxen, das Label „Spalten“ daneben verrutscht.
- Log: nur die erwarteten Warnungen zu ORA-01981 und ORA-01720.
- **Nicht geprüft:** Prod-Verbindung (gesperrter Workspace: Panel zeigt die Sperre wie bei Designer und Synonymen), englische Oberfläche und helles Theme, Profil mit fremdem Schema (Hinweis nur per Code), große Schemas (Laufzeit der Liste; eine Abfrage mit LEFT JOIN, `FetchManyRows`).
