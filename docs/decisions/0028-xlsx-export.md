# ADR 0028: Export als Excel-Datei mit SpreadCheetah

- Status: akzeptiert
- Datum: 2026-10-17
- Paket: WP-53

## Kontext

Der Export kennt bisher CSV (Semikolon, deutsche Schreibweise, `DelimitedExport`) und INSERT-Skripte. Öffnet man ein CSV in Excel, rät Excel die Typen: Dezimalkommas hängen von den Ländereinstellungen ab, lange Schlüssel werden zu `1,23457E+37`, Texte mit `=` am Anfang zu Formeln (Gesamt-Review 3.31.0, A38), und Kopfzeile, Filter und Spaltenbreiten richtet man jedes Mal von Hand ein. Eine `.xlsx` mit typisierten Zellen löst das an der Quelle.

Eine `.xlsx` ist ein ZIP mit einigen XML-Teilen. Zur Wahl standen MiniExcel (Apache-2.0), das OpenXML SDK (`DocumentFormat.OpenXml`, MIT, Microsoft), SpreadCheetah (MIT) und ein eigener Schreiber über `System.IO.Compression` und `XmlWriter`.

## Entscheidung

**SpreadCheetah** (1.28.0) im Core, Schreiben in `Core/Data/XlsxExport` neben `DelimitedExport`. Entschieden vom Nutzer bei der Planung (2026-10-17).

- Für `net10.0` ohne Abhängigkeiten (DLL rund 420 KB), plattformneutral, keine Windows-APIs. Der Core baut weiter unter Linux.
- Schreibt nur vorwärts, Zeile für Zeile, direkt in einen `Stream`: wenig Speicher, und dieselbe API trägt später „Alle Zeilen des Filters“ (WP-54), das blockweise liest und schreibt.
- Kann alles, was das Paket braucht: typisierte Zellen (Zahl, Datum, Bool, Text), Zahlenformate, fixierte Kopfzeile (`FrozenRows`), Autofilter, Spaltenbreiten, Fettschrift. Text bleibt Text. Formeln entstehen nur über einen eigenen Zelltyp, also wird `=SUMME(…)` als Text geschrieben, und die Formel-Frage aus A38 stellt sich beim Excel-Export nicht.
- Der mitgelieferte Source Generator wird nicht gebraucht und ist ausgeschlossen (`ExcludeAssets="analyzers"`).

Verworfen:
- **OpenXML SDK**: alles möglich, aber großes Paket mit zwei Abhängigkeiten und viel eigener Code für Styles, Spaltenbreiten und Fixieren; streamend nur über den SAX-Weg (`OpenXmlWriter`).
- **MiniExcel**: streamt ebenfalls, aber Formate und Breiten nur je Spalte über Konfiguration; die Typen je Zelle (Zahl oder Text, je nach Stellenzahl) passen schlecht dazu.
- **Eigener Schreiber**: keine Abhängigkeit, aber die Feinheiten des Formats (Styles-Teil, Datumswerte als serielle Zahl, Escaping, Content-Types) wären dauerhaft eigener Code ohne Mehrwert.

**Werte** (`XlsxExport.Cell`):
- Zahlen als Zahl, solange Excel sie exakt hält: höchstens 15 signifikante Stellen. Längere (`BigNumber`, NUMBER(38)-Schlüssel, `long` über 2^53) werden zu Text in der Schreibweise des CSV.
- DATE als Datum mit Format `dd.mm.yyyy hh:mm:ss`, TIMESTAMP mit Millisekunden. Was Excel nicht hält, wird zu Text: feinere Bruchteile als Millisekunden, Datumswerte vor dem 01.03.1900 (Excel zählt erst ab 1900, mit dem Schaltjahr-Fehler 1900), Zeitzonen-Werte (Excel kennt keine Zeitzonen).
- Bool als Bool, RAW als Hex-Text, NULL als leere Zelle, LOBs wie beim CSV (nur vollständig geladene, sonst Warnung).
- Texte über 32 767 Zeichen (Grenze je Zelle) werden gekürzt, mit Warnung.

**Präsentation** (Entscheidung des Nutzers): Standard ist die Präsentationsschicht. Die Kopfzeile nimmt den C#-Namen, wenn C#-Namen eingeschaltet sind und die Spalte eine Property hat; Enum- und Bool-Spalten bekommen den Text, den auch das Grid zeigt. Hinter einer FK-Spalte mit Anzeigespalte (WP-31) steht eine zusätzliche Spalte mit den Namen, die das Grid schon nachgeschlagen hat. Der FK-Wert selbst bleibt eine Zahl. „DB-Namen, Rohwerte“ ist ein zweiter Menüeintrag, nur wenn es eine Präsentation gibt.

## Konsequenzen

- Neue Abhängigkeit im Core (MIT). Updates wie die übrigen Pakete (WP-36).
- Spaltenbreiten müssen beim Start des Blatts feststehen: `XlsxExport` misst sie vorher an den Zeilen. Bei WP-54 misst der erste Block.
- Eine Datei hat ein Blatt. Mehrere Ergebnisse des SQL-Editors in einer Datei bleiben im Backlog.
- Der CSV-Export bleibt, wie er ist (DB-Namen, Rohwerte).
