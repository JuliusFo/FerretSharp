---
paths:
  - "src/FerretSharp.Core/Oracle/OracleSchemaReader*"
  - "src/FerretSharp.Core/Oracle/OracleDataEditor*"
  - "src/FerretSharp.Core/Schema/PlSql*"
  - "src/FerretSharp.Core/Audit/**"
  - "src/FerretSharp.UI/Components/PlSql/**"
---

# PL/SQL im Dictionary (WP-28, WP-26)

- `ALL_ARGUMENTS`: Oracle 23 legt für Unterprogramme **ohne Parameter gar keine Zeile** an (ältere Versionen eine Platzhalterzeile ohne Name und Typ) → die Liste der Unterprogramme kommt aus `ALL_PROCEDURES`, die Parameter werden angehängt (`PlSqlArguments.Group`). Position 0 ist der Rückgabewert, `OVERLOAD` ist ohne Überladung NULL, vor 18c gibt es Zeilen mit `DATA_LEVEL > 0` (Komponenten zusammengesetzter Typen). `DEFAULT_VALUE` bleibt leer, nur `DEFAULTED` sagt etwas. Ohne Owner- und Namensfilter ist die View sehr langsam.
- `ALL_SOURCE`: eine Zeile je `LINE` mit Zeilenende im `TEXT`; nur so stimmen Zeile/Spalte aus `ALL_ERRORS`. Wrapped-Code steckt mit vielen Zeilen in einer `TEXT`-Zeile (Kopf „… wrapped“ + `a000000`). `TEXT` ist `VARCHAR2(4000)` → `FetchManyRows`, sonst ~30 Zeilen je Roundtrip. Den Body eines fremden Packages zeigt Oracle nur dem Eigentümer bzw. mit `DEBUG` darauf – mit `EXECUTE` ist er leer (kein Fehler).
- Trigger haben einen eigenen Namensraum (Trigger und Tabelle dürfen gleich heißen) → `PlSqlRef` mit Art. Trigger gelöschter Tabellen bleiben als `BIN$…` in `ALL_OBJECTS`.
- `CREATE` mit Kompilierfehlern wirft in ODP.NET ORA-24344 („success with compilation error“); in SQL*Plus ist es nur eine Warnung. **Ausnahme Trigger** (Oracle 23): `CREATE TRIGGER` mit Kompilierfehlern meldet ODP.NET ohne jeden Fehler – der Trigger ist still ungültig (und ohne `DISABLE` eingeschaltet: jede DML auf der Tabelle scheitert mit ORA-04098). Ob er kompiliert hat, sagt nur `ALL_OBJECTS.STATUS`; der Schemaweg prüft das nach jedem `CREATE TRIGGER` (`OracleDataEditor`, ADR 0021). Ein Package mit ~5.500 Konstanten scheitert an PLS-00123 (Diana nodes).
- `ALL_TRIGGER_COLS` (gemessen gegen Oracle 23, `TriggerColumnsTests`): Spalten aus `:new`/`:old` im Body und `UPDATE OF`, **nicht** die der `WHEN`-Klausel. Die Einträge zeigen auf Spaltenpositionen: `DROP COLUMN` (auch einer nicht benutzten Spalte, ebenso `DROP UNUSED COLUMNS`) macht den Trigger INVALID, nummeriert sie aber nicht um → bis zur nächsten Kompilierung nennt die View die Spalten, die jetzt an diesen Positionen stehen (falsche Namen). `ADD COLUMN` und `SET UNUSED` lassen den Trigger gültig und die Liste richtig. Ein Trigger mit Kompilierfehler behält, was der Compiler vor dem Fehler auflöste (`COLUMN_USAGE` = `NONE`). Deshalb liest `GetTriggerColumnsAsync` nur gültige Trigger; die nächste DML auf der Tabelle kompiliert einen nur durch DDL ungültigen Trigger neu.
