# WP-63 Audit-Tabellen lesen wie ihre Basistabelle

Branch `wp/63-audit-view`, begonnen am 2026-10-27. ADR 0036. Baut auf WP-26 (Vorlage, Zuordnung über die Namensregel) und WP-51 (Vorlage je Verbindung) auf; die Hervorhebung folgt dem Muster der Anzeigespalten (WP-31).

## Wünsche des Nutzers (2026-10-27)
1. **Dieselben „Verlinkungen“ wie die Haupttabelle:** Die Audit-Tabelle mappt Enums, FKs, C#-Namen wie ihre Basistabelle. **Keine** Verlinkungen von anderen Tabellen auf die Audit-Tabelle.
2. **Sprung zum Hauptdatensatz** (Vorschlag, vom Nutzer dazugenommen).
3. **Geänderte Zellen leicht farbig hervorgehoben**, ohne für Hunderte Zeilen den Zeilenvergleich aufzurufen.

## Entscheidungen des Nutzers (2026-10-27)
1. Die Reihenfolge der Versionen kommt aus einer **neuen, optionalen Anweisung der Vorlage** (`-- @version-order AUDIT_ID`), nicht geraten. Die eingebaute Vorlage hat sie; ohne die Anweisung keine Hervorhebung. Die Sortierung des Grids spielt keine Rolle (Rückfrage des Nutzers: „Muss der Nutzer sortiert haben?“ – nein).
2. Die **Aktionsspalte** (I/U/D) gleich mitnehmen: `-- @action AUDIT_TRANS insert=I update=U delete=D`, Insert- und Delete-Zeilen getönt.
3. **Nur die Färbung**, kein alter Wert im Tooltip (hielte die Abfrage schmal).
4. „Bau das mal so mit ein“: Plan freigegeben; die einfachsten Varianten für die offenen UX-Punkte gebaut (unten).

## Gebaut
- **Core:**
  - Vorlage: `AuditTemplate.VersionOrder`, `AuditTemplate.Action` (`AuditAction` mit `KindOf`: Text ohne CHAR-Füllzeichen, Zahlen invariant), `IsOwnColumn`. Beide Anweisungen nennen eine eigene Spalte der Vorlage; Fehler mit Zeile (unbekannte Spalte, nicht ordnende Spalte wie CLOB, mehr als eine Spalte, unbekannter oder doppelter Teil von `@action`, keine Werte). Eingebaute Vorlage mit `@version-order AUDIT_ID` und `@action AUDIT_TRANS insert=I update=U delete=D`.
  - `SchemaCache.UseAuditTemplate`/`AuditBaseOf`; `LinksOf(audit)` gibt die Beziehungen der Basistabelle umgeschrieben (`ForeignKeyInfo.Audit = AuditRelation.Copied`, Quelle bleibt) und eine zum Basisdatensatz (`BaseRow`, `FkSource.Convention`, Name = Basistabelle). Neu gebaut, wenn Vorlage, Tabellenliste oder Beziehungen andere sind (Vergleich per Referenz). `OutgoingOf`, `IncomingOf`, `ForeignKeys` unverändert. `ActiveConnection` gibt die Vorlage über `AuditTemplateSource.For(Profile)` hinein, gefragt beim Laden und mit `RefreshAuditTemplate` (Review).
  - `TablePresentation.Create(…, columnsOf)`: Properties und Enums der Basis-Entity, `Entity` null (LINQ/HasData/Objekte aus); `IsEntityProperty` hält eine Audit-Tabelle aus (fand der Test: NullReference).
  - `AuditView.Of` (Basisschlüssel, Versionsspalte, verglichene Spalten: Kopien der Basisspalten mit `Projection.Value` und `OracleTypes.IsComparable`, ohne Schlüssel und eigene Spalten; Aktionsspalte), `AuditRowChange`.
  - `QueryBuilder.BuildAuditChanges`: Inline-View mit `ROW_NUMBER()` und `DECODE(c, LAG(c) OVER (PARTITION BY Basisschlüssel ORDER BY Versionsspalte, ROWID), '0', '1')` je Spalte, verkettet; innen gefiltert auf die Basisschlüssel des Blocks, außen auf die Zeilenschlüssel (PK der Audit-Tabelle oder ROWID als `FS_RID`). `KeyIn` nimmt dafür Präfix, Alias und ROWID-Namen.
  - `IDataAccess.ReadAuditChangesAsync` (OracleDataAccess): je 500 Zeilen eine Abfrage, ohne neuen Snapshot; Zeilen ohne Basisschlüssel fallen weg.
  - `AppSettings.HighlightAuditChanges` (Standard an).
