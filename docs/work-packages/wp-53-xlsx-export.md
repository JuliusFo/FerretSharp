# WP-53 Export als Excel-Datei
Branch `wp/53-xlsx-export`. ADR 0028. Aus der Ideensammlung vom 2026-10-17, vom Nutzer als Paket übernommen und am selben Tag begonnen.

## Planung (aus `docs/roadmap.md`)
- Export als `.xlsx` mit typisierten Zellen statt Text: Zahlen als Zahl, DATE/TIMESTAMP als Datum mit Format, Text als Text (damit entfällt beim Export die Formel-Frage aus A38), NULL als leere Zelle; Kopfzeile fixiert, Autofilter, Spaltenbreiten nach Inhalt.
- Für die markierten Zeilen eines Tabs und für Ergebnisse von SQL-Editor und LINQ-Konsole; Speichern über den nativen Dialog wie die übrigen Exporte; LOBs wie beim CSV.
- Grenzen: 1 048 576 Zeilen je Blatt; Zahlen mit mehr als 15 signifikanten Stellen und Nanosekunden verliert Excel → als Text; Zeitzonen-Werte als Text.

## Entscheidungen des Nutzers (2026-10-17)
- **Bibliothek:** SpreadCheetah (Vergleich mit MiniExcel, OpenXML SDK und eigenem Schreiber im ADR).
- **„Alle Zeilen des Filters“** wird ein eigenes Paket für CSV und Excel: WP-54 in `docs/roadmap.md`.
- **Präsentation:** Die Roadmap sagte „wie beim CSV“, aber der CSV-Export schreibt DB-Namen und Rohwerte und bleibt so. Excel nimmt standardmäßig die Präsentationsschicht. Ein zweiter Menüeintrag „(DB-Namen, Rohwerte)“ erscheint nur, wenn es eine Präsentation gibt; es gibt keine Einstellung dafür.
- **Ein Blatt je Ergebnis** bei mehreren Ergebnissen des SQL-Editors: in den Backlog.

## Entscheidungen in der Umsetzung (einfachste Variante, Fragen unten)
- **Kopfzeile:** der Property-Name, sobald C#-Namen eingeschaltet sind („daneben“ und „vorn“ gleich, eine Zelle hat nur einen Namen); Spalten ohne Property behalten den DB-Namen bzw. im SQL-Ergebnis den Namen aus der Abfrage.
- **Enum-Werte** wie im Grid: `Gewerbe (2)`. Konvertierte Bools werden echte Excel-Bools. Ein Wert ohne Member bleibt der Wert selbst, eine Zahl bleibt also eine Zahl.
- **Anzeigespalten** (WP-31): eine eigene Spalte `KundeId · NAME` direkt hinter der FK-Spalte, mit den Namen, die das Grid schon nachgeschlagen hat (keine weitere Abfrage). Der FK-Wert selbst bleibt eine Zahl.
- **Zusätzliche Grenzen** (in der Roadmap ergänzt): Datum vor dem 01.03.1900 als Text, Zeitstempel genauer als Millisekunden (nicht erst Nanosekunden) als Text, `long` über 2^53 als Text, Texte über 32 767 Zeichen gekürzt mit Warnung.
- **Ganze Zahlen** bekommen das Format `0`: Mit „Standard“ zeigt Excel einen 12-stelligen Schlüssel als `1,23457E+11`.
- **Datumsformat** `dd.mm.yyyy hh:mm:ss` wie im Grid, TIMESTAMP mit `.000` (der Punkt vor den Millisekunden ist das Dezimaltrennzeichen, unter deutschen Einstellungen ein Komma).
- Die Datei entsteht im Speicher (markierte Zeilen, höchstens die geladenen Blöcke) und geht über `IFileSaveService.SaveBytesAsync` an den Host; erst WP-54 braucht Streaming.

## Gebaut
- **Core:** `XlsxExport.BuildAsync` (`Core/Data`): Typzuordnung, Styles vor dem Blatt (die Breiten müssen beim Start feststehen), fixierte Kopfzeile, Autofilter, Breiten 6–60 Zeichen, gültiger Blattname. `ExportColumn` (Kopf, Quelle, Spalte, optional `Present`), `ExportColumn.Raw`, `DiffersFromRaw`. `ExportFile`. `ExportWarnings` kann zusätzlich gekürzte Werte und Hinweise zur ganzen Datei. `PresentedExport` (`Core/ClrModel`): Spalten eines Tabellen-Grids samt Namensspalten, `Column` für eine einzelne Spalte (auch für SQL-Ergebnisse). Paket SpreadCheetah 1.28.0 (`ExcludeAssets="analyzers"`). Texte `ExportTruncatedOne/Other`, `ExportSheetRowLimit`.
- **UI:** `ExportActions.SaveFileAsync` (baut nach dem Schließen des Menüs, dann der native Dialog). Einträge in `GridContextMenu` (neuer Parameter `Names` aus `FerretGrid.Names`, über `TabView`) und `ResultMenu` (neuer Parameter `ExportColumns` aus `SqlResultPresentation.ExportColumns()`). Texte `GridMenu_SaveXlsx…`, `Export_XlsxFilter`.
- **Tests:** Core `XlsxExportTests` (Typen, 15 Stellen, `BigNumber`/`long`, Millisekunden und 1900, double/float/NaN, LOBs und Kürzen mit Warnungen, Kopfzeile/Filter/Breiten, Blattname, Spaltenbuchstaben, Präsentation mit Property-Namen, Membern und Namensspalte, ohne Modell gleich roh) über `XlsxSheet` (liest die Datei als XML zurück). UI `SqlResultPresentationTests` (Exportspalten eines SQL-Ergebnisses).

## E2E (2026-10-17)
Eigene Instanz (`--data-dir`, eigener WebView2-Ordner), eigener Container `ferret-e2e-wp53` (danach entfernt), Beispielmodell verknüpft, CDP-Eingabe.
- **Excel selbst** (eine Core-Datei, schreibgeschützt in einer unsichtbaren Excel-Instanz geöffnet): Datum und Millisekunden erscheinen im deutschen Format, `=…` als Text, keine Reparaturmeldung. Die zuerst maskierten Punkte im Datumsformat (`dd\.mm\.yyyy`) zeigt Excel genauso wie unmaskierte – die Maskierung ist wieder raus.
- Die Einträge erscheinen im Grid und im SQL-Ergebnis, der Rohwert-Eintrag nur mit Präsentation; der native Dialog bietet `KUNDEN.xlsx` bzw. `SQL 1.xlsx` als „Excel-Arbeitsmappe“ an (nur ausgelesen, per `WM_CLOSE` geschlossen).
- **Nicht geprüft:** Speichern über den Dialog bis zur Datei (native Dialoge werden nicht bedient; Inhalt über die Core-Tests und die Excel-Prüfung), die Namensspalte einer Anzeigespalte in der App (nur Unit-Test, der frische Datenordner hatte keine Anzeigespalten), echte Windows-Eingabe (`SendInput`; die Einträge sind gewöhnliche `@onclick`-Knöpfe).

## Nachfragen beim Abschluss (Antworten des Nutzers, 2026-10-17)
- Kopfzeile bei „C#-Namen daneben“: Property-Name bleibt (so gebaut, „passt erstmal“).
- Enum-Werte bleiben wie im Grid: `Gewerbe (2)`.
- Namen, die das Grid noch nicht nachgeschlagen hat, schlägt der Export nicht selbst nach – sie bleiben leer.

## Offen
- Mehrere Blätter je Datei (Backlog) und alle Zeilen des Filters (WP-54, `docs/roadmap.md`).
