# ADR 0007: Editieren – Ausstehend/Geschrieben, Sperre und Konfliktprüfung je Zeile

- Status: akzeptiert
- Datum: 2026-10-05

## Kontext

WP-09 bringt das Editieren auf Verbindungen ohne „Schreibgeschützt“ (ADR 0006: dort gibt es keine gesperrte Session). Änderungen sollen erst lokal gesammelt, dann bewusst in die Transaktion des Workspaces geschrieben und erst auf Befehl committed werden (Abschnitt 5.6). Zwei Workspaces derselben Verbindung sind zwei Oracle-Sessions und können sich gegenseitig sperren; andere Nutzer können Zeilen zwischen Laden und Schreiben ändern.

Gemessen mit Oracle 23 (`EditingTests`): Ein abgelaufenes `SELECT … FOR UPDATE WAIT n` meldet **ORA-00054**, nicht das dokumentierte ORA-30006.

## Entscheidung

- **Zwei Stufen je Zelle** im `ChangeTracker` (pro Tab): *ausstehend* (nur in FerretSharp) und *geschrieben* (DML in der Transaktion ausgeführt, Zeile gesperrt). Der Tracker kennt den geladenen Wert jeder geänderten Spalte; ein Schreibvorgang (`MarkFlushed`) liefert ein `FlushBatch`, das sich mit dem Savepoint zurücknehmen lässt (↶).
- **Schreiben** (`OracleDataEditor`, über `IDataEditor` am `IDatabaseConnection`): startet bei Bedarf die Transaktion, setzt den Savepoint `FS_FLUSH_n` und führt alle Operationen aus – Löschen, Ändern, Einfügen. Je bestehender Zeile zuerst `SELECT <geänderte Spalten> … FOR UPDATE WAIT n`, dann Vergleich mit dem geladenen Wert, dann DML über den Row-Key (PK, sonst ROWID). Jeder Fehler rollt auf den Savepoint zurück: alles oder nichts, die Änderungen bleiben ausstehend.
- **Konfliktprüfung nur über die geänderten Spalten** (Entscheidung des Nutzers): Wer andere Spalten derselben Zeile geändert hat, stört nicht. Bei Abweichung entscheidet der Nutzer: Überschreiben (gemerkt bis Commit/Rollback), eigene Änderung verwerfen oder abbrechen. Verschwundene Zeilen (`RowGoneException`) werden gemeldet.
- **Sperren:** Wartezeit 3 s (Einstellung, max. 60 s), ORA-00054 und ORA-30006 gelten beide als Sperrkonflikt. Die sperrende Session wird über `V$LOCKED_OBJECT`/`V$SESSION` ermittelt (dank MODULE/ACTION mit Workspace-Namen); ohne `SELECT_CATALOG_ROLE` steht ein Hinweis im Dialog.
- **Einfügen:** `INSERT … RETURNING ROWID INTO :p_rowid`, danach wird die Zeile über die ROWID neu gelesen (Defaults, Trigger, Identity), ihr Row-Key ersetzt den vorläufigen.
- **Commit** schreibt Ausstehendes vorher (Entscheidung des Nutzers); scheitert das, wird nicht committed. Auf Prod-Profilen ohne „Schreibgeschützt“ fragt FerretSharp vor dem Commit nach.
- **PK bestehender Zeilen ist nicht editierbar** (Entscheidung des Nutzers): Er ist die Zeilenidentität und Ziel referenzierender FKs.
- **Grid:** AG Grid im `readOnlyEdit`-Modus; jede Eingabe geht als `cellEditRequest` an .NET, das den Wert prüft (`OracleTypeMapper`) und die Zeile neu liefert. Der Editor prüft beim Enter selbst (`ValidateEdit`) und bleibt bei Fehlern offen – ein späteres Neustarten des Editors durch AG Grid hätte die Eingabe verloren.
- **Kein stiller Datenverlust:** Schließen von Tab (Ausstehendes), Workspace, Verbindung und App fragt, solange es ungeschriebene oder nicht committete Änderungen gibt.

## Konsequenzen

- Geschriebene Zeilen bleiben bis Commit/Rollback gesperrt; die Statusleiste zeigt das Alter der Transaktion und warnt ab 10 Minuten. Der Keep-alive hält solche Sessions offen (Backlog: Erinnerung/automatischer Rollback).
- Bei Verbindungsverlust ist die Transaktion weg; das Banner sagt das, die Schutzabfragen entfallen dann.
- LOBs, exotische Typen und PK-Änderungen bleiben vorerst nur lesbar (LOB-Editor in WP-10, Rest im Backlog).
