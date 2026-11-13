# Fix: Kopf eines Skripts im SQL-Editor (`@target` anlegen)

Branch `fix/script-header-in-editor` (von `main`), 2026-11-13 (Feedback zu WP-84).

## Anlass
Im SQL-Editor getippt (Beispiel neutral nachgestellt):
```sql
-- @param kunde_id number
-- @target kunde_id KUNDE.KUNDE_ID
SELECT * FROM v_auftrag WHERE kunde_von = :kunde_id;
```
und gespeichert. Im Zellmenü von `KUNDE.KUNDE_ID` erschien das Skript nicht. Erwartung des Nutzers: Skripte im SQL-Editor anzulegen ist der Normalweg, nicht über einen externen Editor.

## Ursache
- Der SQL-Tab zeigte nur den Text **unter** dem Kopf (WP-77); beim Speichern baute FerretSharp den Kopf selbst (Beschreibung aus dem Dialog, Parameter aus Binds und Variablen) und schrieb ihn mit einer Leerzeile **vor** den Editortext. Getippte `-- @…`-Zeilen landeten so unter der Leerzeile, also im SQL – das Ziel wurde nie gelesen, ohne Hinweis.
- Ein Weg, ein `@target` in der App anzulegen, fehlte: Geplant war in WP-84 „Ziel beim Speichern vorschlagen“, das ging in den Backlog; geprüft wurde nur mit vorbereiteten Dateien. Beim Abschluss von WP-84 nicht ausdrücklich gesagt.

## Entscheidung des Nutzers (2026-11-13)
**Kopfzeilen sichtbar im Editor** (statt nur beim Speichern übernehmen oder Feld im Speichern-Dialog): `@target` und Zeilen, die FerretSharp nicht kennt, stehen oben im Tab-Text; geändert und gelöscht wird dort. `@param` bleibt in den Variablen, `@description` im Speichern-Dialog – getippte werden übernommen.

## Gebaut
- Core `ScriptHeaders.ForSave(text, variables, fileHeader, headerInText)` → `ScriptSave` (Kopf, SQL, Tab-Text danach, Variablen, Fehler): Die `-- @…`-Zeilen oben im Text sind der Kopf. `@param` gibt Typ und `list` (die Variable übernimmt den Typ, ihr Wert bleibt), `@description` die Beschreibung – beide verlassen danach den Text. `@target` wird gegen die Binds des SQL geprüft, nicht gegen `@param`-Zeilen des Texts (die stehen ja nicht mehr dort). Lesen der Kopfzeilen als `ReadLines` + `Checked` aus `Parse` gezogen, gemeinsam für Datei und Editor.
- `ScriptHeaders.EditorText(header, body)`: was der Tab zeigt – Ziele, dann die unbekannten Zeilen, Leerzeile, SQL. Öffnen (`ShellState.OpenScript`, `OpenScriptRun`) und Neuladen zeigen ihn.
- `ScriptLink.HeaderInText` (init, fehlt in älteren Workspace-Dateien → false): Mit true ist der Text die Wahrheit (eine gelöschte Zeile ist weg); ein Tab aus einer älteren Workspace-Datei behält beim Speichern die Ziele und Zeilen der Datei, sie kommen dabei in seinen Text. `SavedHash` ist der Hash des gezeigten Texts. `HeaderFor` → `SaveOf`.
- `OpenScriptRun` vergleicht den verbundenen Tab nur über den SQL-Teil (`Parse(open.Text).Body`) mit der Datei – mit und ohne gezeigte Kopfzeilen.
- UI `SqlView.Scripts`: Speichern (verbunden und Dialog) schreibt Kopf und SQL aus `ScriptSave`, setzt Text und Variablen des Tabs (`ShowSavedAsync`; der Text ändert sich nur, wenn Zeilen ihn verlassen oder dazukommen – sonst bleibt der Cursor) und meldet unbrauchbare Kopfzeilen sofort in der Leiste über dem Editor (`Notice_HeaderErrors`, Zeilennummer im Tab-Text), statt sie nur im Tooltip des Explorers zu zeigen. `ScriptSaveDraft` füllt die Beschreibung aus getipptem `@description`, sonst aus der Datei.

## Geprüft
- Unit-Tests: der Fall des Nutzers (getippter Kopf → Ziel in der Datei, Rundreise über `Compose`/`Parse`), Tab aus älterem Workspace (Ziele bleiben und kommen in den Text, `list` bleibt), gezeigter Kopf speichert unverändert, gelöschte Zeile ist weg, Ziel ohne Bind mit Zeilennummer gemeldet.
- E2E (2026-11-13, eigener Container `ferret-fixhdr`, Klicks auf Verbindung, Zelle, Menüeintrag, Tab und Explorer mit echter Windows-Eingabe): Fall des Nutzers getippt (`@param` + `@target` ohne Leerzeile über dem SELECT) und über den Dialog gespeichert → Datei mit Ziel im Kopf, Tab zeigt nur noch die Zielzeile über dem SQL, ohne „●“; Rechtsklick auf `KUNDEN.KUNDE_ID` zeigt das Skript unter *Skripte*, der Klick nimmt den verbundenen Tab (Text mit Zielzeile) und führt ihn mit dem Wert aus; Ziel auf einen fehlenden Bind → Hinweis mit Zeile 1, Zeile bleibt in Datei und Tab; Tab geschlossen und aus dem Explorer neu geöffnet → Zielzeile steht oben; Zeile gelöscht und gespeichert → Ziel aus der Datei. Log ohne `[ERR]`/`[WRN]`.
- Nicht im E2E: Tab aus einer älteren Workspace-Datei (nur Unit-Test), der neue Wortlaut „das SQL hat keinen Bind“ (nach dem E2E geändert, Unit-Test).

