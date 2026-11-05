# Klein: Spalten als ALTER TABLE in eine andere Datenbank übertragen → Release 3.48.0

Branch `feat/column-add-ddl` (von `main`), 2026-10-23.

## Anlass
Wunsch des Nutzers beim Planen von WP-61: in der Spaltenansicht eine oder mehrere Spalten markieren und dafür das `ALTER TABLE` erzeugen – vor allem, um Spalten in eine andere Datenbank zu übertragen.

Entscheidungen des Nutzers:
- Zweck: Spalten übertragen, also `ADD` und Kommentare. Keine FKs, Indizes oder Constraints.
- Ein eigenes kleines Paket mit den Vorschlägen aus der Machbarkeitsprüfung: Kopieren und „im SQL-Editor einer anderen Verbindung öffnen“, Owner im Ziel wie bei Links.

Ohne Rückfrage entschieden (Fragen unten):
- **Ein `ALTER TABLE … ADD` je Spalte** statt eines gemeinsamen `ADD (a, b)`: Das Skript besteht aus den Schritten von `ColumnChange` (wie Schema-Vergleich und Designer), jede Spalte behält ihre eigene Warnung, und scheitert eine (Spalte gibt es schon), laufen die anderen im SQL-Editor trotzdem. Reihenfolge nach Position im Schema – eine virtuelle Spalte steht hinter ihren Basisspalten –, Kommentare zuletzt (`DdlScript.Order`).
- **Owner im Ziel:** die passende Tabelle dort, gefunden wie ein Link sie findet (`ShellState.LinkedTable`: gleicher Owner, gleicher Name im Schema der Verbindung, Synonym). Ist die Verbindung nicht offen oder findet sich nichts, der Name im Schema, das die Verbindung zeigt (`EffectiveSchema`). Den reinen Tabellennamen ohne Owner habe ich verworfen: Die App setzt kein `CURRENT_SCHEMA`, er würde im Schema des angemeldeten Users gesucht – bei einem User mit reinen SELECT-Grants und `DefaultSchema` daneben.
- **Ziele:** alle anderen Verbindungen in der Reihenfolge der Liste, offen oder nicht – wie der Vorschlag des Schema-Vergleichs: `ShellState.OpenSqlIn` und `WorkspaceLifecycle.ConnectAsync` zeigen und verbinden sie. Prod ohne zusätzliche Sperre: Das Skript läuft erst, wenn der Nutzer es dort ausführt, über den Schemaweg mit Bestätigung (und ist dort nur nach dem Freischalten möglich).
- **Auswahl** nur in der Spaltenansicht einer Tabelle (nicht einer View): Klick, Ctrl+Klick, Shift+Klick, Rechtsklick außerhalb der Auswahl wählt nur diese Zeile. Zeilen, die der Filter ausblendet, zählen nicht mit. Neben der Überschrift steht „3 Spalten gewählt – Rechtsklick für ALTER TABLE“, sobald etwas gewählt ist (ein `title` an der ganzen Tabelle hätte über jeder Zelle aufgeploppt).

## Gebaut
- Core `AddColumnsScript.Write(source, target, columns)` (`Core/Ddl`): Kopfzeile mit der Quelltabelle, Schritte aus `ColumnChange.Steps(target, null, column, Known)`, `DdlScript.Order` und `Render`. Kein neuer DDL-Text.
- UI State `ListSelection` (Auswahl in einer Liste ohne Grid: Klick, Ctrl, Shift mit Anker, Rechtsklick, `In(shown)`), `AddColumnsTargets` (`Of`: andere Verbindungen; `Table`: Tabelle im Ziel). `ShellState.LinkedTable` gibt es zusätzlich für eine `TableRef`.
- `ColumnsView`: Zeilen wählbar (`table.columns.pickable`, `tr.selected`), `PopoverMenu` mit „Als ALTER TABLE … ADD kopieren“ und „Im SQL-Editor öffnen von“ (je Verbindung mit Art-Kennung, Tooltip nennt die Zieltabelle). `dom.js clearTextSelection`: Shift+Klick zog sonst die Textauswahl des Browsers über die Zeilen.
- Tests: Core `AddColumnsScriptTests`; UI `ListSelectionTests`, `AddColumnsTargetsTests`.

