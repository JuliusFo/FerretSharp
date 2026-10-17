---
paths:
  - "src/FerretSharp.Core/Ddl/**"
  - "src/FerretSharp.Core/Design/**"
  - "src/FerretSharp.Core/Compare/**"
  - "src/FerretSharp.Core/Audit/**"
  - "src/FerretSharp.UI/Components/TableDesigner*"
  - "src/FerretSharp.UI/Components/NewTableView*"
  - "src/FerretSharp.UI/Components/Audit*"
---

# DDL erzeugen (R4b, WP-23, WP-26)

Schemaweg und Sperren vor `ALTER TABLE`: `.claude/rules/oracle-transactions.md`, ADR 0019/0020/0021.

- DDL-Text entsteht nur in `Core/Ddl/OracleDdl` (Statements, Spaltendefinition, Constraint-Klausel, Index, Ausdrücke, Literale). Vergleich (`Compare/DdlWriter`), Designer (`Design/TableDesign`, `IndexDesign`) und Audit bauen keine eigenen Strings.
- Ein Schritt ist ein `DdlStep(Object, Kind, Sql, Risk, Warning)`. `Sql` ist nie auskommentiert: Was nicht laufen soll, sagt `Risk.NotExecutable` bzw. `Kind.Hint`, und `DdlScript.Render`/`Text` kommentieren es. Ausführen nur, was `IsExecutable` ist.
- Die Reihenfolge steht nur in `DdlScript.Stage(Kind)`; `DdlScript.Order` sortiert nach Stufe, Objekt, Schreibreihenfolge (Constraints zuerst löschen, Kommentare zuletzt – Vergleich und Designer gleich). Ein PL/SQL-Objekt endet im Skript mit einer `/`-Zeile; der Schemaweg nimmt davon nur Trigger auf Tabellen/Views (`SqlStatementKind.Trigger`, ADR 0021), der Splitter hält Blöcke bis zur `/`-Zeile zusammen.
- Spaltenänderungen über `ColumnChange.Steps(table, before, after, intent)` auf `OracleTypes.ChangeOf`: `Known` (Designer, Vorlagen) schreibt Rename/Drop als DDL und danach den neuen Namen, `Unknown` (Vergleich) macht daraus Hinweise und bleibt beim alten. Mehrere Umbenennungen nur über `ColumnChange.Renames` (tragfähige Reihenfolge, Zyklen über `FS_RENAME_n`). Die Warnungen der Schritte richten sich an Leser des Skripts; der Designer ersetzt sie durch eigene mit gezählten Daten.
- Ausgabe absichern: `DdlOutputTests` halten das ganze Skript des Vergleichs und die Statements des Designers fest.
- Audit (WP-26, `docs/audit-templates.md`): Die Audit-Tabelle plant `AuditPlanner` über `TableDesign` (keine eigenen DDL-Strings), nur der Trigger-Text kommt aus der Vorlage des Nutzers. Ein Trigger aus der Vorlage muss die Schranke des Schemaweges passieren und `SqlScript.Split` als **ein** Statement sehen (Tests in `AuditTemplateTests`). Kommentare im Trigger-Teil einer Vorlage landen im Quelltext in der Datenbank – Erklärungen gehören über `-- @trigger-source`. Die Vorlage hängt an der Verbindung (WP-51): immer `AuditTemplateSource.For(Active.Profile)`, nie eine Vorlage für alle. Typen vorhandener Audit-Spalten nach `OracleTypes.FitOf` (INSERT), nicht nach `ChangeOf` (MODIFY).
- Was DDL nie ausführt (Entscheidung des Nutzers): `TRUNCATE`, `ALTER TABLE … TRUNCATE/DROP (SUB)PARTITION`, `FLASHBACK TABLE … TO` (außer `TO BEFORE DROP`), `PURGE` (`SqlClassifier`, R5a).
