# Texte und Sprachen (WP-29)

Die Oberfläche gibt es auf Englisch (Standard) und Deutsch. Entscheidung und Hintergrund: ADR 0017. Diese Datei ist die Arbeitsanleitung: wie ein Text in den Code kommt und wie er übersetzt wird.

## Kurz

- Jeder Text, den der Nutzer sieht, steht in einer Ressource: `Resources/<Bereich>Text.resx` (Englisch) und `Resources/<Bereich>Text.de.resx` (Deutsch), im Core unter `src/FerretSharp.Core/Resources/`, in der UI unter `src/FerretSharp.UI/Resources/`, im ModelHost unter `src/FerretSharp.ModelHost/Resources/`.
- Der Build erzeugt daraus eine Klasse `<Bereich>Text` (Namespace `FerretSharp.Core.Resources` bzw. `FerretSharp.UI.Resources`) mit einer Eigenschaft je Schlüssel: `ConnectionText.NameMissing`. Ein fehlender Schlüssel ist ein Compilerfehler (`Directory.Build.targets`).
- Neuer Text = Schlüssel in **beiden** Dateien. `ResourceTests` (Core.Tests, UI.Tests) prüfen, dass jeder Schlüssel in beiden Sprachen existiert, nicht leer ist und dieselben Platzhalter hat.
- Die App setzt beim Start nur `CultureInfo.CurrentUICulture` (Einstellung *Sprache* bzw. `--lang=en|de`). `CurrentCulture` bleibt: Zahlen und Daten formatieren wie vorher. Umschalten wirkt nach einem Neustart.
- Die Tests laufen mit deutscher UI-Kultur (`tests/Shared/UiCulture.cs`), damit die älteren Tests ihre deutschen Texte weiter prüfen; englische Texte prüft ein Test mit `using (UiCulture.Use("en")) { … }`.

## Bereiche (eine resx je Bereich)

| Projekt | Datei | Inhalt |
|---|---|---|
| UI | `CommonText` | Wörter, die überall vorkommen (Abbrechen, Schließen, Zurück, Kopieren, An/Aus …). Nur ergänzen, wenn ein Wort wirklich bereichsübergreifend ist. |
| UI | `ShellText` | Shell, Statusleiste, Verbindungen (Dialog, Wechsler, Seite), Workspaces, Tabs, Einstellungen, Tastenkürzel, allgemeine Dialoge |
| UI | `GridText` | Tabellen-Grid, Kontextmenü, Filter, Formular, Zeilenvergleich, LOB-Dialog, Schreibprobleme |
| UI | `SqlEditorText` | SQL-Editor, LINQ-Konsole, Ergebnis-Grid, Ausführungsplan, Laufanzeige, Verlauf, Monaco |
| UI | `SchemaViewText`, `ModelViewText`, `CompareViewText` | Explorer, Tab-Ansichten (Spalten, Constraints, Indizes, Abhängigkeiten, DDL), PL/SQL; Modellseite; Schema-Vergleich |
| Core | `ClrModelText` | C#-Modell, LINQ-Konsole, Code-Generierung, Build-Ausgabe |
| Core | `QueryText`, `DataText`, `SchemaText` | Query (Filter, SQL-Skripte, FK-Navigation, Export), Data und Forms, Schema |
| Core | `OracleText`, `CompareText`, `ConnectionText`, `SettingsText`, `WorkspaceText` | Oracle-Schicht, Schema-Vergleich/DDL, Verbindungen, Einstellungen (Tastenkürzel), Workspaces und IO |
| ModelHost | `ModelHostText` | Meldungen und Schritte des Hilfsprozesses |

Klassennamen sind über alle Projekte eindeutig (die Razor-Komponenten sehen Core- und UI-Ressourcen).

## Schlüssel

- PascalCase nach der **Bedeutung**, nicht nach dem Wortlaut: `PortOutOfRange`, nicht `PortMussZwischen…`.
- In der UI mit Komponente als Präfix, wenn der Text zu einer Komponente gehört: `Settings_LockWaitHint`, `Grid_CopyAsInsert`. Im Core ohne Präfix, die Datei ist der Bereich.
- Plural: zwei Schlüssel mit `One`/`Other`: `RowsLoadedOne` („{0} row loaded“), `RowsLoadedOther` („{0} rows loaded“), Aufruf über `TextFormat.Plural(count, X.RowsLoadedOne, X.RowsLoadedOther, …)` – die Zahl ist `{0}`, weitere Argumente ab `{1}`.
- `<comment>` in der englischen Datei: Pflicht bei Platzhaltern (was ist `{0}`?) und bei kurzen, mehrdeutigen Wörtern (Button? Überschrift? Zustand?).

## Wie ein Text in den Code kommt