## E2E (2026-10-23)
Eigene Instanz (`--data-dir`, eigener WebView2-Ordner, deutsch, dunkel), eigener Container `ferret-e2e-coladd` mit dem Beispielschema und einem zweiten User `FERRET2`, dessen `KUNDEN` nur `KUNDE_ID` und `NAME` hat (danach entfernt). Zwei Verbindungen auf denselben Container: „E2E Quelle“ (FERRET, Test) und „E2E Ziel“ (FERRET2, Dev). Bedient per CDP, Passwort ohne Credential Manager.
- Spaltenansicht KUNDEN: Klick, Shift+Klick, Ctrl+Klick wählen wie erwartet, ohne Textauswahl des Browsers; Hinweis „4 Spalten gewählt – Rechtsklick für ALTER TABLE“.
- „Als ALTER TABLE … ADD kopieren“: Skript mit Kopfzeile, vier `ADD` (CHAR(3 BYTE), `DEFAULT SYSDATE NOT NULL`, NUMBER(12,2), `DEFAULT 0 NOT NULL`) und drei Kommentaren; Hinweis in der Statusleiste.
- „Im SQL-Editor öffnen von E2E Ziel“, Ziel nicht verbunden: Tooltip und Skript nennen `FERRET2.KUNDEN` (Schema der Verbindung), Passwortabfrage, SQL-Tab dort mit dem Skript. „Skript“ ausführen → Bestätigung mit 7 Statements → in `USER_TAB_COLUMNS`/`USER_COL_COMMENTS` von FERRET2 alle vier Spalten mit Default, NOT NULL und Kommentaren.
- Ziel verbunden: Zuordnung über das geladene Schema, ebenfalls `FERRET2.KUNDEN`. Rechtsklick außerhalb der Auswahl wählt nur diese Zeile. Die Auswahl bleibt beim Wechsel der Verbindung.
- Log ohne `[ERR]`/`[WRN]`.
- **Nicht mit echter Eingabe geprüft:** Der Klick per `SendInput` kam nicht als `mousedown` im WebView an (wie in `e2e-testing.md` beschrieben), nach der Regel dort nicht weitergeklickt. Klick mit Ctrl/Shift und Rechtsklick sind gewöhnliche `click`/`contextmenu` auf Tabellenzeilen, wie im Beziehungsdiagramm.

## Offen / Fragen
- Ob die Spalte im Ziel schon existiert, prüft das Skript nicht – Oracle meldet ORA-01430 beim Ausführen, für den echten Abgleich bleibt der Schema-Vergleich.
- Der Eintrag im Spaltenkopf-Menü des Grids (in WP-58 markierte Spalten) kommt mit WP-61 (dort vermerkt).
- Tastatur in der Spaltenansicht (Ctrl+A, Pfeile mit Shift) gibt es nicht; bisher nur Maus.

## Nachtrag: Fremdschlüssel (Branch `feat/add-columns-foreign-keys`, 2026-11-04)
Wunsch des Nutzers: Das Skript soll den FK der Spalten mitnehmen (bisher bewusst nur `ADD` und Kommentare, siehe oben).