- **UI:**
  - `GridAuditChanges` (State/Grid): eine Abfrage zur Zeit, Generation wie bei den Anzeigespalten, Fehler schaltet ab (`Grid_AuditChangesOff`, einmal), nach Dispose nichts mehr. `AuditMarks` je Zeile.
  - `FerretGrid`: plant mit dem ersten Block (`AuditView.Of` mit Vorlage der Verbindung und PK der Basistabelle), `Clear` beim Lesen ab Zeile 0, `Queue` nach jedem Block, `IAuditChangesGrid.RedrawAsync` nach Schlüsseln. `GridRows.Build(…, audit)` → `__c` (geänderte Spalten-IDs), `__a` („i“/„d“).
  - `grid.js`: `fs-audit-changed` (Zelle, Tooltip-Zeile `Grid_AuditChangedTitle`), `fs-audit-insert`/`fs-audit-delete` (Zeile). CSS-Token `--audit-change` (violett, hell/dunkel), Insert grün 7 %, Delete rot 7 %, eigene Änderungen (ausstehend/geschrieben) gewinnen.
  - Sprungmenü: „Hauptdatensatz“ statt „aus C#-Modell“ (`FkJump_BaseRow`, `FkJumpItem.Source`).
  - `PresentationService.For` und `ChangedSince` kennen die Basistabelle (ein neues Modell der Basistabelle zeichnet die Audit-Tabelle neu).
  - Spalten-, Constraint- und Index-Ansicht, Entity-Code und die „deklariert“-Prüfung des Kopierens lassen markierte Beziehungen aus.
  - Einstellung unter Audit-Tabellen (`SettingKey.AuditHighlightChanges`).
- **Tests:** Core `AuditTemplateTests` (Anweisungen, Fehler mit Zeile, Aktion eines Werts), `SchemaCacheAuditTests` (ausgehend mit Basisdatensatz, Selbstbezug, nichts eingehend, ohne Vorlage, ohne PK, Audit einer Audit-Tabelle, Views, geteilter Name, Vorlagenwechsel), `TablePresentationTests` (Namen und Enums der Basis-Entity, keine Entity), `QueryBuilderTests.Audit` (SQL genau, PK als Zeilenschlüssel, Grenzen, `AuditView.Of`); UI `GridAuditChangesTests` (Block, Aktion sofort, eine Abfrage zur Zeit, verspätete Antwort, Fehler, ohne Versionsspalte, Dispose, Zeilenmarker); Integration `AuditTests.Audit_rows_tell_what_changed_against_the_version_before` (Oracle 23: NUMBER, DATE, NULL, BOOLEAN, LOB nicht verglichen, Delete, nur spätere Zeilen gefragt, Beziehungen).

## E2E (2026-10-27)
Eigener Container mit der Beispiel-DB (`ferret-e2e63`, Port 1563, danach entfernt), Testinstanz mit `--data-dir`, Deutsch dunkel, eine Verbindung DEV ohne C#-Modell, Passwort nicht gespeichert.
- **Geprüft (CDP):**
  - AUFTRAG › Spalten › „Audit anlegen…“ → Designer → Ausführen (9 Statements, Rückfrage bestätigt): Audit-Zeile zeigt `AUDIT_AUFTRAG · AUFTRAG_AUDIT`. Per SQL*Plus: drei Updates von Auftrag 1 (eines ohne Änderung schreibt keine Zeile), ein Insert, ein Delete → 5 Audit-Zeilen.
  - AUDIT_AUFTRAG über den Link der Audit-Zeile: erste Version (U, ohne Vorgänger in der Tabelle) ohne Markierung; Zeile 2 KUNDE_ID getönt (NOTIZ ist ein CLOB, nicht verglichen); Zeile 3 KUNDE_ID und STATUS; Insert-Zeile grün, Delete-Zeile rot. Nach Sortierung STATUS absteigend dieselben Markierungen je Zeile. Hell (Emulation) und dunkel als Screenshot.
  - Kopf KUNDE_ID mit FK-Badge im Grid, in der Spaltenansicht der Audit-Tabelle kein FK.
  - Zellmenü: „Verweist auf“ KUNDEN 2 (`FK_AUFTRAG_KUNDE: AUDIT_AUFTRAG(KUNDE_ID) → KUNDEN`) und „AUFTRAG Hauptdatensatz 1“; kein „Referenziert von“. Der Sprung öffnet AUFTRAG mit `AUFTRAG_ID = 1`.
  - KUNDEN › Zellmenü › Referenziert von: ADRESSE, AUFTRAG, RECHNUNG, VERTRAG – keine Audit-Tabelle.
  - Log ohne `[ERR]`/`[WRN]`.