| Fall | So |
|---|---|
| Fester Text in Razor | `@ShellText.Settings_Title`, Attribute `title="@GridText.Grid_CopyValue"` |
| Text mit Werten | `TextFormat.Format(GridText.Grid_RowsSelected, count)` – ganzer Satz mit Platzhaltern, **nie Satzteile zusammenkleben** (Wortstellung ist je Sprache anders) |
| Text mit Markup (`<code>`, `<b>`, Link, `<Kbd>`) | `<Fmt Text="@X.Y"><Arg0><code>…</code></Arg0></Fmt>` (`Components/Fmt.cs`, bis `{3}`) |
| Zahl/Datum im Text | Kultur wie vorher: Interpolation/`CurrentCulture` → `TextFormat.Format(…)`; Code, der ausdrücklich `German` nimmt → `TextFormat.Format(German, …)`. Die Formatkultur bleibt in WP-29 unverändert (Backlog). |
| Beschriftungs-Tabellen in `@code` | als Eigenschaft `=>`, **nicht** `static readonly`: Ein statisches Feld behält die Sprache des ersten Zugriffs (Tests wechseln die Kultur). |
| Meldung aus dem Core | Der Core baut den fertigen Satz aus seiner eigenen Ressource; die UI zeigt ihn. Der Core kennt keine UI-Ressourcen. |
| Text für JS (Grid, Monaco) | aus .NET übergeben; JS enthält keine Texte |
| Tastenkürzel in Texten | wie bisher über `ShortcutService` (`Hint`/`With`/`Label`), als Platzhalter-Argument |

## Was nicht übersetzt wird

- SQL, Oracle-Namen, Schlüsselwörter (`NULL`, `COMMIT`, `SELECT … FOR UPDATE`), Code-Beispiele. Kommentare in **erzeugtem** Code und DDL, die der Nutzer liest, sind dagegen Text (Ressource).
- Log-Meldungen (Serilog): Englisch, direkt im Code.
- Ausnahmen für Programmierfehler (`InvalidOperationException`, `ArgumentException`): Englisch, direkt im Code. Fachliche Ablehnungen (`RefusedException`) und Fehler, die der Nutzer sieht, kommen aus Ressourcen.
- Gespeicherte Bezeichner: JSON-Schlüssel, Enum-Werte in Dateien, Namen der Tastenkürzel-Aktionen, MODULE/ACTION der Session.
- Sprachnamen in der Sprachauswahl („English“, „Deutsch“): jede Sprache unter ihrem eigenen Namen.
- Enum-Anzeigenamen aus dem C#-Projekt (`[Display]`): kommen über den ModelHost in der Windows-Sprache.

## Gespeicherte Texte

Texte, die bei der Erstellung gespeichert werden (Standardnamen für Tabs, Workspaces), entstehen in der Sprache zum Zeitpunkt der Erstellung und bleiben so. Code darf gespeicherte Texte nie mit einer Ressource vergleichen (die Sprache kann sich ändern) – dafür einen Zustand speichern.

## Stil

**Deutsch:** wie bisher (du-Form, typografische Anführungszeichen „…“, Gedankenstrich –, Auslassung …). Beim Umziehen bestehender Texte den deutschen Wortlaut **unverändert** übernehmen – die Tests prüfen ihn.

**Englisch:** US-Schreibweise (color, canceled), Satzanfang groß, sonst klein (Sentence case: „Copy value“, „Lock wait“, nicht „Copy Value“). Den Nutzer mit „you“ ansprechen, knapp, ohne Ausrufezeichen. Typografische Anführungszeichen “…” und Apostroph ’ (nicht in Code-Bezeichnern), Gedankenstrich – mit Leerzeichen wie im Deutschen, Auslassung …. Bestätigungsfragen beginnen mit dem Verb („Delete connection X?“), ohne „Really“. Zahlen und Einheiten wie im Deutschen (`5 s`, `2 min`).

## Glossar

Ein Begriff, eine Übersetzung – überall gleich. Neue Fachbegriffe hier ergänzen.

