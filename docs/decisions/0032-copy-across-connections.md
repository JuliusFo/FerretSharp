# ADR 0032: Zeilen über Verbindungen hinweg kopieren

- Status: akzeptiert
- Datum: 2026-10-22
- Paket: WP-42

## Kontext

Testdaten entstehen bisher von Hand: Zeilen in Prod oder Test suchen, als INSERT exportieren, im Ziel ausführen, dabei Eltern vergessen und an FK-Verletzungen scheitern. WP-42 soll markierte Zeilen samt Eltern und gewählten Kindern in einen Workspace einer anderen offenen Verbindung kopieren. Das berührt drei Invarianten: Lesen und Schreiben sind getrennt (ADR 0006), Änderungen bilden einen Undo-Stapel aus Aktionen (ADR 0018), und Prod ist standardmäßig gesperrt. Dazu kommen das VPN (jeder Roundtrip kostet) und Schemas, die zwischen den Verbindungen anders heißen und nicht gleich aufgebaut sein müssen.

## Entscheidung

**Lesen auf der Quelle nur über den Leseweg.** Die Traversierung (`Core/Relations/RowTraversal`) liest mit `IDataAccess.ReadByKeysAsync` in der Session des Quell-Workspaces. Das SQL kommt aus `QueryBuilder.BuildRowsByKeys`: IN-Listen in festen Größen wie bei den Anzeigespalten, höchstens 500 Schlüssel je Statement, mehrspaltige Schlüssel als Tupel-IN. Das Filtermodell bleibt dabei unberührt. Gelesen wird, was dieser Workspace sieht, also auch seine nicht committeten Änderungen; der Dialog sagt das. Quelle Prod ist erlaubt. LOBs werden ganz gelesen, weil die Kopie sie schreibt.

**Die Traversierung kennt kein Ziel.** Startzeilen, Pfade von Kind-Beziehungen und abgewählte Eltern-Beziehungen gehen hinein, ein `RowGraph` mit Zeilen und Verknüpfungen kommt heraus, schrittweise erweiterbar (`FollowAsync`). Das Datensatz-Diagramm (WP-60) nutzt denselben Baustein ohne Kopie. Eltern werden immer bis zum Ende der Kette mitgenommen, sonst wäre die Beziehung im Ziel verletzt. Ein Elternteil, den der Graph schon über den Primärschlüssel kennt, wird ohne Abfrage verknüpft. Obergrenze 10 000 Zeilen.

**Prüfen vor dem Schreiben** (`SubgraphCopyPlanner`):
- **Owner-Abbildung:** Das Schema der Quellverbindung wird zum Schema der Zielverbindung (`ERP_DEV` → `ERP_TEST`), Tabellen anderer Schemas behalten ihren Owner.
- **Spalten nach Namen:** Eine Spalte, die im Ziel fehlt, verliert ihre Werte (Warnung). Eine Spalte nur im Ziel, NOT NULL ohne Default, lässt kein INSERT zu (Fehler). Ein abweichender Typ ist eine Warnung. Typen, die FerretSharp nicht schreibt (INTERVAL, LONG, XMLTYPE …), werden ausgelassen.
- **Identity GENERATED ALWAYS** nimmt keinen Wert an. Auf einer Schlüsselspalte ist das ein Fehler, weil die Schlüssel 1:1 übernommen werden; sonst vergibt das Ziel neue Werte. Die Art liest `ColumnInfo.IdentityAlways` aus `ALL_TAB_IDENTITY_COLS`, als Unterabfrage nur für Identity-Spalten.
- **Vorhandene Zeilen** findet ein Lesen nach Schlüssel im Ziel-Workspace: über den durchgesetzten Primärschlüssel, sonst den ersten Unique-Schlüssel. Je Tabelle wählt der Nutzer „überspringen“ (Standard), „aktualisieren“ (nur mit Primärschlüssel) oder „abbrechen“.
- **Reihenfolge:** Tabellen werden topologisch über die Beziehungen sortiert, die Zeilen tatsächlich verbinden, Eltern zuerst. Ein Kreis zwischen Tabellen wird gemeldet und nicht aufgelöst. Ein Selbstbezug wird auf Zeilenebene geordnet (Vorgesetzte vor ihren Leuten).
- **Trigger im Ziel** laufen mit; die Vorschau nennt sie (`ISchemaReader.GetTableTriggersAsync`).

**Schreiben als eine Aktion, ohne neuen Schreibweg.** `IDataEditor.CopyAsync` legt einen Savepoint an und führt die Schritte in Reihenfolge aus. Daraus wird **eine** `WriteAction` der neuen Art `Copy`: Undo nimmt sie ganz zurück, Redo führt dieselben Schritte nach Bestätigung erneut aus. Bei einem Fehler geht alles zum Savepoint zurück. Das Ziel muss beschreibbar sein (Prod erst nach dem Freischalten); Commit nur auf Befehl wie immer.

**Array Binding für die INSERTs** (Entscheidung des Nutzers, 2026-10-22). Einzelne INSERTs kosten einen Roundtrip je Zeile; über das VPN wären 10 000 Zeilen Minuten. `OracleSession.ExecuteNonQueryAsync(BatchSpec)` ist eine interne Überladung des Schreibwegs mit denselben Sperren: genau ein INSERT/UPDATE/DELETE/MERGE, nur in offener Transaktion, nie DDL. Jede Bind-Variable trägt einen Wert je Zeile, auch für CLOB und BLOB (gegen Oracle 23 geprüft). Blöcke à 500 Zeilen. Scheitert eine Zeile, meldet ODP.NET ORA-24381; die Fehlerübersetzung nimmt dann den ersten Fehler einer Zeile (ORA-02291 …), damit Meldung und Fehlercode wie bei einem einzelnen Statement aussehen. Updates laufen Zeile für Zeile mit `FOR UPDATE WAIT n` wie beim Grid.

**Kein DB-Link, kein Kopieren in der Datenbank.** Die Daten gehen durch FerretSharp: Quelle und Ziel sind getrennte Verbindungen, oft hinter verschiedenen Netzen, und ein DB-Link wäre ein Objekt in der Datenbank des Kunden.

## Konsequenzen

- Die Werte der kopierten Zeilen liegen bis Commit oder Rollback im Speicher der Aktion (für Redo), wie die Bind-Werte von Statements (ADR 0018). Die Änderungsübersicht zeigt höchstens 100 Statements einer Aktion und zählt den Rest.
- Schlüssel werden 1:1 übernommen. Neue Schlüssel im Ziel (Sequenzen, FKs umschreiben) und das Maskieren von Spalten stehen im Backlog.
- Beziehungen ohne Constraint (C#-Modell) zählen beim Lesen wie deklarierte. Ein Kreis über eine solche Beziehung blockiert die Kopie, obwohl das Ziel ihn nicht prüft; Abwählen der Beziehung hilft.
- Die Lesesperre bleibt unverändert. `ReadOnlyTests` kennen die neuen Methoden `ReadByKeysAsync` und `GetTableTriggersAsync`, die Batch-Überladung ist wie der übrige Schreibweg `internal`.
