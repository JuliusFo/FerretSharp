# Audit-Vorlagen (WP-26)

Zu einer Tabelle (der **Basistabelle**) legt FerretSharp eine **Audit-Tabelle** und einen **Trigger** an, der jede Änderung dorthin schreibt. Wie beide aussehen, steht in einer **Vorlage**: einer SQL-Datei mit Anweisungszeilen. FerretSharp bringt eine Standardvorlage mit (unten); eine eigene Vorlage (z. B. aus dem Repo der Firma) ersetzt sie.

Code: `src/FerretSharp.Core/Audit/` (`AuditTemplates.Parse`, `AuditTemplate.NamesFor`/`TableFor`/`TriggerFor`), Typwissen in `Schema/OracleTypes` (`TryParse`, `IsComparable`, `IsLong`), Texte in `Resources/AuditText.resx`. Entscheidungen des Nutzers: `docs/roadmap.md` (WP-26), Trigger über den Schemaweg: ADR 0021.

## Beispiel

Das Muster aus der Firma des Nutzers (mit neutralen Namen; ein Testfall in `AuditTemplateTests`):

```sql
-- @audit-table   AUDIT_{table}
-- @trigger       {table}_AUDIT
-- @table-comment Changes to {table}

-- @columns
AUDIT_ID    NUMBER(12)   NOT NULL  -- Running number (AUDIT_ID_SEQ)
AUDIT_USER  VARCHAR2(30) NOT NULL  -- Client identifier, else database user
AUDIT_TRANS CHAR(1)      NOT NULL  -- I = insert, U = update, D = delete
AUDIT_TIME  DATE         NOT NULL  -- Time of the change
AUDIT_SID   NUMBER(9)    NOT NULL  -- Session
-- @base-columns as-base

-- @trigger-source
CREATE OR REPLACE TRIGGER {trigger}
AFTER DELETE OR INSERT OR UPDATE ON {table}
FOR EACH ROW
DECLARE
  audit_trans CHAR(1);
BEGIN
  IF DELETING THEN
    INSERT INTO {audit_table} (AUDIT_ID, AUDIT_USER, AUDIT_TRANS, AUDIT_TIME, AUDIT_SID{each}, {column}{end})
    VALUES (AUDIT_ID_SEQ.NEXTVAL, SUBSTR(NVL(SYS_CONTEXT('USERENV','CLIENT_IDENTIFIER'), USER), 1, 30),
            'D', SYSDATE, USERENV('sessionid'){each}, :old.{column}{end});
  ELSE
    IF INSERTING THEN audit_trans := 'I';
    ELSIF {each comparable sep="\n       OR "}:new.{column} <> :old.{column} OR (:new.{column} IS NULL AND :old.{column} IS NOT NULL) OR (:new.{column} IS NOT NULL AND :old.{column} IS NULL){end}
    THEN audit_trans := 'U';
    ELSE RETURN;
    END IF;
    INSERT INTO {audit_table} (AUDIT_ID, AUDIT_USER, AUDIT_TRANS, AUDIT_TIME, AUDIT_SID{each}, {column}{end})
    VALUES (AUDIT_ID_SEQ.NEXTVAL, SUBSTR(NVL(SYS_CONTEXT('USERENV','CLIENT_IDENTIFIER'), USER), 1, 30),
            audit_trans, SYSDATE, USERENV('sessionid'){each}, :new.{column}{end});
  END IF;
END;
```

Für die Tabelle `APP.KUNDE` mit den Spalten `KUNDE_ID`, `NAME`, `NOTIZ` (CLOB) und `"Bemerkung"` wird daraus die Tabelle `APP.AUDIT_KUNDE` (fünf eigene Spalten, dann die vier der Basistabelle) und der Trigger `APP.KUNDE_AUDIT`:

```sql
CREATE OR REPLACE TRIGGER APP.KUNDE_AUDIT
AFTER DELETE OR INSERT OR UPDATE ON APP.KUNDE
…
    INSERT INTO APP.AUDIT_KUNDE (AUDIT_ID, AUDIT_USER, AUDIT_TRANS, AUDIT_TIME, AUDIT_SID, KUNDE_ID, NAME, NOTIZ, "Bemerkung")
…
    ELSIF :new.KUNDE_ID <> :old.KUNDE_ID OR (…) OR (…)
       OR :new.NAME <> :old.NAME OR (…) OR (…)
       OR :new."Bemerkung" <> :old."Bemerkung" OR (…) OR (…)
…
```

## Aufbau

Eine Vorlage ist eine Textdatei (UTF-8). **Anweisungen** sind Kommentarzeilen der Form `-- @name wert`; sie teilen die Datei in Abschnitte. Leere Zeilen und andere Kommentarzeilen (`-- …`) außerhalb des Trigger-Quelltexts zählen nicht; jede andere Zeile außerhalb eines Abschnitts ist ein Fehler.