| Deutsch | Englisch | Hinweis |
|---|---|---|
| Verbindung | connection | |
| Verbindungsart (Feld „Umgebung“): Entwicklung / Test / Produktion / Sonstige | environment: Development / Test / Production / Other | Badges bleiben DEV / TEST / PROD |
| Gruppe (Verbindung) | group | |
| Neu verbinden / Trennen | reconnect / disconnect | |
| Session trennen (Notausgang bei hängender Abfrage) | disconnect session | |
| Workspace | workspace | klein im Satz, groß am Satzanfang |
| Session | session | Oracle-Session; nicht „Sitzung“ |
| Sitzung (Lauf der App, z. B. `--theme` gilt für die Sitzung) | session | |
| Tab | tab | |
| Explorer | explorer | |
| Statusleiste | status bar | |
| Einstellungen | settings | |
| Tastenkürzel | shortcut | |
| schreibgeschützt | read-only | |
| freischalten / Freischaltung (Prod beschreibbar machen) | unlock | |
| sperren / gesperrt (Workspace wieder schreibgeschützt) | lock / locked | |
| Zeilensperre, gesperrte Zeile | row lock, locked row | |
| ausstehende Änderungen | pending changes | noch nicht in der Session |
| geschrieben (in der Session, nicht committet) | written | nicht „flushed“ |
| Commit / Rollback | commit / rollback | als Verb und Substantiv; Knopf „Commit“, „Rollback“ |
| verwerfen | discard | |
| übernehmen (Dialog) | apply | |
| rückgängig | undo | |
| Transaktion (offen) | transaction (open) | |
| Abfrage | query | |
| Abfrage läuft … | Query running … | |
| Ergebnis | result | |
| Zeile / Zeilen | row / rows | |
| Spalte | column | |
| Wert | value | |
| Filter | filter | |
| FK-Sprung, springen | FK jump, jump | |
| referenzierende / referenzierte Zeilen | referencing / referenced rows | |
| Formular (Formularansicht) | form (form view) | |
| Zeilen vergleichen | compare rows | |
| Ansicht (eines Tabs): Daten, Spalten, Constraints, Indizes, Abhängigkeiten, DDL | view: Data, Columns, Constraints, Indexes, Dependencies, DDL | |
| Schema-Vergleich | schema comparison | |
| Abweichung | difference (Schema-Vergleich) / mismatch (C#-Modell ↔ DB) | |
| DDL-Vorschlag | DDL proposal | |
| Ausführungsplan (geschätzt / tatsächlich) | execution plan (estimated / actual) | |
| SQL-Editor / LINQ-Konsole | SQL editor / LINQ console | |
| Skript | script | |
| Verlauf | history | |
| C#-Modell | C# model | |
| verknüpftes Projekt (C#) | linked project | |
| Hilfsprozess | helper process | |
| Spalte ohne Property | column without property | |
| Entity / Property / Enum / Navigation | entity / property / enum / navigation | EF-Begriffe bleiben |
| Build-Ausgabe | build output | |
| PL/SQL: Spezifikation / Body / Quelltext / Parameter / Fehler | specification / body / source / parameters / errors | |
| ungültig (INVALID) | invalid | |
| Überladung / verschlüsselt (wrapped) / Richtung (IN/OUT) | overload / obfuscated (wrapped) / direction | PL/SQL |
| Schreiben (Knopf: ausstehende Änderungen in die Session schreiben) | Write | andere Texte nennen ihn „Write“ |
| Aktion (Schreib-Aktion von Grid/SQL/LINQ im Undo-Stapel) | action | „3 uncommitted actions“ |
| Committen (Knopf in Bestätigungen) | Commit | |
| freigeschaltet / FREIGESCHALTET / BEARBEITBAR (Badge) | unlocked / UNLOCKED / EDITABLE | READ-ONLY in beiden Sprachen |
| schreibende Transaktion | write transaction | Gegenstück zur Lesetransaktion eines gesperrten Workspaces |
| Stand (Lesestand eines gesperrten Workspaces) | snapshot; Fußzeile „As of 14:05:32“ | |
| Tx seit 5 min (Statusleiste) | Tx open 5 min | |
| Anwenden / Leeren (Filter) | Apply / Clear | |
| anheften / lösen (Spalte) | pin / unpin | |
| Verweist auf / Referenziert von (FK-Sprünge) | Points to / Referenced by | |
| Primärschlüssel / Fremdschlüssel / Row-Key | primary key / foreign key / row key | |
| Pflichtfeld / Eindeutigkeit verletzt | required value / unique constraint violated | Oracle-Fehler |
| Recht (Oracle) / Eigentümer | privilege / owner | |
| zum Löschen markiert | marked for deletion | |
| LOB-Editor / Vorschau | LOB editor / preview | |
| maskiert (Bind-Wert auf Prod) | masked | |
| Bind-Variable | bind variable | |
| Kommando (eines LINQ-Laufs) | command | |
| Modell-Abgleich / Abgleich mit der Datenbank | model mismatches / matching against the database | |
| zugeordnet (C#-Modell ↔ DB) | mapped | |
| kein Member (Enum) | no member | |
| Nachkommastellen / Stellen | decimal places / precision | |
| Schritt (Ladeschritt) | step, als „-ing“-Satz: „Building the model (OnModelCreating)“ | |
| Seite / Referenz / Ziel (Schema-Vergleich) | side / reference / target | |
| angleichen an (DDL-Vorschlag) | align to | |
| Schreibweise (nur Groß-/Kleinschreibung anders) | letter case | |
| Tastennamen Entf / Einfg / Pos1 / Ende / Bild↑ / Bild↓ / Leertaste / Rücktaste | Del / Ins / Home / End / PgUp / PgDn / Space / Backspace | Ctrl, Shift, Alt, Enter, Esc, Tab in beiden Sprachen gleich |
