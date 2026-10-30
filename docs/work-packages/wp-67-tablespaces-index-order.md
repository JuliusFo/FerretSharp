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
- **UI:** `TablespacePicker` (Auswahl mit „Standard (USERS)“, Vorschlag mit „Übernehmen“) in „Index anlegen“, im Kopf einer neuen Tabelle und im Reiter Constraints des Designers („Tablespace der neuen Indizes“, siehe Nachtrag). ASC/DESC-Schalter an jeder Spalte des Index-Entwurfs (`IndexDraftEditor.ToggleDescending`). `TableDesignerDraft.IndexTablespace`/`Tablespaces` überstehen einen Rebase; gelesen wird beim Öffnen des Designers bzw. des Index-Entwurfs, ein Fehler lässt nur den Standard zur Wahl.
- **Tests:** Core `IndexDesignTests`, `TableDesignTests`, `TablespacesTests`, `ObjectDetailsTests`; Integration `TableDesignTests.Tablespaces_and_descending_columns_run_and_read_back_as_drafted`; UI `TableDesignerDraftTests`.

## E2E (2026-10-30)
Eigene Instanz (`--data-dir`, eigener WebView2-Ordner, Build im Scratchpad, `--lang=de`, hell) gegen einen eigenen Container mit zwei Tablespaces (`USERS`, `IDX2`). Index mit `DATUM DESC` in IDX2 über den Vorschlag, neue Tabelle samt PK und FK in IDX2, zwei UKs an einer bestehenden Tabelle (einer neu in IDX2, einer auf vorhandenem Index mit Hinweis) – alles ausgeführt und im Dictionary nachgelesen, Log ohne Fehler.
- **Gefunden und behoben (Layout):** Der Hinweis zum Namen einer neuen Tabelle rutschte unter den Tablespace; der Tablespace steht jetzt darunter.
- **Nicht mit echter Eingabe geprüft:** Per `SendInput` kamen nur Bewegungen an (an der richtigen Stelle), kein `mousedown` – laut Anleitung nicht weitergeklickt. ASC/DESC und „Übernehmen“ per CDP (Stufe 2) geprüft; mit der Maus bitte von Hand nachsehen.

## Nachtrag: Feedback des Nutzers (2026-10-30)
- Der Index-Tablespace stand immer oben im Reiter Constraints, auch ohne neuen Index, und las sich, als hätte ein FK einen eigenen Index. Ein FK hat keinen: „mit Index“ legt einen normalen Index auf seine Spalten an, nur PK/UK bringen ihren Index selbst mit.
- Jetzt steht das Feld unter den neuen Constraints und erscheint nur, wenn der Plan Indizes anlegt; daneben steht, für welche („für PK_X, IX_X_A (Index für FK_X_Y)“). Die Liste kommt aus dem Plan (`DesignPlan.NewIndexes`, dieselbe Regel wie das Skript): ein Schlüssel, den ein vorhandener Index trägt, fehlt darin.
- E2E: Die Testinstanz einer parallelen Session hielt Port 9333 – eine zweite Instanz mit eigenem WebView2-Ordner lief auf 9334 ohne Absturz. Vor CDP-Aufrufen prüfen, dass die Instanz am Port die eigene ist (eigene Verbindung im DOM).

## Nachtrag: Code-Review (2026-10-30)
Unabhängiger Reviewer über den Branch; die drei Verdachtsfälle gegen Oracle 23 gemessen (alle bestätigt). Entscheidung des Nutzers: „Behebe alle, die Audit-Tabelle übernimmt den Tablespace der Tabelle.“