| Anweisung | Pflicht | Inhalt |
|---|---|---|
| `-- @audit-table NAME` | ja | Name der Audit-Tabelle; Platzhalter `{table}`, `{owner}` |
| `-- @trigger NAME` | ja | Name des Triggers; Platzhalter `{table}`, `{owner}`, `{audit_table}` |
| `-- @table-comment TEXT` | nein | Kommentar der Audit-Tabelle; Platzhalter `{table}`, `{owner}`, `{audit_table}`, `{trigger}`. Ohne die Anweisung bekommt die Tabelle keinen Kommentar. |
| `-- @columns` | nein | Beginn der eigenen Spalten der Audit-Tabelle (eine je Zeile, bis zur nächsten Anweisung) |
| `-- @base-columns as-base` bzw. `nullable` | ja | Stelle der Basisspalten zwischen den eigenen Spalten: Spalten davor stehen vor, Spalten danach hinter ihnen. `as-base` übernimmt NOT NULL der Basisspalte, `nullable` macht alle Basisspalten nullable. Nach der Anweisung können weitere Spaltenzeilen folgen. |
| `-- @trigger-source` | ja | Der Rest der Datei ist der Trigger: `CREATE [OR REPLACE] TRIGGER …` bis `END;`. Leerzeilen am Anfang und Ende und eine abschließende `/`-Zeile fallen weg. Muss deshalb der letzte Abschnitt sein. |

Jede Anweisung darf nur einmal vorkommen.

### Namen

In `@audit-table`, `@trigger` und `@table-comment` werden Platzhalter zum **Namen aus dem Dictionary**, ohne Anführungszeichen: Für die Tabelle `Kunde` (gemischt geschrieben) ergibt `AUDIT_{table}` den Namen `AUDIT_Kunde`. Audit-Tabelle und Trigger liegen immer im Schema der Basistabelle. Ein Name, der leer ist, doppelte Anführungszeichen enthält, länger als 128 Byte (UTF-8) ist oder die Basistabelle selbst wäre, ist ein **Problem** dieser Tabelle (`AuditNames.Problems`), kein Fehler der Vorlage.

### Eigene Spalten

```
NAME TYP [GENERATED [ALWAYS | BY DEFAULT [ON NULL]] AS IDENTITY | DEFAULT [ON NULL] ausdruck] [NOT NULL | NULL] [-- Kommentar]
```

- Der Name gilt wie in SQL: ungequotet großgeschrieben (`audit_id` → `AUDIT_ID`), in `"…"` exakt.
- Typen wie in DDL (`OracleTypes.TryParse`): `NUMBER`, `NUMBER(12)`, `NUMBER(12,2)`, `NUMBER(*,2)`, `INTEGER`/`INT`/`SMALLINT`, `FLOAT[(n)]`, `BINARY_FLOAT`, `BINARY_DOUBLE`, `VARCHAR2(30)`, `VARCHAR2(30 CHAR)`, `VARCHAR(n)`, `CHAR[(n [BYTE|CHAR])]`, `NVARCHAR2(n)`, `NCHAR[(n)]`, `RAW(n)`, `DATE`, `TIMESTAMP[(n)] [WITH [LOCAL] TIME ZONE]`, `INTERVAL YEAR[(n)] TO MONTH`, `INTERVAL DAY[(n)] TO SECOND[(n)]`, `CLOB`, `NCLOB`, `BLOB`, `BFILE`, `LONG`, `LONG RAW`, `ROWID`, `XMLTYPE`, `JSON`, `BOOLEAN`. Groß-/Kleinschreibung und Leerzeichen sind egal. `VARCHAR2(30)` zählt Bytes und wird als `VARCHAR2(30 BYTE)` angelegt – die `NLS_LENGTH_SEMANTICS` der Session entscheidet nicht.
- Identity: `GENERATED ALWAYS AS IDENTITY`, `GENERATED AS IDENTITY` und `GENERATED BY DEFAULT [ON NULL] AS IDENTITY` werden gelesen; FerretSharp legt Identity-Spalten wie überall (`OracleDdl`) als `BY DEFAULT` an – `ColumnInfo` kennt den Unterschied nicht. Für einen Trigger, der die Spalte weglässt, ist das gleich. Identity-Spalten sind NOT NULL.
- `DEFAULT ausdruck` reicht bis zu einem abschließenden `NOT NULL`/`NULL` bzw. bis zum Kommentar; der Ausdruck muss in ein Statement passen (kein `;`, keine Bind-Variable, passende Klammern).
- Der Kommentar hinter `--` wird zum Spaltenkommentar.

### Basisspalten

