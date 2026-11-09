# ADR 0048: Zeitreise über `AS OF SCN` im Leseweg, Zurücksetzen über ausstehende Änderungen

- Status: akzeptiert
- Datum: 2026-11-09
- Paket: WP-78 (`docs/work-packages/wp-78-time-travel.md`), baut auf ADR 0047 (Daten-Diff, Lese-Session)

## Kontext

Mit WP-44 vergleicht FerretSharp den jetzigen Stand einer Tabelle mit einem Startpunkt, den der Nutzer vorher gesetzt hat. Der Nutzer will außerdem eine Tabelle sehen, wie sie zu einer gewählten Zeit war, einen Vergleich ab einer Zeit vor dem Klick starten und – als Rettung nach einem versehentlichen Commit auf Dev/Test – Zeilen auf den damaligen Stand zurückholen. Die Invarianten gelten weiter: Lesen und Schreiben bleiben getrennt (ADR 0006), Prod ist standardmäßig gesperrt, kein Diagnostics oder Tuning Pack (ADR 0045).

## Entscheidung (des Nutzers, 2026-11-09)

- **Zeitreise ist eine reine Ansicht.** Ein Tabellen-Tab mit `TimeTravelState` (SCN, gewählte Zeit, Datenbank) zeigt die Tabelle zu dieser SCN, nur lesend. Er liest auf der Lese-Session aus WP-44 (committeter Stand, wartet nie hinter dem Workspace), nie auf der Workspace-Session.
- **`AS OF` gehört in den Leseweg, nicht in die Aufrufer:** `IDataAccess.AsOf(scn)` liefert denselben Datenzugriff, dessen Abfragen ihre Tabelle `AS OF SCN :p_asof` nennen – Seiten, Zählen, ganze Lesungen (Export, Werte kopieren), LOB, Zeilen über Schlüssel und Anzeigespalten. So lesen Grid, Fußzeile, Export, Formular, LOB-Dialog und FK-Zähler eines Zeitreise-Tabs ohne eigenen Weg (`TabData`). Eine solche Abfrage wird nie in einem neuen Snapshot wiederholt (ORA-01555/01466 heilt das nicht, WP-44). Die Abfragen bleiben SELECTs und passieren die Lesesperre; `ReadOnlyTests` halten die Oberfläche fest.
- **Anzeigespalten lesen zum selben Zeitpunkt** (abweichend vom ersten Vorschlag „aktueller Stand“, vom Nutzer bestätigt): Mit `AsOf` am Datenzugriff ist das einfacher und richtiger. Der FK-Sprung eines Zeitreise-Tabs führt zum selben Zeitpunkt, „Aktuellen Stand öffnen“ zum jetzigen.
- **Zeit → SCN einmal, danach `AS OF SCN`:** Die Zeit wird als Abstand zu „jetzt“ auf der Uhr der Datenbank umgerechnet (`TIMESTAMP_TO_SCN(SYSTIMESTAMP - NUMTODSINTERVAL(:p_back, 'SECOND'))`), nicht als Zeitpunkt mit Zone gebunden (vom Nutzer bestätigt): Die Zeitzone des Servers spielt keine Rolle, alle Seiten eines Tabs liegen auf derselben SCN. Eine Zeit, die Oracle nicht mehr zuordnen kann (ORA-08180), meldet schon die Auswahl.
- **Genauere SCN für „jetzt“:** einmal je Verbindung `V$DATABASE.CURRENT_SCN`; ohne Recht (ORA-00942/01031) merkt sich die Lese-Session das bis zum nächsten Verbinden und nimmt still `TIMESTAMP_TO_SCN(SYSTIMESTAMP)`.
- **Ein Zeitpunkt gehört zu einer Datenbank:** SCN-Abfragen liefern `SYS_CONTEXT('USERENV', 'DB_NAME') || '/' || SYS_CONTEXT('USERENV', 'CON_NAME') || '/' || SYS_CONTEXT('USERENV', 'CON_DBID')` mit (alle ohne Recht, gegen Oracle 23 geprüft). Startpunkt und Zeitreise-Tab speichern sie; ein anderer Name oder eine gespeicherte SCN über der jetzigen (Datenbank neu aufgesetzt) verwirft den Startpunkt bzw. schließt den Tab mit Hinweis. Ältere Dateien ohne Namen werden nur über die SCN geprüft.
- **Zurücksetzen über ausstehende Änderungen, kein neuer Schreibweg:** Im Diff-Tab angehakte Zeilen werden im Tab der Tabelle (im Workspace, nötigenfalls geöffnet) zu ausstehenden Änderungen – gelöschte zu INSERTs, geänderte zu UPDATEs auf die alten Werte, hinzugekommene zu DELETEs – in einem Undo-Schritt (`ChangeTracker.Restore`). Geschrieben wird über Flush, Undo und Commit wie jede Änderung; auf gesperrten Workspaces ist es gesperrt. Die Werte werden vorher ganz gelesen (`DataRestore`): die alten `AS OF` auf der Lese-Session, die jetzigen auf der Workspace-Session – gegen sie prüft der Flush.
- **Kein Pack:** Flashback Query, `TIMESTAMP_TO_SCN`, `USERENV`, `V$DATABASE` und `V$UNDOSTAT` gehören zu keinem Pack. Nicht verwendet: Flashback Transaction Query (nur EE, `SELECT ANY TRANSACTION`), Flashback Table/Database (Schreibwege an der App vorbei).

## Folgen

- Eine Tabelle mit `GENERATED ALWAYS AS IDENTITY` bekommt gelöschte Zeilen nicht zurück (die alte ID lässt sich nicht einfügen, eine neue bräche Verweise) – abgelehnt mit Erklärung. `BY DEFAULT` behält die alte ID. Virtuelle Spalten werden ausgelassen.
- Ein INSERT lässt NULL weg wie eine neue Zeile im Grid: Eine Spalte mit DEFAULT, die damals NULL war, bekommt beim Zurückholen den Default. Unsichtbare Spalten (`INVISIBLE`) kennt die Spaltenliste nicht; sie werden nicht zurückgeholt.
- LONG, XMLTYPE und Objekttypen liest der Leseweg nur als Marker: Eine Zeile, deren Wert dort nicht NULL war, wird nicht zurückgeholt (gezählt und gemeldet).
- Datenbank-Audit-Trigger feuern beim Zurückschreiben wie bei jeder Änderung.
- `AS OF` funktioniert auch in der read-only Transaktion einer gesperrten Session (Integrationstest); Grenzen wie bei WP-44: Undo-Reichweite, `FLASHBACK`-Recht für fremde Owner, DDL dazwischen.
- Der Hinweis zur Undo-Reichweite im Zeitdialog (`V$UNDOSTAT.TUNED_UNDORETENTION`, vom Nutzer bestätigt) ist eine Schätzung ohne Garantie: frühere Zeiten bleiben wählbar, nur markiert; ohne Recht fällt er weg.
