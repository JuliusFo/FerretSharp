# ADR 0036: Audit-Tabellen lesen sich wie ihre Basistabelle

- Status: akzeptiert
- Datum: 2026-10-27
- Paket: WP-63

## Kontext

Eine Audit-Tabelle (WP-26) hält Kopien der Spalten ihrer Basistabelle. Im Grid war sie bisher eine Tabelle ohne Beziehungen: keine FK-Sprünge, keine Namen neben FK-Werten, Enum-Werte als Zahl, und welche Werte eine Version geändert hat, sah man nur über „Markierte vergleichen“ Zeile für Zeile. Die Zuordnung Tabelle ↔ Audit-Tabelle kennt FerretSharp seit WP-26 über die Namensregel der Vorlage der Verbindung (seit WP-51 je Verbindung bzw. Gruppe).

Wünsche des Nutzers (2026-10-27): dieselben „Verlinkungen“ wie die Haupttabelle (Enums, FKs, C#-Namen), aber keine von anderen Tabellen auf die Audit-Tabelle; ein Sprung zum Hauptdatensatz; geänderte Zellen leicht eingefärbt, ohne für Hunderte Zeilen den Vergleich aufzurufen; Insert und Delete erkennbar.

## Entscheidung

- **Beziehungen nur ausgehend, abgeleitet beim Lesen, über eine eigene Methode.** `SchemaCache.LinksOf(audit)` gibt die ausgehenden Beziehungen der Basistabelle auf die Spalten der Audit-Tabelle umgeschrieben zurück (`ForeignKeyInfo.Audit = Copied`, Quelle bleibt: der C#-Hinweis gilt weiter) und eine zum Basisdatensatz über den Primärschlüssel der Basistabelle (`Audit = BaseRow`, Quelle `Convention`). `OutgoingOf`, `IncomingOf`, `ForeignKeys` und damit die Struktur-Ansichten, Index-Hinweise, Entity-Code, die Diagramme, das Datensatz-Diagramm und das Kopieren sehen sie nie – nur die Lesewege (Grid, Formular, Menüs, Namen neben FK-Werten) fragen `LinksOf` (Review: zuerst steckten die Kopien in `OutgoingOf`, und fünf Stellen mussten sie wieder herausfiltern; ein neuer Aufrufer hätte sie für Constraints gehalten). Nichts wird gespeichert: Die Zuordnung entsteht aus Vorlage, Tabellenliste und Beziehungen und wird neu gebaut, sobald sich eines davon ändert. Die Vorlage fragt der Schema-Cache beim Laden und wenn ein Tab ab der ersten Zeile liest (`RefreshAuditTemplate`), nie je Nachschlagen – die Datei kann auf einem Share über das VPN liegen, und nachgeschlagen wird beim Rendern. Für „ist Audit-Tabelle“ gibt es eine Regel (`AuditStatus.Pairs`) für Audit-Zeile und Grid.
- **Markiert statt als eigene Quelle.** `FkSource` bleibt die Herkunft (Invariante: Enum, kein `IsVirtual`); `ForeignKeyInfo.Audit` sagt nur, dass die Beziehung zur Lesesicht einer Audit-Tabelle gehört (das Sprungmenü nennt den Basisdatensatz „Hauptdatensatz“).
- **Spalten über die Entity der Basistabelle, ohne deren Entity zu sein.** `TablePresentation.Create(…, columnsOf: base)` nimmt Property-Namen, Typen und Enum-Mitglieder der Basis-Entity für gleichnamige Spalten; `Entity` bleibt null. LINQ-Filter, HasData und Objekt-Code der Audit-Tabelle bleiben damit aus – sie würden gegen die Basistabelle laufen.
- **Vorversion in der Datenbank, nach dem Block.** Zwei optionale Anweisungen der Vorlage: `@version-order` (eigene Spalte, NOT NULL, nach der die Versionen eines Basisdatensatzes laufen) und `@action` (eigene Spalte und ihre Werte für insert/update/delete). Nach jedem Block des Grids fragt eine Abfrage (`QueryBuilder.BuildAuditChanges`) je Zeile die Nummer der Version und ein Zeichen je vergleichbarer Basisspalte: `ROW_NUMBER`/`LAG` über `PARTITION BY` Basisschlüssel `ORDER BY` Versionsspalte, ROWID; `DECODE` vergleicht NULL-sicher. Die Versionen werden über **alle** Zeilen der Basisschlüssel des Blocks nummeriert, bevor die Zeilen gewählt werden – die Vorversion zählt auch, wenn der Filter des Grids sie auslässt, und die Sortierung des Grids spielt keine Rolle. Wie die Anzeigespalten (WP-31): eine Abfrage zur Zeit auf der Session des Workspaces, ohne neuen Snapshot; eine gescheiterte schaltet die Hervorhebung im Tab ab (einmal gemeldet), eine abgebrochene wird einmal wiederholt. Jedes Lesen ab der ersten Zeile plant neu (Vorlage, Einstellung), Neuladen an Ort und Stelle (nach Schreiben, Commit, Rollback) vergisst die Markierungen, und eine Antwort nimmt sie auch weg. Behalten werden Markierungen für so viele Zeilen, wie das Grid hält.
- **Geprüft und verworfen:** die Vorversion in der Seitenabfrage selbst (`LAG` in einer Inline-View um jede Seite) – ROWID und LOB-Projektionen der Seite liefen dann über die View, und jede Seite sortierte die ganze Audit-Tabelle; die Vorversion im Grid suchen – sie steht oft auf einer anderen Seite oder ist ausgefiltert; die Versionsspalte raten – zwei Zeitspalten oder eine nicht steigende ID gingen schief (Entscheidung des Nutzers: Anweisung in der Vorlage).

## Folgen

- Je Block eine Abfrage mehr; ohne Index auf den Basisschlüssel-Spalten der Audit-Tabelle liest jede die Audit-Tabelle ganz. Einstellung „Änderungen in Audit-Tabellen hervorheben“ (Standard an) schaltet beides ab.
- LOBs, XMLTYPE, JSON und Objekttypen werden nicht verglichen (dieselbe Grenze wie `{each comparable}`), die Schlüsselspalten auch nicht.
- Eine Basistabelle ohne Primärschlüssel: keine Versionen, kein Sprung zum Basisdatensatz – die Beziehungen der Basistabelle kommen trotzdem.
- Ein Sprung von der Haupttabelle in ihren Verlauf (eingehend) ist bewusst nicht dabei; er steht im Backlog.
