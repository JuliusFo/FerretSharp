# ADR 0037: Das C#-Modell als Datei weitergeben

- Status: akzeptiert
- Datum: 2026-10-27
- Paket: WP-64

## Kontext

Das C#-Modell einer Verbindung (WP-11, ADR 0009) kommt aus dem Build des verknüpften Projekts: Der ModelHost lädt die DLLs und schreibt ein `ModelExport` als JSON, der Cache (WP-16) hebt es je Build auf. Wer das Projekt nicht bauen kann – Rückmeldung eines Kollegen des Nutzers: Pakete aus einem privaten NuGet-Feed, auf den der Empfänger keinen Zugriff hat –, bekommt heute kein Modell, also keine Entity-Namen, Enums und Beziehungen ohne FK-Constraint.

Fast alles, was das Modell nutzt (Namen, Enums, FK-Navigation `FkSource.ClrModel`, Präsentation, Abgleich WP-27, Code-Generierung, Stil von „Entity aus Tabelle“), liest nur das `ModelExport`, nicht das Projekt. Das Projekt brauchen die LINQ-Konsole (DbContext), die Verwendung im Code (WP-40, IL der Solution), Arbeits-Klone und das Bauen.

## Entscheidung

- **Export des geladenen Modells, kein neuer Lauf.** Die Modell-Seite schreibt das geladene `ModelExport` mit einer Herkunft (`ModelFileOrigin`: Projektname ohne Pfad, Konfiguration, DbContext, Exportzeit, Git-Branch und Commit aus den Dateien des Arbeitsordners, FerretSharp-Version) in eine Datei (`<Projekt>.ferretmodel.json`). Erst der Speichern-Dialog, dann im Hintergrund atomar (`AtomicJsonFile`). Aus einer verknüpften Datei wird nicht exportiert – sie wird so weitergegeben, wie sie ist.
- **Ein eigenes Feld im Profil, kein Umbau des Links.** `ConnectionProfile.ClrModelFile` steht neben `ClrProject`; beide schließen sich aus (Validator). `ClrProjectLink` bleibt, was es ist: LINQ-Konsole, Code-Index, Watcher und Arbeits-Klone vergleichen ihn und brauchen ein Projekt – mit einer Datei ist `ClrModelState.Link` null, und sie bleiben ohne weiteren Code aus. Was „ein Modell ist verknüpft“ meint (Modell-Seite, Statusleiste), fragt `ClrModelState.IsLinked`. Im Verbindungsdialog nimmt das Projektfeld beides; die Endung entscheidet (`.json` = Modelldatei). Ein Verbindungs-Export nimmt die Datei als lokalen Pfad mit wie das Projekt; beim Import gewinnt ein eigenes Modell (Projekt oder Datei) als Ganzes.
- **Laden ohne Build, Cache und ModelHost.** `ClrModelManager` liest die Datei, legt sie wie gewohnt über das Schema und beobachtet sie (`BuildOutputWatcher.ForFile`, dieselbe Ruhezeit): Ein neuer Export, über die Datei kopiert, lädt neu, auch wenn sie beim Verbinden noch fehlte. Beim Empfänger läuft kein fremder Code – der ModelHost führt den Code des Projekts aus, eine Modelldatei ist nur Daten.
- **Die Datei ist ein Austauschformat mit eigener Version.** `ModelFile.CurrentVersion` (1) ist unabhängig von `ModelExport.CurrentFormatVersion` (der Version des Cache). Änderungen am `ModelExport` bleiben additiv (neue Felder optional), dann liest eine neuere FerretSharp jede Datei derselben Version; eine Änderung, die das nicht ist, erhöht die Dateiversion. Ein Test hält eine Datei des Formats 1 fest. Eine neuere Datei lehnt FerretSharp mit „FerretSharp aktualisieren“ ab (`JsonFileState.Newer`), eine fremde Datei mit „keine Modelldatei“.
- **Die Datei ist fremde Eingabe** (Review): Ihre Namen landen in erzeugtem C#, das der Nutzer in ein Projekt kopiert. `ModelFileCheck` lässt nur C#-Bezeichner bzw. Typnamen ohne Klammern, Anführungszeichen und Operatoren zu und keinen Text mit Steuerzeichen; Pflichtfelder müssen da sein, Dateien über 64 MB werden nicht gelesen. Der Export prüft vorher dasselbe, damit es der Absender erfährt und nicht erst der Empfänger.
- **Eine Sprache.** Die Anzeigenamen der Enums stehen in der Sprache, in der der ModelHost sie aufgelöst hat (Windows-Sprache des Exportierenden). `ModelExport.Culture` hält sie fest (dafür `CurrentFormatVersion` 4: jeder Cache wird einmal neu exportiert), die Modell-Seite des Empfängers nennt sie (Entscheidung des Nutzers).
- **Geprüft und verworfen:** `ClrProjectLink` polymorph machen (Projekt | Datei) – 67 Verwendungen, und die Teile, die ein Projekt brauchen, müssten es überall wieder ausschließen; beim Export beide Sprachen schreiben (zweiter ModelHost-Lauf, beim Nutzer ~12 s; Entscheidung des Nutzers: eine Sprache); den Cache-Ordner weitergeben (Fingerprint an Pfade und Build gebunden, ohne Herkunft).

## Folgen

- Eine Modelldatei veraltet still: Die Modell-Seite und der Tooltip der Statusleiste nennen Exportzeit, Branch und Commit; der Abgleich mit der Datenbank (WP-27) zeigt Abweichungen wie beim Projekt.
- Die Datei enthält Tabellen- und Spaltennamen sowie Enum-Texte – beim Weitergeben so vertraulich wie der Quellcode des Modells.
- Export per Kommandozeile (z. B. in der Pipeline je Release-Branch) ist nicht dabei; er steht im Backlog.
