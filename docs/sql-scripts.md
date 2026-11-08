# SQL-Skripte (WP-77)

Gespeicherte Abfragen sind `.sql`-Dateien in drei Ordnern; Entscheidung und Sicherheit: ADR 0046. Hier steht das Format und wie FerretSharp damit umgeht.

## Ordner

| Ordner | Wo | Wer ihn sieht |
|---|---|---|
| Persönlich | `scripts\` im Datenordner oder der Pfad in *Einstellungen › SQL-Skripte* | jede Verbindung |
| Gruppe | Ordner je Verbindungsgruppe (Einstellungen, Link im Gruppenmenü) | nur die Verbindungen dieser Gruppe |
| Allgemein | Pfad in den Einstellungen, z. B. ein geteiltes Repository | jede Verbindung |

Unterordner sind Kategorien (höchstens acht Ebenen). Versteckte Ordner (`.git`, Name mit Punkt vorn), Links und Dateien mit Namen, die FerretSharp beim Speichern ablehnen würde, erscheinen nicht.

## Format

```sql
-- @description Offene Aufträge eines Kunden
-- @param kunde_id number
-- @param seit date

SELECT *
  FROM auftrag
 WHERE kunde_id = :kunde_id
   AND angelegt >= :seit;
```

- Der **Kopf** sind die `-- @…`-Zeilen ganz oben (Leerzeilen davor erlaubt); die erste andere Zeile beendet ihn, eine Leerzeile direkt danach gehört noch dazu. Was darunter steht, zeigt der SQL-Tab.
- `-- @description text` – Beschreibung; mehrere Zeilen ergeben mehrere Absätze.
- `-- @param name typ` – ein Bind des Skripts mit seinem Typ: `text`, `char`, `number`, `date`, `raw`, `null` (wie im Variablen-Editor). Ein Name mit Leerzeichen in Anführungszeichen (`-- @param "ab datum" date`). **Nie ein Wert.**
- Unbekannte `-- @…`-Zeilen und Zeilen mit Fehlern bleiben beim Speichern erhalten (Fehler mit Zeilennummer im Tooltip des Skripts).
- Beim Speichern schreibt FerretSharp den Kopf neu: Beschreibung, dann die Parameter der Binds, die der Text benutzt, mit den Typen der Variablen, dann die übrigen Zeilen.

**Tipp:** Tabellen ohne Owner schreiben – dann läuft ein Skript auf jeder Umgebung (das Default-Schema der Verbindung).

## Abrufen

- Explorer › *Skripte*: Baum je Ordner, Suche nach Namen. Klick öffnet einen neuen SQL-Tab mit dem Text, **ausgeführt wird nichts**; die Parameter stehen als Variablen mit Typ bereit. Rechtsklick: öffnen, in den aktuellen SQL-Tab einfügen, im Datei-Explorer zeigen, löschen.
- SQL-Editor › *Skripte* (neben *Verlauf*): Suche über Name, Beschreibung und Ordner; Klick fügt am Cursor ein bzw. ersetzt die Markierung. Das Statement davor wird abgeschlossen, Parameter kommen zu den Variablen, vorhandene Werte bleiben. Einfügen verbindet den Tab nicht mit der Datei.
- Befehlspalette: Skripte nach Namen.

## Speichern

- SQL-Editor: *Speichern* (in die verbundene Datei, sonst wie *Fenster als Skript speichern…*), im Menü daneben *Fenster als Skript speichern…* bzw. *Speichern unter…*, *Auswahl als Skript speichern…* (ohne Auswahl das Statement am Cursor). Am Eintrag des Verlaufs *Als Skript speichern…*. Die Aktion *SQL-Skript speichern* hat keine Standardtaste.
- Der Dialog fragt Name, Ordner, Unterordner (vorhandene zur Auswahl, ein neuer wird angelegt) und Beschreibung. Eine Datei gleichen Namens wird erst nach Rückfrage ersetzt.
- Ein Tab, der eine Datei zeigt, heißt wie das Skript und trägt „●“, solange Text oder Parametertypen von der Datei abweichen. Gespeichert wird nur ausdrücklich; Schließen fragt nicht (der Workspace hält den Text).
- Außen geändert (z. B. `git pull`): ohne eigene Änderungen lädt der Tab still neu, sonst *Neu laden* / *Meine behalten*. Speichern über eine seit dem Öffnen geänderte Datei fragt vorher.
- Neue Dateien sind UTF-8 ohne BOM mit CRLF; eine vorhandene behält Kodierung (auch ANSI/Windows-1252), BOM und Zeilenenden. Ein Zeichen, das die Kodierung der Datei nicht kennt, wird abgelehnt statt durch „?“ ersetzt.
