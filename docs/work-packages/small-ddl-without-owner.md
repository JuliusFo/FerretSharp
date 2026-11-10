# Klein: DDL ohne Owner

Branch `feat/ddl-without-owner` (von `main`), 2026-11-10, Release offen. Baut auf v1.7 (Ansicht DDL) und WP-71 (GRANT-Statements im DDL-Tab).

## Anlass (Wunsch des Nutzers, 2026-11-10)
- Die Ansicht DDL zeigt jeden Namen mit Owner (`"OWNER"."TABELLE"`). Wer das DDL in eine Datenbank mit anderem Owner kopiert, muss den Owner jedes Mal erst von Hand entfernen. Erst bewerten, dann bauen.

## Bewertung
- Betroffen ist im Kern die Ansicht DDL (`DBMS_METADATA.GET_DDL`) samt GRANT-Block. Der Schema-Vergleich schreibt sein Skript schon für den Ziel-Owner, „Spalten als ALTER TABLE … ADD kopieren“ löst die Zieltabelle in der anderen Verbindung auf; Designer, Synonyme und Trigger an/aus laufen gegen die eigene DB und brauchen den Owner (der eingeloggte User muss nicht der Owner sein) – dort bleibt alles, wie es ist.
- Oracles eigener Weg `DBMS_METADATA.SET_TRANSFORM_PARAM(…, 'EMIT_SCHEMA', FALSE)` ist PL/SQL und passt nicht zum Leseweg (ADR 0006); `WITH FUNCTION` im SELECT würde die Lesesperre unterlaufen. Deshalb nimmt der Core den Owner nachträglich aus dem Text.

## Entscheidungen des Nutzers
- Vorschlag freigegeben („Ja, so bauen“, 2026-11-10): Schalter „Ohne Owner“ direkt in der Ansicht DDL, gilt für CREATE und GRANTs, der Stand bleibt als Einstellung (auch auf der Einstellungsseite); Designer, Vergleich und übrige DDL-Stellen unverändert.
- Standard aus (wie bisher) – Vorschlag (nicht bestätigt).

## Gebaut
- **Core:** `Ddl/DdlOwner.Omit(ddl, owner)` läuft mit `SqlTokenizer` über den Text und streicht den gequoteten eigenen Owner, wo direkt ein Punkt folgt (`"SHOP".`), und nur als ersten Teil eines Punkt-Namens (`"SHOP"."SHOP"."NEXTVAL"` → `"SHOP"."NEXTVAL"`, `"OTHER"."SHOP".X` bleibt; Review). Groß-/Kleinschreibung exakt wie gequotet. Unverändert bleiben String-Literale (auch `q'[…]'`), Kommentare, andere Schemas (ein FK in ein anderes Schema braucht ihn) und ungequotete Namen im Quelltext einer View oder eines Triggers – `SHOP.X` kann dort ein Tabellen-Alias sein. `AppSettings.DdlWithoutOwner` (Standard aus).
- **UI:** `DdlView` mit Schalter „Ohne Owner“ neben der Überschrift (Tooltip nennt den Owner), Anzeige und Kopier-Symbol zeigen denselben Text; `GrantScript.WithoutOwner`. Beide halten den gezeigten Text je geladenen Daten und Schalterstand (sie rendern mit jedem Rendern des Tabs; Review). Einstellungen › Datenansicht › „DDL ohne Owner“ (`SettingKey.DdlWithoutOwner`, Eintrag im `SettingsCatalog`). Texte Englisch und Deutsch.
- **Tests:** `DdlOwnerTests` (Tabelle mit Index und FK ins eigene Schema, unverändert bei anderem Schema/anderer Schreibweise/Spalte wie der Owner/leer, Literale und Kommentare, View- und Trigger-Quelltext, GRANT mit Empfänger gleich dem Owner); Standard in `SettingsStoreTests.A_missing_file_gives_the_decided_defaults`.

## E2E
2026-11-10, eigener Container (`New-SampleDb.ps1 -Name ferret-e2e-ddlowner -Port 1591`, dazu `GRANT SELECT ON AUFTRAG TO PUBLIC`), Testinstanz deutsch/dunkel auf Debug-Port 9341 (parallel lief die Instanz einer anderen Session), Passwort nicht gespeichert; Klicks auf Schalter, Kopier-Symbol und „Aus“ in den Einstellungen mit echter Windows-Eingabe (Probe vorher bestanden).
- Gefunden: Der eingeschaltete Schalter (`btn ghost small active`) sah aus wie der ausgeschaltete – `.active` färbt einen Ghost-Knopf nur in Werkzeugleisten. **Behoben:** `.ddl-head .btn.active` wie die Schalter der Werkzeugleisten (Akzentfarbe, schwacher Akzent-Hintergrund).
- Nicht geprüft: heller Modus (dieselben Tokens wie die Schalter der Werkzeugleisten).

## Grenzen
- Ein gequoteter Alias, der genau wie der Owner heißt (`"SHOP".ID` im View-Text mit Alias `"SHOP"`), verliert ebenfalls sein Präfix.
- Ungequotete Verweise auf das eigene Schema im Quelltext einer View bleiben stehen und müssen im Zielschema passen. (Trigger zeigt die Ansicht DDL nicht; der Core lässt ihren Quelltext genauso stehen.)
- Scheitert `GET_DDL` (ORA-31603 ohne `SELECT_CATALOG_ROLE`), fehlt der Schalter im Tab; die GRANTs folgen dann der Einstellung von der Einstellungsseite.

## Nachtrag: Code-Review (2026-11-10)
Review per `/branch-code-review` (eigener Reviewer-Agent): keine S/M-Befunde, vier K. Entscheidung des Nutzers: „Alle, Owner bleibt“.
1. K Der Owner wurde auch als mittlerer Teil eines dreiteiligen Namens gestrichen: `DEFAULT "SHOP"."SHOP"."NEXTVAL"` (Sequence wie das Schema) wurde `DEFAULT "NEXTVAL"`, `"OTHER"."SHOP".X` wurde `"OTHER".X`. **Behoben** (`c357d5bc`): nur der erste Teil eines Punkt-Namens; Tests `Only_the_first_part_of_a_dotted_name_is_the_owner` und Fälle im Theory (auch mit Kommentar zwischen den Teilen), ohne den Fix rot.
2. K Bei eingeschaltetem Schalter zerlegten `DdlView` und `GrantScript` den Text bei jedem Rendern des Tabs neu (die Shell rendert alle gemounteten Tabs, auch bei Zellbearbeitungen) – teuer bei großem DDL. **Behoben** (`e06b750d`): der gezeigte Text wird je geladenen Daten und Schalterstand gehalten. Ohne Test (Rendern), Gegenprobe im zweiten Review-Durchgang.
3. K Tooltip, CHANGELOG und Protokoll nannten „Quelltext eines Triggers“, die Ansicht DDL zeigt aber nur Tabellen, Views und MViews. **Behoben** (`d573e134`): Texte nennen nur Views.
4. K Das Glossar sah für „owner“ „Eigentümer“ vor, die neuen und die meisten bestehenden Texte schreiben „Owner“. **Behoben** (`51d248a8`, Entscheidung des Nutzers): „Owner“ bleibt im Deutschen, Glossar angepasst.
- Zweiter Durchgang über die Fix-Commits: alle vier abgedeckt, keine Regression; nur Protokoll und Regel kannten die Fixes noch nicht (hier nachgezogen).
- Vorher schon da: `Ddl_Source` sagt „Grants stehen nicht darin“, obwohl darunter seit WP-71 der GRANT-Block steht (gemeint: nicht im Text von `GET_DDL`).
