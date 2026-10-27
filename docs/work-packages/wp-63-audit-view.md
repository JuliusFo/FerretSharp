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
  - `SchemaCache.UseAuditTemplate`/`AuditBaseOf`; `OutgoingOf(audit)` gibt die Beziehungen der Basistabelle umgeschrieben (`ForeignKeyInfo.Audit = AuditRelation.Copied`, Quelle bleibt) und eine zum Basisdatensatz (`BaseRow`, `FkSource.Convention`, Name = Basistabelle). Neu gebaut, wenn Vorlage, Tabellenliste oder Beziehungen andere sind (Vergleich per Referenz). `IncomingOf`, `ForeignKeys` unverändert. `ActiveConnection` gibt die Vorlage über `AuditTemplateSource.For(Profile)` hinein.
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

## Offen / Fragen an den Nutzer
- Sprung von einer Zeile der Haupttabelle in ihren Verlauf (gefilterte Audit-Tabelle): bewusst nicht gebaut (Wunsch 1: nichts auf die Audit-Tabelle), im Backlog.
- LOB-Spalten werden nicht hervorgehoben (kein `LAG` auf LOBs; `DBMS_LOB.COMPARE` wäre je Zeile teuer).
- Die Hervorhebung gilt ab dem nächsten Öffnen des Tabs, wenn Einstellung oder Vorlage geändert werden (wie die Audit-Zeile).