Die Spalten der Basistabelle kommen in ihrer Reihenfolge an die Stelle von `@base-columns`. Eine Kopie behält Name, Typ und Kommentar (Spaltenkommentare der Basistabelle werden immer übernommen); NOT NULL nur mit `as-base`.

- **Kein Default, keine Identity:** Eine Audit-Zeile ist eine Kopie, keine neue Zeile.
- **Virtuelle Spalten** werden normale Spalten ihres Typs (ohne Ausdruck): Der Trigger speichert den Wert, den sie zum Zeitpunkt der Änderung hatten – so bleibt er erhalten, auch wenn sich der Ausdruck später ändert.
- **`LONG` und `LONG RAW`** fehlen in der Audit-Tabelle und in allen Schleifen des Triggers, mit Warnung: Oracle bietet in Triggern kein `:new`/`:old` dafür (ORA-04093).
- Heißt eine Basisspalte wie eine eigene Spalte der Vorlage, ist das ein Problem dieser Tabelle (`AuditTableShape.Problems`).

### Trigger-Quelltext

Platzhalter:

| Platzhalter | Wird zu |
|---|---|
| `{table}` | Basistabelle mit Schema, z. B. `APP.KUNDE` |
| `{audit_table}` | Audit-Tabelle mit Schema, z. B. `APP.AUDIT_KUNDE` |
| `{trigger}` | Trigger mit Schema, z. B. `APP.KUNDE_AUDIT` |
| `{owner}` | Schema, z. B. `APP` |
| `{column}` | (nur in einer Schleife) Name der Basisspalte, z. B. `KUNDE_ID` |
| `{type}` | (nur in einer Schleife) Typ der Basisspalte wie in DDL, z. B. `VARCHAR2(80 BYTE)` |
| `{name}` | (nur in einer Schleife) Name der Basisspalte für ein String-Literal, `'` verdoppelt, z. B. `UPDATING('{name}')` |

- Namen werden als **Bezeichner** geschrieben: ungequotet, wenn Oracle sie so liest (Großbuchstaben, Ziffern, `_ $ #`, beginnt mit einem Buchstaben, kein reserviertes Wort von SQL oder PL/SQL), sonst exakt gequotet: `"Bemerkung"`, `"DATE"`, `"END"` (`OracleIdentifier.QuoteIfNeeded`).
- `{table}`, `{audit_table}` und `{trigger}` stehen **mit Schema**: Ein ungequalifizierter Name im Kopf des Triggers (`CREATE TRIGGER x … ON t`) gälte im aktuellen Schema der Session, und das ist nicht immer das der Basistabelle. Im Rumpf löst Oracle Namen ohnehin im Schema des Triggers auf (eine Sequence wie `AUDIT_ID_SEQ` braucht deshalb kein Schema).
- `{{` und `}}` schreiben eine geschweifte Klammer (`REGEXP_LIKE(x, '^[0-9]{{2}}$')`). Eine einzelne `{` oder `}` ist ein Fehler.

**Spaltenschleife:** `{each}…{end}` wiederholt den Text dazwischen für jede Basisspalte (außer LONG), darin gelten `{column}`, `{type}` und `{name}`. Optionen hinter `each`:

- `sep="…"`: Text zwischen zwei Wiederholungen (nicht vor der ersten, nicht nach der letzten). Escapes wie in C#: `\n`, `\t`, `\"`, `\\`. Ohne `sep` wird nur der Text wiederholt – `{each}, {column}{end}` schreibt vor jede Spalte ein Komma.
- `comparable`: nur Spalten, die sich mit `<>` vergleichen lassen (`OracleTypes.IsComparable`): Text-, Zahl-, Datums-, Timestamp- und Intervalltypen, `RAW`, `ROWID`, `BOOLEAN`. Nicht: LOBs und `BFILE`, `XMLTYPE`, `JSON`, `VECTOR`, Objekttypen. Lässt eine solche Schleife Spalten aus und hat die Vorlage keine `incomparable`-Schleife, warnt FerretSharp (eine Änderung nur in diesen Spalten erkennt der Vergleich nicht).
- `incomparable`: genau die übrigen Spalten (LOBs …). Ihre Änderung zeigt `UPDATING('{name}')`: wahr, wenn das UPDATE die Spalte setzt – auch auf denselben Wert (Integrationstest gegen Oracle 23, auch mit gemischter Schreibweise wie `"Anhang"`). Die Standardvorlage schreibt die Bedingung deshalb so, dass sie für jede Spaltenmischung gültig bleibt:

  ```sql
  ELSIF FALSE{each comparable}
     OR (:new.{column} <> :old.{column} OR …){end}{each incomparable}
     OR UPDATING('{name}'){end}
  ```

Schleifen lassen sich nicht schachteln. Eine Schleife ohne Spalten (z. B. `comparable` bei einer Tabelle nur aus LOBs) ergibt eine Warnung – außer bei `incomparable`, wo das der Normalfall ist.