## Offen
- Beim Ausführen laufen die `-- @target`-Zeilen als Kommentar vor dem ersten Statement mit; die Zeile über dem Ergebnis und der Verlauf zeigen sie vorn (`-- @target … SELECT …`). Oracle stört das nicht – Frage an den Nutzer, ob führende Kopfzeilen dort ausgeblendet werden sollen.
- „Ziel beim Speichern vorschlagen“ (aus `WHERE t.x = :x` erraten) bleibt im Backlog.

## Nachtrag: Code-Review (2026-11-13)
Separater Reviewer, zwei Runden plus Nachprüfung der Fixes. Entscheidung des Nutzers zur ersten Runde: „Alle beheben, bei 6 bitte komplett ersetzen, auch aus der Branch-Historie.“ Zur zweiten Runde: „Alle beheben“.
- 1 (M) Tippen während des Speicherns (Freigabe über VPN) wurde zurückgesetzt, Variablenwerte überschrieben: behoben – der Tab bekommt den umgebauten Text nur, wenn der Editor nach dem Schreiben noch den gespeicherten hält (`ScriptSave.TextFor`); auf die Variablen kommen nur die Typen getippter `@param` (`Typed`), auf den Stand nach dem Schreiben. Test `After_writing_the_tab_gets_the_rebuilt_text_only_…`. Nicht im E2E nachgestellt (lokal schreibt die Platte zu schnell).
- 2 (M) Getipptes `@param` ohne Bind verschwand still: behoben – Meldung `ParamUnknownBind`, die Zeile bleibt im Tab. Test `A_typed_param_without_its_bind_…`.
- 3 (K) Doppeltes `@param` der Datei drehte beim Speichern still den Typ: behoben – `@param`/`@description`-Zeilen aus `Other` zeigt der Tab nicht (`ShownInText`), sie bleiben in der Datei. Test `A_param_of_the_file_kept_with_errors_…`.
- 4 (K) Zeilennummer im Hinweis zeigte nach dem Umbau auf eine andere Zeile: behoben – der Hinweis nennt die Zeile selbst (`Problems`, `HeaderLineProblem`).
- 5 (K) Doku und ADR hinkten hinterher: behoben – `docs/sql-scripts.md` an vier Stellen, Nachtrag in ADR 0046.
- 6 (M, beim Nachprüfen gefunden) Das Protokoll zitierte Tabellen- und Spaltennamen aus der echten DB des Nutzers: behoben – neutrales Beispiel; der Doku-Commit wurde ersetzt und der Branch per Force-Push überschrieben (auf Wunsch des Nutzers, auch aus der Branch-Historie).
- Zweite Runde, neu durch die Fixes:
  - N1 (M) Das gemeldete `@param` ohne Bind wurde in die Datei geschrieben, dort als echter Parameter gelesen (das Zellmenü fragte nach ihm, nach dem Neuöffnen verwaiste Variable, beim nächsten Speichern still weg): behoben – es bleibt nur im Tab, der Hinweis sagt „nicht gespeichert“. Test mit Rundreise über die Datei. Im E2E bestätigt; eine solche Zeile aus dem Zwischenstand vor diesem Fix blieb im Kopf eines wiederhergestellten Tabs stehen (nie veröffentlicht).
  - N2 (M) Ein Tab aus einem älteren Workspace, in den beim ersten Speichern getippt wurde, galt als „Text ist die Wahrheit“ ohne die Zielzeilen – das nächste Speichern hätte die Ziele der Datei verloren: behoben – `ScriptSave.LinkAfter` lässt `HeaderInText` dann aus. Test im Workspace-Test.
  - N3 (K) Ausgeblendete `@param`-Fehlerzeilen ließen sich in der App nicht loswerden: behoben – ein getipptes `@param` desselben Namens ersetzt sie.
  - N4 (K) Review fehlte im Protokoll: dieser Abschnitt.
- Dritte Runde: N1–N3 gedeckt. Entscheidung des Nutzers: „Alle beheben“.
  - K1 (K) Doku nannte die Ausnahme für `@param` ohne Bind nicht: behoben.
  - K2 (K) Ein nicht gespeichertes `@param` stand ohne „●“ im Tab, ein Pull lud es still weg: behoben – der Link hasht den Text ohne die nicht geschriebenen Zeilen (`ScriptSave.SavedText`). Im E2E bestätigt („●“ nach dem Speichern).
  - K3 (K) Ausgeblendete `@param`-Zeilen ohne Namen oder ohne Bind blieben unlöschbar: behoben – sie fallen beim Speichern weg (ohne Wirkung).
- Vierte Runde: K1–K3 gedeckt. Optional offen (dem Nutzer vorgelegt): R1 – „●“ bleibt nach dem Löschen einer nicht gespeicherten Zeile stehen, wenn der Kopf ohne Leerzeile getippt war (kosmetisch, verschwindet beim nächsten Speichern); R2 – ADR 0046 nennt den Wegfall wirkungsloser `@param`-Zeilen nicht.
- Vorher schon da (Backlog): Ein unverbundener Tab aus dem Zellmenü (anderer Text im verbundenen) bekommt nur das SQL ohne Ziele; „Als Skript speichern“ aus ihm verliert sie.