Ohne Rückfrage entschieden:
- **Nur FKs, deren Spalten alle gewählt sind.** Ein FK mit einer nicht gewählten Spalte bleibt still draußen: Die Spalte fehlt im Ziel womöglich, und der Nutzer hat sie bewusst nicht gewählt. PK, UNIQUE und CHECK kommen nicht mit (gefragt war der FK).
- **Zieltabelle des FK** wie im Schema-Vergleich (`DdlWriter.MapTable`): eine Tabelle im Owner der Quelle → gleicher Name im Owner des Ziels; die Quelltabelle selbst (Selbstbezug) → die Zieltabelle; ein fremder Owner bleibt. Ob die Elterntabelle im Ziel existiert, prüft das Skript nicht (ORA-00942 beim Ausführen).
- **Constraint-Klausel** aus `OracleDdl.ConstraintClause`: Name (außer `SYS_C…`), `ON DELETE`, `DEFERRABLE`, `DISABLE`/`NOVALIDATE` wie in der Quelle. Stufe `AddForeignKey` – nach den Spalten, vor den Kommentaren.
- **Warnung** „Prüft die vorhandenen Zeilen …“ (Risiko `MayFailOnValues`) nur, wenn eine FK-Spalte durch Default, Identity oder Ausdruck sofort Werte bekommt und der FK aktiv und validiert ist – neue Spalten sind sonst NULL, der FK prüft nichts.
- **Constraints lesen erst beim Klick** (Kopieren oder Verbindung), über `Shell.RunDbAsync` und `SchemaCache.GetConstraintsAsync`: Das Spaltenkopf-Menü jedes Grids zeigt dieselben Einträge, ein Lesen beim Öffnen hätte jedes Öffnen eine Abfrage gekostet. Scheitert das Lesen, entsteht das Skript ohne FKs mit einer zweiten Kopfzeile „Ohne Fremdschlüssel: …“ (`CompareText.AddColumnsWithoutForeignKeys`); schließt das Menü während des Lesens, passiert nichts.

Gebaut: `AddColumnsScript.Write(source, target, columns, constraints)` (null = nicht lesbar), `AddColumnsMenuItems` liest die Constraints. Tests: `AddColumnsScriptTests` (FKs mit Owner-Zuordnung, Selbstbezug, fremdem Owner, teilweise gewählten Spalten; Warnung bei Default; Kopfzeile ohne Constraints).

E2E (2026-11-04): eigene Instanz (`--data-dir`, deutsch, dunkel), eigener Container `ferret-e2e-fk` (Port 1531, danach entfernt) mit dem Beispielschema, dazu in `FERRET.RECHNUNG` ein Selbstbezug `STORNO_VON` (`ON DELETE SET NULL`) und `LAND` mit `DEFAULT 'DE'` und FK auf `LAND_IOT`, und ein User `FERRET2` mit den Elterntabellen und einem `RECHNUNG` ohne diese Spalten. Verbindungen „E2E Quelle“ (FERRET) und „E2E Ziel“ (FERRET2), Passwort ohne Credential Manager, bedient per CDP (Klicks über `Input.dispatchMouseEvent`).
- Spaltenansicht `RECHNUNG`, vier FK-Spalten gewählt, „Kopieren“: vier `ADD`, danach `FK_RECHNUNG_AUFTRAG`, `FK_RECHNUNG_KUNDE`, `FK_RECHNUNG_LAND` (mit „⚠ Prüft die vorhandenen Zeilen …“) und `FK_RECHNUNG_STORNO … ON DELETE SET NULL`, alle unter `FERRET`; die Windows-Zwischenablage hatte das Skript.
- „Im SQL-Editor öffnen von E2E Ziel“: dasselbe Skript mit `FERRET2` für Tabelle und Elterntabellen; „Skript“ → Bestätigung mit 8 Statements → alle liefen, in `ALL_CONSTRAINTS` von FERRET2 vier FKs `ENABLED`/`VALIDATED`, `SET NULL` am Selbstbezug, `LAND` mit `'DE'` gegen die Zeile im Ziel geprüft.
- `LIEFERUNG`: nur `POS_NR` gewählt → kein FK; mit `AUFTRAG_ID` → `FK_LIEF_POS (AUFTRAG_ID, POS_NR)`. Ebenso im Spaltenkopf-Menü des Grids mit zwei per Ctrl+Klick markierten Spalten.
- Container bei offenem Menü gestoppt, dann „Kopieren“: Skript ohne FK mit „-- Ohne Fremdschlüssel: Die Constraints der Tabelle ließen sich nicht lesen.“; im Log nur die erwartete Warnung ORA-03135.
- Nicht mit echter Windows-Eingabe geprüft: Am Klickweg des Menüs hat sich nichts geändert, nur was der Klick danach liest.

