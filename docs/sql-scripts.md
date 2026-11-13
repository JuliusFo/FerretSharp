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
-- @target kunde_id KUNDE.KUNDE_ID

SELECT *
  FROM auftrag
 WHERE kunde_id = :kunde_id
   AND angelegt >= :seit;
```

- Der **Kopf** sind die `-- @…`-Zeilen ganz oben (Leerzeilen davor erlaubt); die erste andere Zeile beendet ihn, eine Leerzeile direkt danach gehört noch dazu. Der SQL-Tab zeigt das SQL darunter und darüber nur Ziele und unbekannte Zeilen (unten, *Kopf im SQL-Editor*).
- `-- @description text` – Beschreibung; mehrere Zeilen ergeben mehrere Absätze.
- `-- @param name typ` – ein Bind des Skripts mit seinem Typ: `text`, `char`, `number`, `date`, `raw`, `null` (wie im Variablen-Editor). Ein Name mit Leerzeichen in Anführungszeichen (`-- @param "ab datum" date`). **Nie ein Wert.**
- `-- @param name typ list` (WP-84) – ein Bind, der mehrere Werte nimmt: Er steht allein in `IN ( … )` (`WHERE kunde_id IN (:kunden)`). Mit einem Wert ist er ein gewöhnlicher Bind; mit mehreren (Start aus markierten Zeilen, unten) wird er zu je einem Bind pro Wert (`:kunden_1, :kunden_2, …`), höchstens 1000 (Grenze von Oracle). Ein Wert wird nie zum Literal.
- `-- @target name TABELLE.SPALTE` (WP-84) – die Spalte, aus der der Parameter seinen Wert nimmt: Das Skript erscheint im Zellmenü dieser Spalte und jeder Spalte, die auf sie zeigt (unten). Tabelle **ohne Owner**, Namen ohne Anführungszeichen werden groß geschrieben (`kunde.kunde_id` = `KUNDE.KUNDE_ID`), mit Anführungszeichen genau so. In der Datei braucht der Parameter ein `@param` (im Tab nicht, das schreibt FerretSharp beim Speichern selbst); mehrere Ziele je Parameter sind mehrere Zeilen. Die Reihenfolge der Kopfzeilen ist frei.
- Unbekannte `-- @…`-Zeilen und Zeilen mit Fehlern bleiben beim Speichern erhalten (Fehler mit Zeilennummer im Tooltip des Skripts).
- Beim Speichern schreibt FerretSharp den Kopf neu: Beschreibung, dann die Parameter der Binds, die der Text benutzt, mit den Typen der Variablen, dann die übrigen Zeilen.

### Kopf im SQL-Editor

Der SQL-Tab zeigt oben die Kopfzeilen, für die es sonst keinen Platz gibt: `@target` und Zeilen, die FerretSharp nicht kennt, dann eine Leerzeile und das SQL. Dort werden sie geschrieben, geändert und gelöscht; *Speichern* nimmt alle `-- @…`-Zeilen ganz oben im Text als Kopf.

- `@param` steht in den Variablen (Typ), `@description` im Speichern-Dialog. Oben getippt, werden sie übernommen (Typ und `list`; der Wert der Variable bleibt) und verschwinden danach aus dem Text – ein `@param` nur, wenn das SQL seinen Bind hat; sonst wird es gemeldet, bleibt im Tab stehen und wird nicht gespeichert (in der Datei wäre es ein Parameter, nach dem das Zellmenü fragt). `@param`-Zeilen der Datei mit Fehlern (ein Parameter doppelt) zeigt der Tab nicht; sie bleiben in der Datei, bis oben ein `@param` desselben Namens getippt wird.
- Was während des Speicherns getippt wird, bleibt stehen; der Tab zeigt dann „●“.
- Ein `@target` braucht im Tab kein `@param`: Es muss nur einen Bind des SQL nennen.
- Steht davor ein gewöhnlicher Kommentar oder SQL, ist es kein Kopf mehr.
- Kopfzeilen, die sich nicht verwenden lassen (Ziel ohne passenden Bind, Tippfehler), meldet die Leiste über dem Editor nach dem Speichern – mit der Zeile selbst, nicht ihrer Nummer; gespeichert werden sie trotzdem, wie sie sind – außer einem `@param` ohne Bind (oben), das nur im Tab bleibt; solange es dort steht, zeigt der Tab „●“. Ausgeblendete `@param`-Zeilen der Datei ohne Wirkung (ohne Namen oder ohne Bind im SQL) fallen beim Speichern weg.

```sql
-- @target kunde_id KUNDE.KUNDE_ID

