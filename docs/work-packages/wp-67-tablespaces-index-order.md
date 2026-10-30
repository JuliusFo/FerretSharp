# WP-67 Tablespaces und Sortierrichtung im Designer → Release 3.57.0

Branch `wp/67-tablespaces-index-order`, 2026-10-30. Wünsche eines Kollegen, vom Nutzer am selben Tag weitergegeben: bei Indizes je Spalte die Sortierrichtung angeben, bei Indizes und Constraints den Tablespace. Der Nutzer hat die Tablespaces neuer Tabellen dazugenommen.

## Planung
- Ausgangslage: Lesen, Vergleichen und DDL kannten `IndexColumn.Descending` schon (WP-20), der Index-Entwurf setzte es fest auf aufsteigend. Den Tablespace von Tabelle und Index lasen Objektkopf und Indizes-Ansicht, geschrieben wurde er nirgends.
- „Unsortiert“ gibt es bei einem B-Tree-Index nicht: Ohne Angabe ist eine Spalte aufsteigend. Also ein Umschalter ASC/DESC je Spalte, nicht drei Werte.
- Ein Constraint hat keinen eigenen Speicher: Der Tablespace eines PK/UK ist der seines Index (`USING INDEX TABLESPACE`); FK und Check haben keinen.
- Nicht drin: bestehende Tabellen oder Indizes umziehen (`MOVE`, `REBUILD`), Tablespaces im Schema-Vergleich (bleiben keine Abweichung, die DDL des Vergleichs schreibt keine), Speicherparameter.

## Entscheidungen des Nutzers (2026-10-30)
1. **Tablespaces auch für neue Tabellen**, nicht nur für Indizes und Constraints.
2. **Leer starten, aber einen Vorschlag geben:** Das Feld steht auf dem Standard des Schemas, darunter ein Vorschlag mit „Übernehmen“.
3. **Ein gemeinsamer Index-Tablespace je Designer-Lauf** (für die Indizes neuer Schlüssel und neuer FKs), nicht einer je Constraint-Zeile.
4. **Speicherparameter** (die „MEMORY-Einstellungen“ des SQL Developer: `STORAGE`, `PCTFREE`, `INMEMORY` …) **nicht** in diesem Paket – Backlog. In lokal verwalteten Tablespaces wirken die meisten nicht mehr, `INMEMORY` braucht eine kostenpflichtige Option.

## Gemessen gegen Oracle 23 (eigener Container, vor dem Bau)
- **Ein DESC-Index schützt einen FK nicht:** Mit `(FK DESC)` wartet das Löschen einer Elternzeile weiter auf die Kindtabelle, mit `(FK)` nicht. Für einen neuen Schlüssel legt Oracle neben einem DESC-Index einen eigenen an.
- **`USING INDEX TABLESPACE` auf einer Spaltenliste mit Index:** ORA-01408 statt den vorhandenen Index zu nehmen (ohne Klausel nimmt Oracle ihn).
- **Kein Kontingent:** `CREATE TABLE`/`CREATE INDEX` auf einer leeren Tabelle laufen durch (verzögerte Segment-Erzeugung), erst die erste Zeile scheitert mit ORA-01950; ein Index auf Zeilen und ein Schlüssel darauf scheitern sofort.
- `USER_TABLESPACES` zeigt nur Tablespaces mit Kontingent (bzw. alle mit `UNLIMITED TABLESPACE`); `USER_TS_QUOTAS` nennt eines ohne Zeile nicht.

## Gebaut
- **Core:** `OracleDdl.CreateTable`/`CreateIndex` mit `TABLESPACE`, `ConstraintClause` mit `USING INDEX TABLESPACE` (nur PK/UK, nur ohne `usingIndex`). Der Vergleich gibt nie einen Tablespace mit: Der Parameter ist ausdrücklich, nicht `IndexInfo.Tablespace` der Referenz. `IndexDraft.Descending`/`Tablespace`; die Zwillingsprüfung (ORA-01408) vergleicht Namen **und** Richtung, `(A)` und `(A DESC)` dürfen nebeneinander stehen. Warnung, wenn alle Spalten absteigend sind. `NewTable.Tablespace`, `TableDraft.IndexTablespace`; `TableDesign` hängt den Index-Tablespace an einen neuen Schlüssel nur, wenn kein aufsteigender Index der Tabelle (oder ein Schlüssel davor im selben Plan) ihn trägt – sonst ohne Klausel und mit Hinweis „nutzt den vorhandenen Index“. Die Indizes neuer FKs bekommen ihn ebenfalls.
- **`IndexAdvice.Backs`** (neu): wie `Covers`, aber nur aufsteigende führende Spalten. Gilt für FK-Hinweis, FK-Index im Designer und den Schlüssel-Index; `Covers` bleibt für Suchen (`HasIndexFor`, ein DESC-Index taugt dafür). Behebt nebenbei, dass ein DESC-Index als FK-Index zählte.
- **`ISchemaReader.GetTablespacesAsync`** – ein Roundtrip (`UNION ALL`): wählbare Tablespaces, Standard und Kontingente (nur eigenes Schema; fremde Kontingente bräuchten DBA-Views), wo die Indizes der Tabelle und die Indizes und Tabellen des Schemas liegen. `Tablespaces.ForIndexes()` (Indizes der Tabelle, ohne welche die des Schemas) und `ForTable()` (Tabellen des Schemas) schlagen den häufigsten vor, außer er ist ohnehin der Standard. `LacksQuota` warnt (ORA-01950) – in der Praxis selten, weil die Liste schon nur Tablespaces mit Kontingent zeigt.
- **UI:** `TablespacePicker` (Auswahl mit „Standard (USERS)“, Vorschlag mit „Übernehmen“) in „Index anlegen“, im Kopf einer neuen Tabelle und im Reiter Constraints des Designers („Index-Tablespace“). ASC/DESC-Schalter an jeder Spalte des Index-Entwurfs (`IndexDraftEditor.ToggleDescending`). `TableDesignerDraft.IndexTablespace`/`Tablespaces` überstehen einen Rebase; gelesen wird beim Öffnen des Designers bzw. des Index-Entwurfs, ein Fehler lässt nur den Standard zur Wahl.
- **Tests:** Core `IndexDesignTests`, `TableDesignTests`, `TablespacesTests`, `ObjectDetailsTests`; Integration `TableDesignTests.Tablespaces_and_descending_columns_run_and_read_back_as_drafted`; UI `TableDesignerDraftTests`.

## E2E (2026-10-30)
Eigene Instanz (`--data-dir`, eigener WebView2-Ordner, Build im Scratchpad, `--lang=de`, hell) gegen einen eigenen Container mit zwei Tablespaces (`USERS`, `IDX2`). Index mit `DATUM DESC` in IDX2 über den Vorschlag, neue Tabelle samt PK und FK in IDX2, zwei UKs an einer bestehenden Tabelle (einer neu in IDX2, einer auf vorhandenem Index mit Hinweis) – alles ausgeführt und im Dictionary nachgelesen, Log ohne Fehler.
- **Gefunden und behoben (Layout):** Der Hinweis zum Namen einer neuen Tabelle rutschte unter den Tablespace; der Tablespace steht jetzt darunter.
- **Nicht mit echter Eingabe geprüft:** Per `SendInput` kamen nur Bewegungen an (an der richtigen Stelle), kein `mousedown` – laut Anleitung nicht weitergeklickt. ASC/DESC und „Übernehmen“ per CDP (Stufe 2) geprüft; mit der Maus bitte von Hand nachsehen.