## Nachtrag: Code-Review (2026-11-04)
Reviewer als eigener Agent über `c2e752c3..88d3cd06`, Befunde nachgeprüft. Entscheidung des Nutzers: „Alle, 8 nur Hinweis“.
1. M – Kopieren nach dem Lesen über `navigator.clipboard` (scheitert, wenn das Fenster nicht vorn ist): behoben, jetzt `IClipboardService`; das Menü schließt vor dem Schreiben.
2. M – Klick wartete ohne Rückmeldung auf die Explorer-Session: behoben, Chips deaktiviert und „Liest die Fremdschlüssel …“ während des Lesens (`SchemaViewText.Columns_ReadingForeignKeys`).
3. K – Warnung bei zusammengesetztem FK schon, wenn eine Spalte einen Default hat (Oracle prüft keine Zeile mit NULL im Schlüssel): behoben, nur wenn alle Spalten sofort Werte bekommen; `[Theory]` scheitert ohne Fix.
4. K – Tooltip des Kopieren-Chips ohne FKs: behoben (en, de).
5. K – Tabelle und Spalten erst nach dem `await` gelesen (neue `Details` dazwischen): behoben, vorher festgehalten.
6. K – FK ohne lesbare Elterntabelle fiel still weg: behoben, Hinweis am Ende des Skripts (`AddColumnsForeignKeyUnreadable`), auch bei ungleich vielen referenzierten Spalten; Test scheitert ohne Fix.
7. K – Testlücken: ergänzt (zusammengesetzter FK in eigener Spaltenreihenfolge mit `DEFERRABLE INITIALLY DEFERRED DISABLE VALIDATE`, Warnung bei Identity/virtueller Spalte, keine Warnung bei `DISABLE`/`NOVALIDATE`).
8. K – Selbstbezug auf eine mitgewählte Schlüsselspalte, der Schlüssel fehlt im Skript (ORA-02270): nur Hinweis, wie entschieden – Warnung „Braucht im Ziel einen Primär- oder eindeutigen Schlüssel auf (…)“ über dem FK, Risiko bleibt `Safe`.
9. Verworfen – „abgelehntes Lesen ohne Spur“: Das Schema-Lesen der Explorer-Session hat keinen Ablehnungsweg.

Zweiter Durchgang über die Fix-Commits, vier neue Befunde. Entscheidung des Nutzers: „Alle vier“.
10. M – Hinweise und Warnungen wurden nur an LF getrennt; ein einzelnes CR (oder NEL, LS, PS) in einem gequoteten Namen aus dem Dictionary machte im SQL-Editor den Rest des Namens zur ausführbaren Zeile – auch bei den Hinweisen von Vergleich und Designer: behoben in `DdlScript` (Kommentare an jedem Zeilenende getrennt, ausführbare Statements unverändert, damit Literale ihre Werte behalten); `[Theory]` über CR, NEL, LS, PS scheitert ohne Fix.
11. K – Ein `disabled` gewordener, fokussierter Chip gab den Fokus an die Seite ab, Esc erreichte das Menü während des Lesens nicht (im E2E nachgestellt: Fokus auf `BODY`, Menü blieb offen): behoben mit `aria-disabled` und Sperre im Handler; im E2E bleibt der Fokus am Chip, Esc schließt das Menü, nichts wird kopiert.
12. K – Der Theory-Fall „virtual“ prüfte den `IsVirtual`-Zweig nicht (eine virtuelle Spalte trägt ihren Ausdruck als Default): Zweig und Fall entfernt.
13. K – Der Hinweis bei ungleich vielen referenzierten Spalten war ungetestet und nannte nur die Elterntabelle: Text neutral („Elterntabelle oder ihre Schlüsselspalten“, en und de), Fall im Test.

E2E der UI-Fixes (eigene Instanz, Container `ferret-e2e-fk` neu angelegt und danach entfernt, verzögernder TCP-Proxy 0,4 s je Paket vor einer dritten Verbindung): Während des Lesens beide Chips deaktiviert und der Hinweis im Menü, nach gut 4 s schließt das Menü, das Skript liegt mit Umlauten in der Windows-Zwischenablage, darin die Warnung zum fehlenden Schlüssel. Log ohne `[ERR]`/`[WRN]`.

Offen: Ob der FK eine Elterntabelle anderen Namens im Ziel (Synonym) braucht, wie `AddColumnsTargets.Table` sie für die Zieltabelle sucht – bisher nur über den Owner.
