# Fix: INSERT-Export lässt Identity ALWAYS und virtuelle Spalten weg (`fix/insert-export-generated-columns`)

Branch `fix/insert-export-generated-columns` (von `main`), 2026-11-04.

## Anlass
Frage des Nutzers zu „Als INSERT kopieren“ auf markierten Zeilen: Der Kopf des Skripts sagte selbst, dass die ID-Spalte eine Identity `GENERATED ALWAYS` ist und Oracle Werte ablehnt (ORA-32795) – trotzdem stand die ID in jedem INSERT. Ursache: Der Export (WP-07) ist älter als `ColumnInfo.IdentityAlways` (WP-42); er kannte nur `IsIdentity`, schrieb alle Spalten und nannte jede Identity im Kopf als ALWAYS, auch eine `BY DEFAULT`. Virtuelle Spalten standen ebenso im Skript (ORA-54013).

## Entscheidung des Nutzers (2026-11-04)
- Spalten, die Oracle selbst füllt und für die es jeden Wert ablehnt (Identity `GENERATED ALWAYS`, virtuell), fehlen in Spaltenliste und Werten; Oracle vergibt neue Schlüssel. Der Kopf nennt jede weggelassene Spalte.
- Identity `BY DEFAULT` behält ihre Werte; der Kopf sagt, dass die Sequenz dabei nicht weiterzählt.
- Code-Review: alle Vorschläge übernommen („Jo, deine Vorschläge passen so.“) – den SQL-Editor gleich mit beheben statt in den Backlog; bei einer ALWAYS-Identity im Primärschlüssel nur eine Kopfzeile, keine Warnmeldung bei jedem Kopieren.

## Gebaut
- Core `InsertExport.Build`: schreibt nur Spalten, die einen Wert annehmen; besteht die Tabelle nur aus einer ALWAYS-Identity, `INSERT INTO … ("ID") VALUES (DEFAULT)` (eine leere Spaltenliste nimmt Oracle nicht). Ein Ergebnis nur aus virtuellen Spalten ergibt kein Statement, nur den Kopf mit „Keine Spalte nimmt einen Wert an“. Kopfzeilen je Identity- und virtueller Spalte, bei einer ALWAYS-Identity im Primärschlüssel zusätzlich „Neue Schlüssel: Verweise auf die alten Werte … ziehen nicht mit“ (Selbstbezug, getrennt kopierte Kindzeilen).
- Core `InsertExport.ForTable`: Ein Ergebnis des SQL-Editors kennt keine Identity und keine virtuelle Spalte (`OracleDataAccess.ColumnOf`); jede Spalte übernimmt sie von der gleichnamigen Spalte der Zieltabelle – in diese schreibt das INSERT –, dazu deren Primärschlüssel. Eine Spalte, die die Tabelle nicht hat (Alias), bleibt, wie sie ist.
- UI `ExportMatrix.CompleteInsertTable`: vervollständigt das Ziel direkt vor dem Bauen des Skripts; ein zweiter Klick, solange das läuft, kopiert nicht noch einmal. `ResultMenu` holt die Struktur der Tabelle aus dem Schema-Cache (einmal je Tabelle gelesen, meist schon mit dem Ergebnis geladen) über `RunDbAsync`: Scheitert das Lesen, schließt das Menü und der Fehlerdialog zeigt den Grund (`ShowFailure`); wurde das Menü inzwischen geschlossen, wird nichts kopiert.
- Texte in Englisch und Deutsch: `InsertExportIdentityAlways`, `InsertExportIdentityDefault`, `InsertExportIdentityByDefault`, `InsertExportVirtual`, `InsertExportNewKeys`, `InsertExportNothingToWrite` (ersetzen `InsertExportIdentity`).
- Core `CSharpRows.Initializers` („Als C#-Objekte kopieren“): lässt dieselben Spalten weg – EF Core schickte eine gesetzte ID einer Identity ALWAYS mit (ORA-32795). Entscheidung des Nutzers (2026-11-04): „Ja, der C#-Objektinit soll das auch weglassen würde ich sagen.“ Still, ohne Kommentarzeile je Objekt.
- Integrationstest: Das Skript läuft gegen Oracle auf einer Tabelle mit ALWAYS-Identity und virtueller Spalte und auf einer Tabelle nur aus einer ALWAYS-Identity.

## Bekannte Grenzen
- Neue Schlüssel heißt: Verweise auf die alten (Selbstbezug, Kindtabellen) bleiben stehen. Neue Schlüssel samt umgeschriebener Fremdschlüssel steht im Backlog (Fortsetzung von WP-42).
- Der `HasData`-Seed behält Identity- und virtuelle Spalten: EF verlangt den Schlüssel im Seed.

## Offen / nicht geprüft
- Nicht in der laufenden App geprüft (E2E): Im SQL-Editor liegt zwischen Klick und Zwischenablage jetzt das Lesen der Tabellenstruktur, wenn sie noch nicht im Cache ist; das Menü bleibt so lange offen.

## Nachtrag: Code-Review (2026-11-04)
Entscheidung des Nutzers: „Jo, deine Vorschläge passen so.“ (alle, 1 mit Variante B, 2 nur als Kopfzeile).
- 1 (M) SQL-Editor: Ergebnisspalten ohne Identity-/Virtual-Kennung, „Als INSERT kopieren“ schrieb die ID weiter; CHANGELOG versprach den Fix pauschal – behoben (`InsertExport.ForTable`, `ExportMatrix.CompleteInsertTable`).
- 2 (M) ALWAYS-Identity im Primärschlüssel: neue Schlüssel, Verweise ziehen nicht mit – Kopfzeile `InsertExportNewKeys`.
- 3 (K) `VALUES (DEFAULT)` nicht gegen Oracle getestet, Distinct-Assert konnte nicht scheitern – Integrationstest als Theory mit `EXPORT_ID_ONLY`, Assert entfernt.
- 4 (K) Kopf sagte „weggelassen“, das Statement nennt die Spalte mit DEFAULT – eigener Text `InsertExportIdentityDefault`.
- 5 (K) Protokoll und Indexzeile fehlten – diese Datei.

Zweiter Durchgang, Entscheidung des Nutzers: „Jop, leg los“ (alle).
- 6 (K) Scheiterte das Lesen der Struktur, blieb der Klick ohne Meldung (`RunDbAsync` meldet nicht selbst) – behoben (`ShowFailure`, Menü schließt).
- 7 (K) Menü während des Lesens geschlossen → „Zwischenablage nicht verfügbar“; zweiter Klick kopierte doppelt – behoben (nach dem Schließen nichts kopieren, zweiter Klick ignoriert).
- 8 (K) Ergebnis nur aus virtuellen Spalten ergab `INSERT INTO t () VALUES ()` – behoben (kein Statement, Kopfzeile `InsertExportNothingToWrite`).

Dritter Durchgang, Entscheidung des Nutzers: „Ja, behebe das noch.“
- 9 (K) Ohne Statement zählte die erste Kopfzeile die markierten Zeilen, der Hinweis 0 – behoben (beide 0).
