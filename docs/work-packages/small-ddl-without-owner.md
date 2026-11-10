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
- **Core:** `Ddl/DdlOwner.Omit(ddl, owner)` läuft mit `SqlTokenizer` über den Text und streicht den gequoteten eigenen Owner, wo direkt ein Punkt folgt (`"SHOP".`). Groß-/Kleinschreibung exakt wie gequotet. Unverändert bleiben String-Literale (auch `q'[…]'`), Kommentare, andere Schemas (ein FK in ein anderes Schema braucht ihn) und ungequotete Namen im Quelltext einer View oder eines Triggers – `SHOP.X` kann dort ein Tabellen-Alias sein. `AppSettings.DdlWithoutOwner` (Standard aus).
- **UI:** `DdlView` mit Schalter „Ohne Owner“ neben der Überschrift (Tooltip nennt den Owner), Anzeige und Kopier-Symbol zeigen denselben Text; `GrantScript.WithoutOwner`. Einstellungen › Datenansicht › „DDL ohne Owner“ (`SettingKey.DdlWithoutOwner`, Eintrag im `SettingsCatalog`). Texte Englisch und Deutsch.
- **Tests:** `DdlOwnerTests` (Tabelle mit Index und FK ins eigene Schema, unverändert bei anderem Schema/anderer Schreibweise/Spalte wie der Owner/leer, Literale und Kommentare, View- und Trigger-Quelltext, GRANT mit Empfänger gleich dem Owner); Standard in `SettingsStoreTests.A_missing_file_gives_the_decided_defaults`.

## Grenzen
- Ein gequoteter Alias, der genau wie der Owner heißt (`"SHOP".ID` im View-Text mit Alias `"SHOP"`), verliert ebenfalls sein Präfix.
- Ungequotete Verweise auf das eigene Schema im View- oder Trigger-Quelltext bleiben stehen und müssen im Zielschema passen.
- Scheitert `GET_DDL` (ORA-31603 ohne `SELECT_CATALOG_ROLE`), fehlt der Schalter im Tab; die GRANTs folgen dann der Einstellung von der Einstellungsseite.