`AuditTrigger.Columns` nennt die Basisspalten, die in den Schleifen vorkommen – das, was `ALL_TRIGGER_COLS` für einen aktuellen Trigger zeigt.

### Fehler, Probleme, Warnungen

- **Fehler der Vorlage** (`AuditTemplateException.Errors`, mit Zeile): unbekannte oder doppelte Anweisung, fehlende Pflichtanweisung (Zeile 0), Text außerhalb eines Abschnitts, unverständliche Spaltenzeile oder unbekannter Typ, doppelte Spalte, unbekannter Platzhalter oder Platzhalter an der falschen Stelle, unbekannte Option, falsches Escape, `{each}` ohne `{end}` und umgekehrt, geschachtelte Schleife, einzelne Klammer. `AuditTemplates.Parse` meldet alle auf einmal.
- **Probleme** (`Problems` von `AuditNames`, `AuditTableShape`, `AuditTrigger`): Die Vorlage passt nicht zu dieser Tabelle (Name zu lang, Spalte doppelt). So nicht anlegen.
- **Warnungen** (`Warnings`): LONG-Spalten fehlen, eine `comparable`-Schleife lässt Spalten aus, eine Schleife hat keine Spalten.

### Abgleich (`AuditPlanner`)

`AuditLoader.LoadAsync` liest, was das Schema hat (Audit-Tabelle nach dem Namen der Vorlage – nur eine Tabelle zählt –, ihr Kommentar, der Trigger mit Status, Tabelle und `ALL_TRIGGER_COLS`). `AuditPlanner.Plan` macht daraus über den Schreiber des Designers (`TableDesign`) einen `DesignPlan`:

- Keine Audit-Tabelle: `CREATE TABLE` mit Kommentaren, danach der Trigger.
- Vorhandene Audit-Tabelle (Entscheidungen des Nutzers 2026-10-12): Spalten, die die Tabelle nicht mehr hat, **bleiben** und werden nullable; Typen werden nur **größer** (sonst eine Warnung); Kommentare folgen der Tabelle; Umbenennungen aus dem Designer werden übernommen; neue Spalten kommen ans Ende, in einer Audit-Tabelle mit (oder unbekannt vielen) Zeilen nullable (ORA-01758).
- Trigger: neu geschrieben, wenn er fehlt, ungültig ist, andere Spalten benutzt als die Vorlage, die Audit-Tabelle neue Spalten bekommt oder ausdrücklich gewünscht (Vorlage geändert). Ein gleichnamiger Trigger auf einer anderen Tabelle ist ein Problem. Ein aktueller, aber ausgeschalteter Trigger wird nur gemeldet.
- `AuditPlanner.Combine` hängt den Plan an den des Designers: ein Skript, die Basistabelle vor der Audit-Tabelle, der Trigger zuletzt.

Texte: `AuditText.resx` (Englisch) und `AuditText.de.resx`.

## Standardvorlage

`src/FerretSharp.Core/Audit/DefaultAuditTemplate.sql` (eingebettete Ressource, `AuditTemplates.DefaultText`/`Default`). Gleiches Muster wie oben, aber ohne Sequence:

- Audit-Tabelle `AUDIT_<TABELLE>`, Trigger `<TABELLE>_AUDIT`, Tabellenkommentar „Changes to <TABELLE>, written by trigger <TRIGGER>“.
- `AUDIT_ID NUMBER GENERATED BY DEFAULT AS IDENTITY` (ab 12c, keine Sequence nötig; die INSERTs des Triggers lassen die Spalte weg).
- `AUDIT_USER VARCHAR2(64)`: `SUBSTRB(NVL(SYS_CONTEXT('USERENV', 'CLIENT_IDENTIFIER'), USER), 1, 64)` – `SUBSTRB`, weil die Spalte Bytes zählt.
- `AUDIT_TRANS CHAR(1)` (I/U/D), `AUDIT_TIME DATE` (`SYSDATE`), `AUDIT_SID NUMBER` (`SYS_CONTEXT('USERENV', 'SESSIONID')`).
- Basisspalten `nullable`: Gelöschte Basisspalten bleiben in der Audit-Tabelle (Entscheidung des Nutzers) und müssen danach NULL aufnehmen können; neue Spalten in einer gefüllten Audit-Tabelle sind ohnehin nullable (ORA-01758).
- Ein UPDATE, das keinen vergleichbaren Wert ändert, schreibt keine Zeile (NULL-sicherer Vergleich je Spalte).

Die Kommentare der Standardvorlage sind englisch und werden nicht übersetzt: Sie landen als `COMMENT ON` in der Datenbank und dürfen nicht von der UI-Sprache dessen abhängen, der die Tabelle anlegt (sonst zeigte das Abgleichen Unterschiede je nach Sprache).