- **Nicht geprüft:** Einstellung „Änderungen in Audit-Tabellen hervorheben“ aus (der Knopf der Einstellungen reagierte nicht auf ein DOM-Klick-Ereignis; der Weg ist eine Abfrage beim Planen, `FerretGrid.PlanAuditChanges`); Enum-Namen und C#-Namen mit verknüpftem Projekt (Unit-Test `TablePresentationTests`); Filter, der die Vorversion auslässt (Integrationstest). Nicht mit echter Windows-Eingabe (keine neuen Mausinteraktionen).

## Review (2026-10-27), alle Befunde behoben (Auftrag des Nutzers)
1. **Markierungen blieben stehen:** Eine Antwort setzte nur Markierungen und nahm keine weg, und `FerretGrid.ReloadAsync` (Schreiben, Commit, Rollback an Ort und Stelle) vergaß die Anzeigespalten, nicht die Audit-Änderungen. Jetzt `GridAuditChanges.Apply` (jede Zeile des Blocks bekommt, was die Antwort sagt, auch „nichts“) und `Clear` in `ReloadAsync`.
2. **Vorlage je Nachschlagen:** `OutgoingOf` fragte bei jedem Aufruf `AuditTemplateSource.For` – beim Rendern, alle 3 s eine Prüfung der Datei, vielleicht auf einem Share über das VPN. Jetzt fragt der Schema-Cache beim Laden und mit `RefreshAuditTemplate` (jedes Lesen ab der ersten Zeile eines Tabs); `SchemaCache.AuditTemplate` gibt die gefragte, das Grid braucht `AuditTemplateSource` nicht mehr.
3. **Kopien in `OutgoingOf`:** Fünf Stellen mussten `Audit is null` filtern, ein neuer Aufrufer hätte Kopien für Constraints gehalten. Jetzt `SchemaCache.LinksOf` für die Lesewege (Grid-Badges, Spaltenliste, Kopfmenü, Zellmenü, Formular, `DisplayLinks`), `OutgoingOf` ist wieder die Datenbank; die Filter sind weg (Spalten-, Constraint-, Index-Ansicht, Entity-Code und Kopie wie auf `main`).
4. **Speicher:** Markierungen aller je gelesenen Blöcke blieben. Jetzt höchstens `GridAuditChanges.MaxKept` (40 Blöcke wie `RowBlocks`), darüber fallen die der Zeilen weg, die das Grid nicht mehr hält (`IAuditChangesGrid.HeldKeys`).
5. **Zwei Regeln:** Audit-Zeile (`IsAuditTable`) und Grid (`Pair` im Schema-Cache) waren sich bei geteilten Namen uneins. Jetzt eine Regel, `AuditStatus.Pairs`; `IsAuditTable` fragt sie.
6. **Einmal geplant:** Jetzt plant `FerretGrid.PlanAuditChangesAsync` bei jedem Lesen ab der ersten Zeile neu (Vorlage, Einstellung).
7. **Abgebrochen:** Ein abgebrochener Block wird einmal wiederholt.
8. **NULL in der Versionsspalte:** `@version-order` verlangt NOT NULL (oder Identity), sonst ein Fehler mit Zeile (`VersionOrderNullable`).
9. **Tests:** `ActiveConnectionTests.The_schema_pairs_audit_tables_by_the_connections_template`, `PresentationServiceAuditTests` (mit `AuditTemplateSource` in der `TestApp` wie in der App, `TestApp.Tables`), `SchemaCacheAuditTests.Lookups_do_not_ask_for_the_template`, `GridAuditChangesTests` (Markierung weg, zweiter Versuch, Grenze), `AuditPlannerTests` (eine Regel).
10. **Kontrast berechnet** (Text auf der gemischten Fläche, WCAG): hell mindestens 12,2:1, dunkel mindestens 8,6:1 (Änderung auf einer Insert-Zeile, ungerade Zeile), überall weit über 4,5:1. Die Flächen gegen die Zeile: Änderung 1,25–1,35:1, Insert/Delete 1,08–1,12:1 – bewusst leicht (Wunsch des Nutzers).

## Offen / Fragen an den Nutzer
- Sprung von einer Zeile der Haupttabelle in ihren Verlauf (gefilterte Audit-Tabelle): bewusst nicht gebaut (Wunsch 1: nichts auf die Audit-Tabelle), im Backlog.
- LOB-Spalten werden nicht hervorgehoben (kein `LAG` auf LOBs; `DBMS_LOB.COMPARE` wäre je Zeile teuer).
- Die Hervorhebung gilt ab dem nächsten Öffnen des Tabs, wenn Einstellung oder Vorlage geändert werden (wie die Audit-Zeile).
