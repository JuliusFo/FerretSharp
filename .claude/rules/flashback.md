---
paths:
  - "src/FerretSharp.Core/Data/DataDiff*"
  - "src/FerretSharp.Core/Data/DataRestore*"
  - "src/FerretSharp.Core/Data/TimePoints*"
  - "src/FerretSharp.Core/Data/ChangeTracker*"
  - "src/FerretSharp.Core/Connections/ReadSession*"
  - "src/FerretSharp.UI/State/Workspaces/WorkspaceLifecycle.TimeTravel.cs"
  - "src/FerretSharp.UI/State/Workspaces/WorkspaceLifecycle.DataDiff.cs"
  - "src/FerretSharp.UI/State/Tabs/DataDiffTab*"
  - "tests/**/DataDiff*"
  - "tests/**/TimeTravel*"
---

# Flashback: Daten-Diff, Zeitreise, Zurücksetzen (WP-44, WP-78, ADR 0047/0048)

Aus `oracle.md` herausgelöst (Review 3.70.0, D2): Die Dateien von Diff und Zeitreise erreichte diese Regel nicht. Lese-Session und Leseweg: `oracle.md`, `connections.md`.

- `AS OF SCN` in Mengenoperationen: `MINUS`/`UNION` vergleichen keine LOB, LONG, XMLTYPE, Objekttypen (ORA-00932) – CLOB über `DBMS_LOB.SUBSTR` + `GETLENGTH`, BLOB über die Länge, den Rest weglassen (`QueryBuilder.BuildDiff`). LONG auch nicht über `CASE … IS NULL` in einer Mengenoperation.
- Was den Vergleich bricht (Oracle 23 gemessen): eine seit der SCN **hinzugefügte** Spalte nicht (liest sich damals als NULL), `TRUNCATE` schon; Undo zu kurz → ORA-01555/08180/08181, fremder Owner ohne `FLASHBACK` → ORA-01031 (`DataDiffErrors`).
- Eine Flashback-Abfrage in einer gesperrten Session nie über die Snapshot-Wiederholung laufen lassen (`repeatInNewSnapshot: false`): ORA-01555/01466 der Seite `AS OF` heilt kein neuer Snapshot, die ganze Abfrage lief sonst bis zu viermal mit Pausen (Review WP-44). `OracleDataAccess` mit `asOf` (WP-78) schaltet das für jede Abfrage selbst ab, auch beim Streaming.
- `AS OF SCN` gehört in den Leseweg (`IDataAccess.AsOf`, `QueryBuilder.From`), nie in einzelne Aufrufer; ein Zeitreise-Tab liest nur über `TabData` (Lese-Session). Läuft auch in `SET TRANSACTION READ ONLY` (Integrationstest WP-78).
- Eine Zeit wird als Abstand zu `SYSTIMESTAMP` umgerechnet (`QueryBuilder.ScnBackSql`), nie als Zeitpunkt mit Zone gebunden: die Uhr des Servers entscheidet (Entscheidung des Nutzers, WP-78). SCN-Abfragen liefern `DB_NAME/CON_NAME/CON_DBID` mit (alles ohne Recht) – ein gespeicherter Zeitpunkt gehört zu seiner Datenbank (`TimePoints.Mismatch`).
- `TIMESTAMP_TO_SCN(SYSTIMESTAMP)` braucht kein Recht, liegt aber bis zu ein paar Sekunden zurück: Ein Test, der direkt nach einem Commit den Startpunkt nimmt, wartet, bis die SCN den Commit (`MAX(ORA_ROWSCN)`) erreicht – sonst ORA-01466 bei frisch angelegten Tabellen oder Änderungen „vor“ dem Start; DDL auf der Tabelle (`ENABLE ROW MOVEMENT`) deshalb vor den letzten Zeilen, sonst flackert der Test unter Last (Review WP-44). Nach dem Commit reicht nicht: Ein Punkt binnen rund einer Sekunde nach `CREATE TABLE` gibt ebenfalls ORA-01466, wenn `TIMESTAMP_TO_SCN` schon sofort vorne liegt – `StartAfterAsync` nimmt den Startpunkt erst, wenn die Tabelle `AS OF` ihn lesbar ist (CI rot 2026-11-09).
