# WP-52 Warten und Fortschritt laufender Statements → Release 3.39.0
Branch `wp/52-waits-and-progress`. ADR 0027. Aus der Ideensammlung vom 2026-10-17 (Oracle-Funktionen, die FerretSharp noch nicht nutzt), vom Nutzer als Paket übernommen und gleich vorgezogen.

## Planung (aus `docs/roadmap.md`)
- Nur lesend über eine andere Session als die beschäftigte Workspace-Session; Leserechte wie der Sperrkonflikt-Dialog, ohne Rechte still weglassen.
- **Wer blockiert mich:** `BLOCKING_SESSION`, Wartegrund und Dauer in der Laufanzeige, eigene Workspaces erkennen und dorthin springen.
- **Fortschritt:** `V$SESSION_LONGOPS` als Balken mit Restzeit.
- Takt alle 2–3 s ab einer Schwelle von etwa 3 s.

## Entscheidungen (in der Umsetzung, einfachste Variante – Fragen unten)
- **Explorer-Session** statt einer eigenen (Vorschlag der Roadmap war eine eigene): kein weiterer Verbindungsaufbau über das VPN, kein eigener Lebenszyklus. Ein Blick wartet hinter einem laufenden Schema-Aufruf.
- **Kennung in `CLIENT_INFO`** statt SID nach dem Öffnen: kein zusätzlicher Roundtrip, und eine sperrende Session ist eindeutig ein eigener Workspace (MODULE/ACTION haben Kollegen genauso).
- Schwelle 3 s, Takt 2 s nach jeder Antwort, fest (keine Einstellung).
- **Ohne Rechte** nennt ein schreibendes Statement die anderen Workspaces der Verbindung mit nicht committeten Änderungen – lesende Statements warten in Oracle nie auf Sperren.
- Umfang: `RunProgress` (SQL-Editor, LINQ-Konsole, DDL-Entwürfe des Designers). Grid-Flush, COUNT und FK-Counts später (Backlog).

## Gebaut
- **Core:** `OracleSessionDefaults.NewSessionTag`, `IDatabaseConnection.SessionTag`, `SessionContext.ClientInfo` belegt (`OracleDatabaseConnector`). `ISchemaReader.GetSessionActivityAsync` (`OracleSchemaReader.Diagnostics`): eine Abfrage über `V$SESSION` (Session per Kennung, blockierende Session nur auf derselben RAC-Instanz, Tabelle über `ROW_WAIT_OBJ#`) und `V$SESSION_LONGOPS` (`OUTER APPLY … FETCH FIRST 1 ROW ONLY`); `SessionActivity`, `SessionWait`, `LongOperation` in `Core/Data`. `SchemaCache` merkt sich fehlende Rechte je Verbindung (`SessionActivityUnavailable`). `LockHolder.ClientInfo`, auch in der Abfrage des Sperrkonflikt-Dialogs. `WorkspaceManager.SessionTagOf`/`FindBySessionTag`.
- **UI:** `SessionWatch` (Takt, ein Blick zur Zeit, Ende mit dem Statement, `Unavailable`; loggt fehlende Rechte einmal je Verbindung), `SessionActivityText` (Texte, `OwnOf` über alle offenen Verbindungen), `ConnectionHub.FindSession`/`ShowWorkspace` (der Änderungs-Panel nutzt es jetzt auch), `TimeText.Clock` (vorher in `RunProgress`). `RunProgress` erbt von `FerretComponent`, neuer Parameter `MayWaitForLock` (SQL-Editor: alles außer Abfragen, LINQ-Schreiben, DDL-Entwürfe). `LockHolderList` nennt eigene Workspaces („Dein Workspace „X“ [in Verbindung]“). CSS `.run-wait`, `.run-longop`, `.run-bar`.
- **Tests:** Core `SessionActivityTests` (Kennung, Prozent, Rechte einmal), `WorkspaceManagerTests` (Kennung findet Workspace), `ReadOnlyTests` (neue Methode, SQL passiert die Lesesperre); UI `SessionWatchTests` (falsche Uhr: Schwelle, kein zweiter Blick ohne Antwort, Ende, Abbruch), `SessionActivityTextTests` (Deutsch/Englisch, eigene Workspaces auch in anderer Verbindung); Integration `EditingTests` (Sperrhalter mit Kennung, wartendes UPDATE nennt den Halter samt Tabelle, gegen Oracle 23).

## E2E (2026-10-17)
Eigene Instanz (`--data-dir`, eigener WebView2-Ordner, deutsch), eigener Container `ferret-wp52` (Port 1532, danach entfernt), Passwort nicht gespeichert, CDP.
- **Ohne Rechte:** UPDATE in Workspace 2 auf eine Zeile, die Workspace 1 geändert hat → nach 3 s „Falls es auf eine Sperre wartet: „Workspace 1“ hat nicht committete Änderungen – dort Commit oder Rollback.“ Abbrechen funktioniert („Abgebrochen – das Statement wurde zurückgenommen.“).
- **Mit `SELECT_CATALOG_ROLE`** (nach Neuverbinden): „Wartet auf eine Sperre auf KUNDEN, gehalten von deinem Workspace „Workspace 1“ · 0:05“ mit „Workspace zeigen“; der Knopf springt zu Workspace 1, nach dessen Rollback läuft das UPDATE durch.
- **Fortschritt:** Full Scan über eine 350-MB-Tabelle mit teurem `REGEXP_LIKE` → „Table Scan FERRET.WP52_BIG: 4 % · noch etwa 8 min 52 s“ mit Balken, hell und dunkel; Abbrechen danach nach einigen Sekunden.
- Log ohne `[ERR]`.
- **Nicht geprüft:** echte Windows-Eingabe (`SendInput`) auf „Workspace zeigen“ – sie kam nicht im Fenster an (kein `mousedown`, `SendInput` meldete Erfolg; der Rechner war vermutlich gesperrt, der Nutzer per Remote Control dabei). Geprüft per CDP-Eingabe; der Knopf ist ein gewöhnliches `@onclick` ohne Doppel- oder Mittelklick. Eine Sperre aus einer zweiten Verbindung (Workspace in anderer Verbindung) nur per Unit-Test.

## Offen / Fragen an den Nutzer
- Eigene Session statt der Explorer-Session, falls die Anzeige hinter Schema-Aufrufen zu spät kommt.
- Schwelle und Takt als Einstellung?
- Anzeige für Statements in Workspaces im Hintergrund (Statusleiste).
