# ADR 0046: SQL-Skripte als Dateien in drei Ordnern

- Status: akzeptiert
- Datum: 2026-11-08
- Paket: WP-77 (`docs/work-packages/wp-77-sql-scripts.md`)

## Kontext

Ein Kollege des Nutzers wollte im SQL-Editor Statements markieren und speichern – auch das ganze Fenster – und später wieder abrufen. Gespeicherte Abfragen gehören teils einer Person, teils einem Produkt (alle Verbindungen einer Gruppe), teils jeder Oracle-Datenbank (Sperren, Sessions). Sie sollen sich teilen lassen, ohne dass FerretSharp einen eigenen Server oder ein eigenes Austauschformat braucht. Die Filter-Vorlagen (WP-37) sind eine JSON-Bibliothek mit festen IDs; ihr Teilen scheiterte genau daran (Backlog).

## Entscheidung (des Nutzers, 2026-11-08)

- **Ein Skript ist eine `.sql`-Datei**, Ordner sind Kategorien – keine JSON-Bibliothek, keine IDs. Dateien lassen sich in Git diffen und mergen und in Visual Studio oder VS Code bearbeiten.
- **Angaben im Kopf** als Kommentarzeilen wie bei den Audit-Vorlagen (`docs/audit-templates.md`): `-- @description …`, `-- @param name typ` (Typen des SQL-Editors: `text`, `char`, `number`, `date`, `raw`, `null`). Parameter nur mit Typ, **nie mit Wert**. Unbekannte Angaben und Zeilen mit Fehlern bleiben beim Schreiben erhalten. Syntax: `docs/sql-scripts.md`.
- **Drei Wurzeln nebeneinander**, nicht „die erste gesetzte gilt“: *Persönlich* (`scripts\` im Datenordner, umstellbar), *Gruppe* (Ordner je Verbindungsgruppe, nur für deren Verbindungen), *Allgemein* (für jede Verbindung). Kein Ordner je Verbindung.
- **FerretSharp schreibt auch in geteilte Ordner** (Speichern, Löschen) wie bei den Anzeigespalten (ADR 0022); Commit und Pull macht der Nutzer.
- **Ein Tab ist mit seiner Datei verbunden**, gespeichert wird nur ausdrücklich. Der Workspace merkt sich Wurzel (Art und Gruppe) und relativen Pfad, nie den absoluten.
- Neue Dateien: UTF-8 ohne BOM, CRLF; eine vorhandene Datei behält Kodierung, BOM und Zeilenenden. Keine Standardtaste für „Skript speichern“ (Regel aus `docs/architecture.md` 3).

## Sicherheit

- Ausgeführt wird nur über die bestehenden Wege (Lese-, Schreib- und Schemaweg mit Sperre auf Prod, ADR 0006). Ein Skript wird beim Öffnen nie ausgeführt. Es gibt keinen neuen Weg zur Datenbank.
- Kopfangaben werden nie zu SQL; nur `@param` wird zum Typ einer Variable.
- Gelesen, geschrieben und gelöscht wird nur über `ScriptPaths.Resolve`: ein relativer Pfad aus Namen (keine `..`, keine Laufwerke, keine Gerätenamen, Endung `.sql`), höchstens acht Ordner tief, strikt unterhalb der Wurzel, und kein Teil unterhalb der Wurzel ist ein symbolischer Link oder eine Junction (die Wurzel selbst darf einer sein: die hat der Nutzer gewählt). So kann weder ein Pfad aus einer Workspace-Datei noch ein Link in einem geteilten Repository aus dem Ordner führen. Die Liste folgt Links nicht und zeigt keine versteckten Ordner (`.git`).
- Ein Skriptname aus dem Dialog wird als Dateiname geprüft und abgelehnt statt bereinigt (`SuggestedFileName`-Regeln: Trenner, `:`, `%`, Bidi-Zeichen, Gerätenamen wie `CON`).
- Größengrenzen: 1 MB je Datei (die Liste liest nur die ersten 16 KB für den Kopf), 2.000 Skripte je Ordner.

## Folgen

- Die Liste liest die Ordner im Hintergrund (Watcher mit Unterordnern, dazu alle 10 s ein Blick für Freigaben, die sich nicht beobachten lassen); die Ansichten lesen nie im UI-Thread von der Platte.
- Eine Datei, die außen geändert wurde (etwa durch `git pull`), lädt ein unveränderter Tab still neu; ein geänderter fragt „neu laden / behalten“, und Speichern über eine geänderte Datei fragt vorher.
- Umbenennt jemand die Gruppe, wandert ihr Skriptordner mit (wie die Audit-Vorlage). Ein Tab, der mit einem Skript der alten Gruppe verbunden ist, findet seinen Ordner nicht mehr und bietet „Speichern unter“ an.
- Umbenennen und Verschieben im Explorer kommen später (Backlog); Löschen gibt es.

## Nachtrag WP-84 (2026-11-12): Skripte aus einer Zelle starten

Entscheidungen des Nutzers (2026-11-11):
- Zwei neue Kopfangaben: `-- @target name TABELLE.SPALTE` (eigene Zeile, nicht an `@param` gehängt – ältere FerretSharp behalten eine unbekannte Zeile still, ein erweitertes `@param` wäre dort ein Fehler) und `-- @param name typ list` (Listen aus markierten Zeilen). Ziele ohne Owner.
- Ein Skript, das nur liest, läuft aus dem Zellmenü sofort, **auch auf Prod** (wie ein FK-Sprung). Fehlen weitere Parameter, fragt ein Dialog (*Ausführen* / *Im Editor öffnen*); ein Skript mit DML öffnet nur.
- „Zuletzt benutzt“ in einer eigenen Datei ohne Werte; die Werte des Dialogs nur im Speicher.

Sicherheit: Weiter kein neuer Weg zur Datenbank – der Tab führt das Skript über den Leseweg aus, die Lesesperre prüft jedes Statement wie sonst. „Sofort ausführen“ gilt nur, wenn **jedes** Statement eine reine Abfrage ist (`SqlStatementInfo.IsQuery`); alles andere öffnet nur. Werte aus der Zelle sind Binds; eine Liste wird zu einem Bind je Wert (`SqlBindLists`, nur allein in `IN ( … )`), nie zu einem Literal. Ein Ziel nennt nur, wo das Skript im Menü steht – es wird nie zu SQL. Grenze wie bei jedem SELECT (CLAUDE.md 2): Ruft eine Abfrage eine Funktion mit `PRAGMA AUTONOMOUS_TRANSACTION` auf, schreibt sie trotz Lesesperre – mit dem Zellmenü genügt dafür ein Klick auf ein Skript aus einem geteilten Ordner; für Prod bleibt die Empfehlung ein User mit reinen SELECT-Grants.

## Nachtrag fix-script-header-in-editor (2026-11-13): Kopf im SQL-Editor

Entscheidung des Nutzers (2026-11-13): Die Kopfzeilen, für die es in der App sonst keinen Platz gibt – `@target` und unbekannte Zeilen –, stehen **sichtbar oben im SQL-Tab** und werden dort geschrieben, geändert und gelöscht (statt nur beim Speichern übernommen oder als Feld im Speichern-Dialog). `@param` bleibt in den Variablen, `@description` im Dialog; oben getippt, werden sie übernommen. Grund: Skripte im SQL-Editor anzulegen ist der Normalweg; ein dort getipptes `@target` landete vorher still im SQL. Protokoll: `docs/work-packages/fix-script-header-in-editor.md`.

Folgen:
- Der Workspace merkt sich je verbundenem Tab, ob sein Text den Kopf zeigt (`ScriptLink.HeaderInText`); ältere Workspace-Dateien zeigen ihn nicht, beim Speichern behält FerretSharp dann die Zeilen der Datei.
- Die Zeile „Kopfangaben werden nie zu SQL“ (Sicherheit) gilt weiter für die Datei und für alles, was FerretSharp aus dem Kopf macht (Ziele, Typen). Im Tab stehen Ziele und unbekannte Zeilen aber als `--`-Kommentare über dem SQL und laufen beim Ausführen als Kommentar vor dem ersten Statement mit. Sie bleiben reine Kommentarzeilen: Gezeigt wird nur, was als Kopfzeile erkannt ist, je Zeile ohne Zeilenumbruch (`KeptLines`), die Lesesperre prüft den ganzen Text wie sonst.