1. M – Ein Schlüssel, den der Plan löscht, ließ seinen vorher eigens angelegten Index stehen (`CREATE UNIQUE INDEX …; ADD CONSTRAINT … USING INDEX …`, wie SQL Developer und DBMS_METADATA schreiben); ein neuer Schlüssel auf denselben Spalten mit `USING INDEX TABLESPACE` scheiterte dann mit ORA-01408 – nach dem `DROP CONSTRAINT`, die Tabelle ohne Schlüssel: behoben, die Indizes gelöschter Schlüssel zählen weiter als vorhanden (keine Klausel). Unit- und Integrationstest.
2. K – Ein eindeutiger Index mit weiteren Spalten (auch der eines neuen PK davor) und ein Bitmap-Index tragen einen neuen Schlüssel nicht, Oracle legt einen eigenen an – der Designer ließ die Klausel weg und meldete „nutzt den vorhandenen Index“: behoben mit `IndexAdvice.BacksKey` (gemessen: UNIQUE (A, B) und Bitmap (A, B) → eigener Index, nicht eindeutig (A, B) → genommen). Auch die Namensprüfung eines neuen Schlüssels (ORA-00955) nutzt die Regel.
3. K – Das Lesen der Tablespaces lag vor dem Erscheinen des Designers (eine VPN-Runde mehr): behoben, es läuft danach im Hintergrund.
4. K – „Index anlegen“ las Namen und Tablespaces in einem Aufruf; scheiterte der zweite, fehlte auch die Prüfung auf ORA-00955: behoben, zwei Aufrufe.
5. K – Globale temporäre Tabellen: Oracle ignoriert `TABLESPACE` am Index still und bricht `USING INDEX TABLESPACE` mit ORA-00600 ab (gemessen): behoben, `GetTablespacesAsync` liest, ob die Tabelle temporär ist; dann kein Picker, keine Klausel, im Index-Entwurf ein Problem.
6. K – Ein Tablespace-Name mit `"` warf beim Bauen des DDL, statt ein Problem zu sein (nicht erreichbar, der Name kommt aus der Liste): behoben, geprüft wird vorher; der Index-Entwurf schreibt dann kein Statement.
7. K – „nutzt den vorhandenen Index“ erschien auch, wenn das Feld ausgeblendet war (kein neuer Index): behoben, der Hinweis nur, solange andere Indizes entstehen.
8. K – `docs/architecture.md` nannte die FK-Regel ohne „aufsteigend“: ergänzt (`Backs`, `BacksKey`, `Covers`, `GetTablespacesAsync`).
- Frage des Reviewers, vom Nutzer entschieden: Eine neue Audit-Tabelle übernimmt den Tablespace ihrer Tabelle (neue Tabelle: der gewählte, bestehende: `Tablespaces.TableTablespace`).
- Zweiter Durchgang des Reviewers: 1–8 abgedeckt, Tests würden ohne Fix scheitern (3, 4 Razor ohne Test). Zwei neue K, Entscheidung des Nutzers: „Jo, behebe beides.“
  - A – Bei einer bestehenden Tabelle kam der Tablespace für die Audit-Tabelle seit Fix 3 erst im Hintergrund; „Ausführen“ davor oder ein gescheitertes Lesen legte sie still in den Standard: behoben, der Designer hält das Skript zurück, solange eine neue Audit-Tabelle auf den Tablespace wartet (wie beim Lesen des Audit-Zustands); scheitert das Lesen, kommt sie in den Standard mit Warnung. E2E: Audit-Tabelle von `E2E_POS` (in IDX2) mit `TABLESPACE "IDX2"`.
  - B – Fix 1 behandelt den Index eines gelöschten Schlüssels als bleibend; meist hat Oracle ihn aber mit dem Schlüssel angelegt, dann geht er mit, und der neue Schlüssel bekommt einen Index im Standard – der Hinweis „nutzt den vorhandenen Index“ stimmte dann nicht: behoben mit eigenem Hinweis („falls er beim Löschen bleibt … sonst im Standard-Tablespace“), auch ohne weitere neue Indizes. Ohne Klausel bleibt es (das Dictionary sagt nicht, welcher Fall vorliegt).
- Dritter Durchgang: A und B abgedeckt, das Warten in A bleibt nicht hängen (Rebase, Lesefehler, Abbruch, neue Tabelle). Drei neue K, Entscheidung des Nutzers: „Fixen“.
  - C – Ohne Schema beim Lesen blieb der Lesestand auf „liest noch“ (eine neue Audit-Tabelle hätte für immer gewartet; kaum erreichbar): behoben, dann gilt das Lesen als gescheitert.
  - D – Der unsichere Hinweis aus B kam auch, wenn ein anderer, sicher bleibender Index den Schlüssel trägt: behoben, ein Index mit dem Namen eines gelöschten Schlüssels zählt nur, wenn kein anderer trägt (ein implizit angelegter heißt wie sein Constraint, einer mit anderem Namen war eigens angelegt und bleibt).
  - E – Der Test zu B prüfte den alten Satz ohne weiteren neuen Index, wo er ohnehin nicht erschien: der Testfall bekommt einen FK mit Index, dazu beide Reihenfolgen für D.
- E2E der Fixes (eigene Instanz, Port 9334): Audit-Vorschau wie oben; temporäre Tabelle `E2E_TEMP` – weder im Index-Entwurf noch im Designer ein Tablespace-Feld. Log ohne Fehler.
- Nebenbei gemessen, vorher schon so: Auf den Spalten eines Bitmap-Index scheitert ein neuer Schlüssel mit ORA-01408, mit und ohne Klausel (Backlog).
