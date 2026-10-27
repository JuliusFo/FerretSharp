# WP-64 C#-Modell als Datei weitergeben

Branch `wp/64-model-file`, begonnen am 2026-10-27. ADR 0037. Baut auf WP-11 (Modell aus dem Build, ModelHost), WP-16 (Cache) und ADR 0016 (Watcher) auf.

## Auftrag (2026-10-27)
Rückmeldung eines Kollegen des Nutzers: Er würde FerretSharp gern an jemanden weitergeben, der keinen Zugriff auf die NuGet-Pakete des verknüpften `.csproj` hat. Ohne Build gibt es kein Modell, also weder Beziehungen noch Enum-Werte. Idee des Kollegen: das Modell exportieren.

## Planung (aus `docs/roadmap.md`)
- **Export** auf der Modell-Seite, Speichern-Dialog, Vorschlag `<Projekt>.ferretmodel.json`; geschrieben wird das geladene `ModelExport` (kein neuer ModelHost-Lauf) mit Kopf: Dateiversion, Exportzeit, FerretSharp-Version, Projektname ohne Pfad, Konfiguration, DbContext, Kultur der Anzeigenamen, Git-Branch und Commit (lokal gelesen, kein fetch).
- **Verknüpfen:** eigenes Feld `ConnectionProfile.ClrModelFile` neben `ClrProject`, beide schließen sich aus – kein Umbau von `ClrProjectLink`. Verbindungen exportieren/importieren nimmt das Feld mit.
- **Laden** ohne BuildOutput, Cache und ModelHost, Datei beobachten; Fehler (fehlt, kaputt, zu neu) als `Failed` mit Text.
- **Ohne Projekt:** keine LINQ-Konsole, keine Verwendung im Code, keine Arbeits-Klone, kein Bauen. Alles, was nur das Modell liest, geht.
- **Format** mit eigener Dateiversion, `ModelExport` nur additiv ändern, Fixture des ersten Formats im Test.

## Entscheidungen des Nutzers (2026-10-27)
1. Anzeigenamen der Enums **nur in einer Sprache** (der des Exportierenden); die Kultur steht in der Datei.
2. Export per **Kommandozeile in den Backlog**.
3. WP-64 **als Nächstes**, vor WP-55.

## Gebaut
- **Core:**
  - `ModelFile` (`ClrModel/Host`): `CurrentVersion` 1, `WriteAsync` (atomar, `JsonFiles.Options`), `ReadAsync` über `VersionedJsonFile` mit Grund je Zustand (fehlt, neuer, nicht lesbar, keine Modelldatei), `OriginOf` (Git über `GitFolder`), `SuggestedName`, `IsModelFile` (Endung `.json`). `ModelFileOrigin`, `ModelFileRead`.
  - `ModelExport.Culture` (vom ModelHost gesetzt, `CultureInfo.CurrentUICulture` nach `--culture`); `CurrentFormatVersion` 4 – jeder Cache wird einmal neu exportiert.
  - `GitFolder.Commit` (Ref-Datei, `packed-refs`, `commondir` eines Worktrees, losgelöster HEAD).
  - `ConnectionProfile.ClrModelFile` (in `LocalPaths`/`WithoutLocalPaths`); Validator: `.csproj` bzw. `.json`, nie beides (Fehler am Feld `ClrProject`); Import: ein eigenes Modell (Projekt oder Datei) gewinnt als Ganzes.
  - `ClrModelManager`: ohne Projekt mit Datei liest er die Datei (`StepReadModelFile`), legt sie über das Schema, beobachtet sie (`BuildOutputWatcher.ForFile`, auch solange sie fehlt – sofern der Ordner existiert) und lädt bei jeder Änderung neu. `ClrModelState.ModelFile`, `Origin`, `IsLinked`. `ExportAsync` (nur mit geladenem Modell eines Projekts). Fehlerart `ClrModelErrorKind.ModelFile`.
