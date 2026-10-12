# ADR 0022: Anzeigespalten – eine geteilte Datei und Nachschlagen je Block

- Status: akzeptiert
- Datum: 2026-10-12
- Paket: WP-31 (Issue #11)

## Kontext

Tabellen wie `AUFTRAG` verweisen über IDs auf andere Tabellen (`KUNDE_ID`, `SDLAND_ID`). Das Grid zeigt `4711`; wer wissen will, welcher Kunde das ist, springt per FK oder merkt sich die IDs. Gewünscht (Idee des Nutzers, 2026-10-12): Neben dem Wert steht der Name der Zielzeile – `4711 · PetersPizza`. Dafür braucht jede Zieltabelle eine **Anzeigespalte**, und jemand muss die Namen holen, ohne die Datenbank spürbar zu belasten (Frage des Nutzers).

Entscheidungen des Nutzers (2026-10-12, `docs/roadmap.md`):
- Festgelegt **in FerretSharp**, nicht im C#-Code. Verworfen: ein Attribut an der Entity (`[DisplayColumn(nameof(Name))]` oder ein eigenes), das der ModelHost ausliest – es käme auf Prod erst nach dem Merge nach `release` an (ein Klon je Datenbank) und ginge nur mit C#-Projekt.
- **Eine Datei, globaler Pfad** in den Einstellungen (wie die Audit-Vorlage, WP-26), z. B. im Repository der Firma, damit die Kollegen dieselben Anzeigespalten haben.
- **Schlüssel ist der Tabellenname ohne Owner**: Das Schema ist auf Prod/Test/Dev dasselbe, der Schemaname kann abweichen.
- **Anzeige in derselben Zelle**, in den Optionen abschaltbar.

## Entscheidung

**Die Datei** (`DisplayColumnSource`, `Core/Display`):
- JSON mit Version und einem Objekt `displayColumns` (Tabelle → Spalte), Schlüssel ordinal sortiert, eingerückt – eine Zeile je Eintrag, damit sie sich in Git sauber diffen und mergen lässt. Unbekannte Eigenschaften oben (eine neuere FerretSharp-Version, später z. B. virtuelle FKs) bleiben beim Schreiben erhalten (`JsonExtensionData`).
- Ohne Pfad `display-columns.json` im Datenordner. FerretSharp schreibt nie in den Klon einer Verbindung und macht kein Git; committen tut der Nutzer.
- Markieren liest die Datei vorher neu und ändert nur den einen Eintrag (Änderungen der Kollegen nach einem `git pull` bleiben). Eine Datei, die kein gültiges JSON ist (z. B. ein Merge-Konflikt), wird gemeldet und **nie** überschrieben; eine Datei einer neueren Version wird gelesen, aber nicht geändert.
- Ein `FileSystemWatcher` auf den Ordner meldet Änderungen (300 ms Ruhe); unabhängig davon wird die Datei höchstens alle 3 s neu angesehen, wenn Ansichten fragen (Freigaben, die sich nicht beobachten lassen). `Changed` feuert nur, wenn sich Inhalt, Pfad oder die Einstellung „Namen neben Fremdschlüsseln“ wirklich geändert haben.

**Welche Spalten Namen bekommen** (`DisplayLinks`):
- Je Spalte der erste **einspaltige** FK (deklarierte zuerst, dann die des C#-Modells), dessen Ziel eine Anzeigespalte hat.
- Der referenzierte Schlüssel muss **PK oder Unique Key** des Ziels sein: Dann gibt es sicher einen Index, und jeder Wert trifft höchstens eine Zeile. Beziehungen des C#-Modells ohne Constraint auf eine Spalte ohne eindeutigen Schlüssel bekommen keine Namen (sonst ein Full Table Scan je Block). **Views** als Ziel scheiden damit aus – sie haben keinen erzwungenen eindeutigen Schlüssel (Entscheidung beim Start, einfachste Variante).
- FK-Spalte und Schlüssel des Ziels müssen zur selben Typfamilie gehören (Zahl, Text, Datum, Timestamp, RAW), sonst bindet der Wert nicht. Die Anzeigespalte darf kein LOB, LONG, RAW oder nur als „NULL oder nicht“ gezeigter Typ sein.

**Wie die Namen geholt werden** (`QueryBuilder.BuildLookup`, `IDataAccess.LookupAsync`, `DisplayNames`):
- **Kein JOIN in der Grid-Abfrage**: Fehlende Rechte auf die Zieltabelle (ORA-00942) würden das ganze Grid brechen, und die Lesesperre, die Zeilenidentität und das Paging bleiben unberührt. Stattdessen nach jedem Block (500 Zeilen) je verknüpfter Spalte die verschiedenen, noch unbekannten Werte sammeln und `SELECT key, display FROM ziel WHERE key IN (…)` auf dem Leseweg ausführen.
- **IN-Listen auf feste Größen** 1/10/50/100/500 aufgefüllt (letzter Wert wiederholt): Sonst wäre jede Anzahl ein eigenes Statement im Shared Pool (Hard Parse je Anzahl).
- **Auf der Session des Workspace**, damit eigene, nicht committete Zeilen und der Snapshot der Read-only-Transaktion stimmen; der Lookup startet keinen neuen Snapshot.
- **Cache je Grid (Tab)**, geleert, sobald das Grid ab der ersten Zeile liest (F5, Filter, Sortierung, nach Schreiben, Commit, Rollback). Abweichend vom Plan (je Workspace): So bleibt der Cache ohne eigene Invalidierung aktuell; ein Name, den ein anderer Tab desselben Workspace ändert, folgt mit dem nächsten Neuladen.
- Die Namen kommen **nach** dem Block: Das Grid zeigt erst den Wert, dann wird die Zeile mit Namen neu gezeichnet. Ein Lookup zur Zeit je Grid; ein Lookup einer älteren Generation (das Grid las inzwischen neu) wird verworfen.
- **Timeout 5 s** je Lookup (Statement wird abgebrochen, wie bei FK-Counts); nach Timeout oder Fehler (fehlendes Recht) ist die Spalte für den Tab abgeschaltet und der Nutzer bekommt einmal einen Hinweis.
- CHAR-Schlüssel vergleichen ohne Blank-Padding; ein CHAR-Wert gegen einen VARCHAR2-Schlüssel wird ohne Padding gebunden. Werte, die sich nicht exakt binden lassen (NUMBER mit mehr als 28 Stellen, Gleitkomma), werden nicht nachgeschlagen.

**Darstellung:** Der Name steht gedämpft hinter dem Wert in derselben Zelle (`__n` im Zeilenformat von grid.js), ein Wert ohne Zielzeile als „nicht gefunden“ (`__nm`) – das macht verwaiste Verweise sichtbar. Filtern, Sortieren, Bearbeiten, Kopieren und Export bleiben bei den Rohwerten (Präsentationsschicht). Die Formularansicht zeigt den Namen unter dem Wert. Markieren über das Kontextmenü des Spaltenkopfs; Badge „Name“ im Spaltenkopf und in der Spaltenansicht.

## Konsequenzen

- Last: je Block und verknüpfter Spalte höchstens eine Abfrage über einen eindeutigen Index, meist mit wenigen Werten – billiger als die Grid-Abfrage selbst; kleine Nachschlagetabellen sind nach dem ersten Block im Cache. Was zählt, sind Roundtrips (über VPN) und die belegte Session des Workspace. Reicht das nicht, lassen sich die Abfragen eines Blocks zu einer (`UNION ALL`) zusammenfassen (Backlog).
- Nach einem Schreiben, Commit oder Rollback werden die Namen der geladenen Blöcke neu geholt (ein Lookup je Spalte und Block).
- Mehrspaltige FKs, Views als Ziel, mehrere Anzeigespalten je Tabelle, Filtern/Sortieren nach dem Namen und eine Auswahl der Zielzeile beim Bearbeiten stehen im Backlog.
- Der Name im Ergebnis-Grid des SQL-Editors und der LINQ-Konsole fehlt (dort gibt es keine FK-Spalten im Sinne des Schemas).