SELECT * FROM auftrag WHERE kunde_id = :kunde_id;
```

**Tipp:** Tabellen ohne Owner schreiben – dann läuft ein Skript auf jeder Umgebung (das Default-Schema der Verbindung).

## Abrufen

- Explorer › *Skripte*: Baum je Ordner, Suche nach Namen. Klick öffnet einen neuen SQL-Tab mit dem Text, **ausgeführt wird nichts**; die Parameter stehen als Variablen mit Typ bereit. Rechtsklick: öffnen, in den aktuellen SQL-Tab einfügen, im Datei-Explorer zeigen, löschen.
- SQL-Editor › *Skripte* (neben *Verlauf*): Suche über Name, Beschreibung und Ordner; Klick fügt am Cursor ein bzw. ersetzt die Markierung. Das Statement davor wird abgeschlossen, Parameter kommen zu den Variablen, vorhandene Werte bleiben. Einfügen verbindet den Tab nicht mit der Datei.
- Befehlspalette: Skripte nach Namen.

## Aus einer Zelle starten (WP-84)

Rechtsklick auf eine Zelle zeigt unter **Skripte** die Skripte, deren `@target` zur Spalte passt:

- **Passend** ist die Zielspalte selbst und jede Spalte, die über eine bekannte Beziehung auf sie zeigt – deklarierter FK, manuelle Beziehung, Konvention, C#-Modell, auch über mehrere Schritte (`POSITION.AUFTRAG_ID` → `AUFTRAG.ID`, `AUFTRAG.KUNDE_ID` → `KUNDE.KUNDE_ID`). Nicht passend sind Geschwister (zwei Spalten, die auf dieselbe Tabelle zeigen) und die Gegenrichtung: Das Ziel ist die Spalte, aus der die Werte stammen, meist der Primärschlüssel. Fehlt eine Beziehung, schließt eine manuelle sie. Die Tabelle des Ziels sucht FerretSharp im Schema der Verbindung (eigenes Objekt vor Synonym); gibt es sie dort nicht, erscheint das Skript nirgends.
- **Reihenfolge:** zuletzt aus einer Zelle gestartete zuerst (`script-usage.json` im Datenordner: Ordnerart, Gruppe, Pfad, Zeit – keine Werte), dann wie in der Liste. Bis zu fünf stehen offen, der Rest hinter „N weitere“; das Filterfeld des Menüs sucht in Name, Beschreibung und Ordner.
- **Wert:** der Wert der Zelle, wie die Datenbank ihn hat (nicht der Anzeigename eines Enums). Ein `list`-Parameter nimmt die Werte der Spalte aus allen markierten Zeilen, jeden einmal, ohne NULL – der Eintrag zeigt „· N Werte“. NULL, LOBs und Gleitkommazahlen lassen sich nicht übergeben (Eintrag gesperrt, Grund im Tooltip); eine Liste auch nicht, solange markierte Zeilen nicht geladen sind (ihre Werte fehlten) oder mehr als 1000 Werte zusammenkommen. Aus einem Zeitreise-Tab kommt der Wert von damals, das Skript liest den Stand von jetzt.
- **Ausführen:** Besteht das Skript nur aus Abfragen (SELECT/WITH ohne `FOR UPDATE`), öffnet ein SQL-Tab im Workspace der Tabelle und führt es sofort aus – auch auf Prod, es liest nur. Haben weitere Parameter keinen Wert aus der Zelle, fragt vorher ein Dialog danach (*Ausführen* oder *Im Editor öffnen*); die Werte merkt sich FerretSharp für den nächsten Start, nur bis zum Beenden. Ein Skript mit DML öffnet nur, vorbelegt, und führt nichts aus.
- **Tab:** Ein Tab, der schon mit der Datei verbunden ist, wird genommen, seine Variablen gesetzt – aber nur, wenn sein SQL dem der Datei gleicht (die Kopfzeilen darüber zählen nicht); sonst öffnet ein neuer Tab mit dem Text der Datei (nicht verbunden), der bearbeitete bleibt, wie er ist. Ausgeführt wird immer das ganze Skript, nie nur eine Markierung, und nur, wenn der Text im Editor aus Abfragen besteht. Mit mehreren Werten eines `list`-Parameters öffnet ein neuer Tab mit dem aufgeklappten Text, **nicht** mit der Datei verbunden (Speichern schriebe sonst die aufgeklappten Binds ins Skript).

## Speichern

- SQL-Editor: *Speichern* (in die verbundene Datei, sonst wie *Fenster als Skript speichern…*), im Menü daneben *Fenster als Skript speichern…* bzw. *Speichern unter…*, *Auswahl als Skript speichern…* (ohne Auswahl das Statement am Cursor). Am Eintrag des Verlaufs *Als Skript speichern…*. Die Aktion *SQL-Skript speichern* hat keine Standardtaste.
- Der Dialog fragt Name, Ordner, Unterordner (vorhandene zur Auswahl, ein neuer wird angelegt) und Beschreibung. Eine Datei gleichen Namens wird erst nach Rückfrage ersetzt.
- Ein Tab, der eine Datei zeigt, heißt wie das Skript und trägt „●“, solange Text oder Parametertypen von der Datei abweichen. Gespeichert wird nur ausdrücklich; Schließen fragt nicht (der Workspace hält den Text).
- Außen geändert (z. B. `git pull`): ohne eigene Änderungen lädt der Tab still neu, sonst *Neu laden* / *Meine behalten*. Speichern über eine seit dem Öffnen geänderte Datei fragt vorher.
- Neue Dateien sind UTF-8 ohne BOM mit CRLF; eine vorhandene behält Kodierung (auch ANSI/Windows-1252), BOM und Zeilenenden. Ein Zeichen, das die Kodierung der Datei nicht kennt, wird abgelehnt statt durch „?“ ersetzt.