- **UI:**
  - Verbindungsdialog: **ein** Feld für Projekt und Modelldatei (Abweichung vom Plan, „Wahl Projekt/Modelldatei“: die Endung entscheidet, die einfachste Variante); bei einer Datei ohne Build/DbContext und mit eigenem Hinweis; Auswahl-Dialog mit `*.csproj;*.json`. `ConnectionForm.IsModelFile`.
  - Modell-Seite: „Als Datei exportieren …“ (Speichern-Dialog über `AskPathAsync`, danach im Hintergrund, Hinweis „Modell gespeichert als …“); mit Datei Abschnitt „Modelldatei“ (Pfad, Exportiert mit Version, Herkunft, Sprache der Anzeigenamen, Hinweis auf das, was nur mit Projekt geht), ohne Export und Bauen.
  - Statusleiste: „C# · Datei · …“, Tooltip „Modell aus einer Datei, exportiert … · Branch @ Commit“. `ModelFileText`.
  - LINQ-Knopf, Reiter „Verwendung“ und Arbeits-Klon bleiben ohne Code-Änderung aus (`State.Link` ist null).
- **Tests:** Core `ModelFileTests` (zurückgelesen, Format-1-Fixture, Gründe, Herkunft mit Git, Commit aus packed-refs/Worktree/losgelöst, Endung), `ModelFileLoadingTests` (lädt ohne ModelHost, fehlende Datei lädt, sobald sie kommt, ersetzte Datei lädt neu, Export eines Projekts und Wechsel auf die Datei, Export nur mit Projekt), `ConnectionProfileValidatorTests`, `ImportReviewTests`; UI `ModelFileTextTests`, `ShellTextTests` (Formular).

## E2E (2026-10-27)
Eigener Container mit der Beispiel-DB (`ferret-wp64`, Port 1564, danach entfernt), Testinstanz mit `--data-dir`, Deutsch dunkel, zwei Verbindungen (Beispielprojekt `samples/…Data.csproj`; Modelldatei), Passwort nicht gespeichert.
- **Geprüft (CDP):**
  - Verbindung mit Projekt: Modell-Seite mit „Neu laden“, „Als Datei exportieren …“, „Neu bauen“. Export-Knopf öffnet „Speichern unter“ mit `FerretSharp.SampleModel.Data.ferretmodel.json` und Filter „FerretSharp-Modell (*.json)“ (Fenster per `WM_GETTEXT` gelesen, per `WM_CLOSE` geschlossen, nicht bedient).
  - Die Datei selbst mit `ModelFile.OriginOf`/`WriteAsync` (kleines Konsolenprojekt im Scratchpad) aus dem Modell-Cache der Testinstanz geschrieben: 9 Entities, `de-DE`, Branch `wp/64-model-file`, Commit `19c5ce7`.
  - Verbindung mit Datei: Statusleiste „C# · Datei · 8 Entities · 4 Abw.“, kein Arbeits-Klon; Modell-Seite „Modelldatei“ mit Exportzeit, Herkunft, „Deutsch (Deutschland)“, Ladezeit 49 ms (Datei 7 ms), dasselbe Abgleichsergebnis wie mit dem Projekt (8/9 Entities, 43/44 Properties); kein Export, kein Bauen.
  - AUFTRAG: C#-Namen und Typen im Kopf, Enum `Versendet (VERSANDT)`, FK-Markierungen; kein „+ LINQ“, kein Reiter „Verwendung“.
  - Datei ersetzt (neuer Commit, `os.replace`): Tooltip zeigt nach ~2 s den neuen Stand. Datei mit `version` 9: Statusleiste „C#-Modell“ mit „… neueren FerretSharp (Format 9) …“; wiederhergestellt: geladen.
  - Verbindungsdialog mit Datei: Pfad im Feld, Hinweis zur Modelldatei, keine Build-/DbContext-Felder.
  - Log der Testinstanz ohne `[ERR]`/`[WRN]`.
- **Nicht geprüft:** Klicks mit echter Windows-Eingabe (nur gewöhnliche Knöpfe, keine eigene Maus-Logik); das Schreiben über den Speichern-Dialog selbst (native Dialoge nicht bedienen – der Weg dahinter ist `ClrModelManager.ExportAsync`, getestet in `ModelFileLoadingTests`).

## Review (2026-10-27, Schwerpunkt: fremde Datei als Einfallstor)
Was eine Modelldatei erreicht, Stelle für Stelle:
- **Lesen:** nur `System.Text.Json` in Records, keine Polymorphie, keine Typnamen, die aufgelöst würden, kein Prozess, kein Laden von Assemblies – beim Empfänger läuft nichts. LINQ-Konsole, Code-Index und Bauen hängen an `ClrModelState.Link` und bleiben mit Datei aus.
- **Oberfläche:** Blazor maskiert; `MarkupString` nur bei festen Icons; `grid.js` setzt Namen, Typen und Kommentare als `textContent`/`title`, Zellen als Elemente, `tooltip.js` als `textContent`.
- **SQL:** Tabellen und Spalten des Modells werden nur gegen das Schema aufgelöst (`ClrModelMapping`), Spalten liest es nur für Besitzer gefundener Tabellen; Filterwerte (auch Enum-Werte) sind Binds, Spalten prüft und quotet der `QueryBuilder`.
- **Gefunden und behoben:**
  1. **Namen in erzeugtem C#** (`CSharpCode.Identifier(property.Name)`, `// `-Kommentare, `DbSet`, Converter- und Typnamen in LINQ-Filtern und Objekt-Code): Aus dem ModelHost waren es kompilierte Namen, aus einer Datei beliebiger Text – `X = Process.Start(…)` oder ein Zeilenumbruch hinter `//` hätte Code in den kopierten Text gebracht. `ModelFileCheck`: Property-, Navigations-, Schlüssel- und Enum-Namen müssen C#-Bezeichner sein, Typnamen nur Bezeichner mit den Zeichen eines Typs (`.+<>,[]?`, Leerzeichen, Backtick), kein Text darf ein Steuerzeichen enthalten; Pflichtfelder müssen da sein (statt NullReference). Lesen lehnt ab, der Export prüft vorher dasselbe.
  2. **Größe:** Dateien über 64 MB werden nicht gelesen (`ModelFile.MaxBytes`; das Modell beim Nutzer hat etwa 3 MB).
  3. **`GitFolder.Commit`** (Export): Der Ref-Name aus `HEAD` ging ungeprüft in `Path.Combine` – `ref: refs/../../../x` las eine andere Datei, ein UNC-Pfad hätte eine Anmeldung an einem fremden Host ausgelöst; `commondir` folgte einer Freigabe ohne die Regel aus A31. Jetzt nur `refs/…` ohne `..`, Backslash und Doppelpunkt, `commondir` wie `gitdir`.
- **Bleibt bewusst:** Der Pfad der Modelldatei kommt vom Nutzer oder aus einem Verbindungs-Import nur mit „lokale Pfade übernehmen“ (wie das Projekt, A4); eine Datei auf einer Freigabe ist ein gewollter Weg, dorthin meldet sich Windows wie bei jedem Dateizugriff an.
- **Tests:** `ModelFileTests` (sechs Namen, die aus dem Code ausbrechen würden – Lesen und Export; Namen, wie der ModelHost sie schreibt; unvollständig; zu groß; Ref außerhalb von `.git`, Ref und `commondir` auf einer Freigabe – der Ref-Test schlägt ohne den Fix fehl).

## Offen
- Export per Kommandozeile und beide Sprachen: Backlog.
- Die Meldung „vom Absender neu exportieren lassen“ gibt es erst mit Dateiversion 2; heute meldet eine fremde oder unvollständige Datei „keine Modelldatei“.
