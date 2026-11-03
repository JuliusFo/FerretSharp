# WP-72 Berichtsseite Repo ↔ DB → Release offen

Branch `wp/72-model-report`, 2026-11-03, eigener Worktree. Idee des Nutzers: eine Seite, die wie ein Dashboard zeigt, wie gut das C#-Repo und die Datenbank zusammenpassen – die Listen der Model-Seite (bei einem Arbeits-Repo über 1000 Zeilen) gut sortiert und aufklappbar, in der Leiste oben. Entscheidung und Begründung: ADR 0042. Bewusst nicht im Release nach WP-73: Der Nutzer veröffentlicht vorher selbst von `main`, gemergt wird danach.

## Planung (aus `docs/roadmap.md`)
(Idee des Nutzers, 2026-11-03.) Baut auf dem Abgleich (WP-27), der Verwendung im Code (WP-40) und dem Beziehungsgraphen (WP-42), alle released.
- **Seite „Bericht“ in der Leiste oben rechts:** zeigt auf einen Blick, wie gut das C#-Projekt und die DB zusammenpassen – je Bereich eine Zahl, aufklappbar zur Liste mit Sprung in die Tabelle bzw. Spalte. Nur aus dem, was schon geladen ist (Schema, Modell, Code-Index, Graph): keine eigenen Abfragen, sofort da, mit „Stand von …“ der Quellen.
- **Bereiche:** Tabellen ohne Entity (Auswahl der Schemas, Ausnahmen z. B. für Audit- und Hilfstabellen); Entities ohne Tabelle, Properties ohne Spalte, Spalten ohne Property, nur in anderer Schreibweise (`MappingIssueKind`); abweichende Spalten (WP-27); ungenutzte Spalten (WP-40); Beziehungen nur im Modell bzw. nur als FK in der DB („nur im Modell“ mit dem vorhandenen Weg „Constraint anlegen“).
- **Model-Seite entschlanken:** die Listen (Abgleich, Abweichungen, ungenutzt, Entity-Tabelle) ziehen auf die Berichtsseite um; die Model-Seite behält Projekt bzw. Modelldatei, Laden, Bauen und Export. Verweise auf die Abweichungen führen auf die Berichtsseite.
- **Gestaltung laut Spike** (Icon `clipboard-check` zwischen Schema-Vergleich und Einstellungen; Kopf mit Verbindung als Umschalter, Projekt und Branch, „Neu einlesen“, „Model-Seite“, „Zurück“; Zeile mit dem Stand der Quellen; Kacheln in vier Gruppen; aufklappbare Abschnitte mit Suche, Chips, ersten Einträgen und „alle zeigen“; Spalten ohne Property nach Tabelle gruppiert; Beziehungen zweigeteilt).
- **Bewusste Ausnahmen:** eigene Datei mit der Mechanik der Anzeigespalten (ADR 0022), Tabellenname ohne Owner, Muster und Begründung, Pfad wie die Audit-Vorlage (global, je Gruppe), ohne Pfad im Datenordner; erst nur „Tabelle bewusst ohne Entity“, Format offen für mehr. Gesetzt per Klick, ausgeblendete zählbar und einblendbar.
- WP-43 kommt später als Bereich „Datenqualität“ mit eigenem „Prüfen“.

## Entscheidungen des Nutzers (2026-11-03)
1. Passt zu WP-43? Ja, als späterer Bereich der Seite; WP-72 braucht WP-43 nicht.
2. Die Model-Seite verliert ihre Listen („ich glaube, aktuell schaut sich keiner diese ewig lange Liste an“).
3. Bezug: die aktive Verbindung, oben umschaltbar; nur der aktuelle Stand. Verlauf und Übersicht über alle Verbindungen in den Backlog.
4. Ausnahmen in einer eigenen Datei, nicht in der der Anzeigespalten.
5. Spike so abgenommen. Plan mit drei Vorschlägen freigegeben („Ja, umsetzen“): Views bei „Tabellen ohne Entity“ per Chip zuschaltbar, kein Platzhalter „Datenqualität“ bis WP-43, aufgeklappte Abschnitte nur für die Sitzung.

## Gebaut
- **Core** (`Core/ClrModel/Report/`): `ModelReport.Build` (Tabellen ohne Entity mit ein- und ausgehenden deklarierten FKs, Ausnahme und Audit-Basis; Abdeckung `TablesInScope`/`TablesMapped`; `ColumnWithoutProperty` mit `RequiredWithoutDefault`; Modell ohne Gegenstück; Abweichungen; `ModelOnlyRelation` mit `ConstraintPossible`; `DbOnlyRelation` mit Grund und `HasProperties`), `ModelReport.Unused` mit `CodeUsageIndex.IsUsedInTests`. `ClrModelMapping.ColumnsOf` behält die beim Abgleich gelesenen Spalten. `SchemaCache.PairsOf` ist `internal` (derselbe Vergleich wie beim Zusammenführen der Quellen). `EntityCode.Navigation` (Navigation und `HasOne…HasForeignKey` als Schnipsel, aus `Write` herausgelöst; fehlende FK-Property als Kommentar). `ReportExceptions` (Muster ohne Backtracking, Grenzen für Name und Begründung) und `ReportExceptionSource` (Gruppe → global → Datenordner, lesen über `CachedFileRead`, Ändern liest neu und schreibt atomar). `AppSettings.ReportExceptionsPath`/`ReportExceptionsGroups`, `AppPaths.ReportExceptionsFile`; `ConnectionGroupRename` nimmt die Datei der Gruppe mit (gemeinsames `Move` für Audit-Vorlage und Ausnahmen, Warnung im Dialog, wenn eine entfällt).
- **UI:** `ShellPage.Report`, `ShellState.ShowReport(part)`/`TakeReportPart`, Knopf in der Leiste; `Shell` merkt sich die Seite vor einer Seite oben rechts auch bei Links (Zurück aus dem Bericht führt zur Model-Seite, wenn man von dort kam). `ReportPage` mit Kopf, Quellenzeile, Kacheln und `ReportSection`; Abschnitte `ReportTables`, `ReportColumns`, `ReportRelations`, `ReportMismatches` (aus `ModelMismatches`), `ReportModelIssues`, `ReportEntities` (die Entity-Tabelle der Model-Seite, aus dem Review), `ReportUnused` (aus `UnusedReport`), `ReportMore`, `ReportExceptionDialog`. Reine State-Klasse `ReportView` (Suche, Chips, Gruppen, erste 25, offene Abschnitte statisch). `TableTab.OpenEntityCode` → `ColumnsView` öffnet „Entity aus Tabelle“. Einstellungen › C#-Modell: `ReportExceptionSettings` (global und je Gruppe, über `AuditGroupTemplates`). Model-Seite: Zahlen und „Bericht öffnen“; die LINQ-Konsole springt nach ORA-00904 zu „Modell ohne Gegenstück“. CSS `.report-*`, `.model-issues` entfernt.

## Entscheidungen in der Umsetzung
- **Kein Watcher für die Ausnahmedatei** (Planung: „Mechanik wie die Anzeigespalten, Watcher“): Der Bericht liest beim Rendern über `CachedFileRead` wie die Audit-Vorlage und schaut, solange er offen ist, alle 5 s außerhalb des UI-Threads nach. ADR 0042.
- **Abweichungen vom Spike** (folgen aus „keine eigenen Abfragen“): keine Spaltenzahl und keine Zeilen laut Statistik bei Tabellen ohne Entity; kein Hinweis „kein Index“ und nur der PK als Gegenseite eines Constraints (UKs und Indizes sind nicht global geladen; der Chip heißt „nur mit PK auf der Gegenseite“). Die Abweichungen haben drei Stufen (Fehler, Warnungen, Hinweise in der Warnungen-Kachel); „nur in Tests“ ist ein „davon“ der ungenutzten. Statt des Chips „mit Zeilen“ (braucht die Statistik) gibt es „mit FK zu gemappter Tabelle“.
- **„Nur in der DB“ nur für FKs gemappter Tabellen**: Ein FK einer Tabelle ohne Entity steht schon an deren Zeile, einer zu einer Tabelle, die als Ausnahme oder Audit-Tabelle bewusst kein Entity hat, ist erwartet.
- **Abdeckung zählt nur Tabellen**, ohne ausgeblendete; Views werden gelistet (Chip), zählen aber nicht.
- **Die Statusleiste bleibt bei der Model-Seite** (Entscheidung des Nutzers im Review: „Ich finde es sollte immer zur Modell-Seite gehen.“): Ihr Eintrag zeigt vor allem Laden und Build des Modells; die Model-Seite führt mit „Bericht öffnen“ weiter.
- **Navigation-Name:** der der Hauptentität, außer er ist schon vergeben (auch als Klassenname bei einem FK auf dieselbe Tabelle), das Modell hat schon eine Navigation zur selben Tabelle oder die Tabelle deklariert einen weiteren FK zur selben Entity – dann nach der FK-Property wie in „Entity aus Tabelle“.

## E2E (2026-11-03, eigener Container `ferret-wp72`, Testinstanz mit `--data-dir`, Beispielmodell aus `samples/`)
- Geprüft mit echter Windows-Eingabe: Knopf in der Leiste (ohne Verbindung Leerzustand), Kacheln springen zum Abschnitt und klappen ihn auf, Abschnitte und Tabellengruppen auf- und zuklappen, „Property-Code kopieren“ (Zwischenablage), „Als Ausnahme…“ mit Muster und Begründung (Datei geschrieben, Zähler), einblenden und „Ausnahme entfernen“, „Entity erzeugen“ (Spalten mit „Entity aus Tabelle“), „Constraint anlegen“ (Designer mit FK, nicht ausgeführt), Model-Seite mit „Bericht öffnen“ und „Zurück“ dorthin, Einstellungen (Suche, Datei je Gruppe, danach nimmt der Bericht die Gruppendatei), hell und dunkel.
- Gefunden und behoben: Eine von außen geänderte Ausnahmedatei erschien erst beim nächsten Rendern der ganzen Seite – ein Klick in einem Abschnitt rendert nur diesen (jetzt Abfrage alle 5 s); Namen in der Abweichungstabelle brachen mitten im Wort (`overflow-wrap: break-word` statt `anywhere`); Tags klebten am Tabellennamen; „öffnen“ klein und „1 könnten Constraint werden“.
- Nicht geprüft: Umschalter für mehrere verbundene Verbindungen (eine Verbindung in der Testinstanz; `Hub.Show` wie in der Statusleiste), „Navigation kopieren“ (die Beispiel-DB hat keinen FK zwischen zwei gemappten Tabellen ohne Beziehung im Modell; Text über `ModelReportTests`), der Link aus der LINQ-Konsole (über `ReportViewTests`).

## Nachtrag: Code-Review (2026-11-03)
Unabhängiger Agent über den Branch, zwei Durchgänge, alle Befunde nachgeprüft. Entscheidungen des Nutzers: Frage A (Entity-Tabelle) „Ja, nimm das noch mit rein.“, Frage B (Statusleiste) „Ich finde es sollte immer zur Modell-Seite gehen.“, „Den Rest kannst du so beheben wie Vorgeschlagen.“; zum zweiten Durchgang „Ja, beheben“.
1. (M) Der Link der LINQ-Konsole nach ORA-00904 führte zu den abweichenden Spalten statt zu „Modell ohne Gegenstück“ – behoben, Knopf „Im Bericht zeigen“.
2. (M) „Navigation kopieren“ schrieb bei einem FK auf dieselbe Tabelle `Kategorie Kategorie` (CS0542) und gab zwei FKs zur selben Entity denselben Namen – behoben, Test `Navigation_code_avoids_the_class_name_and_a_second_foreign_key_to_the_same_entity`.
3. (M) Die Entity-Tabelle der Model-Seite fehlte im Bericht – nachgeholt als Abschnitt „Entities“ (Entscheidung A), Test in `ReportViewTests`.
4. (M, Sicherheit) Unbegrenzt viele Muster aus der geteilten Datei hätten den UI-Thread anhalten können – behoben: höchstens 2.000 Einträge, Muster mit höchstens 4 `*`, Ausgelassenes in der Quellenzeile; Test `Entries_beyond_the_limit_…`.
5. (M) Die Statusleiste führt zur Model-Seite statt zum Bericht – bleibt so (Entscheidung B), Kommentar angepasst.
6. (K) Eine Änderung schrieb die bereinigte Menge zurück und kürzte Einträge anderer – behoben: nur die eigene Zeile ändert sich, `maxlength` im Dialog; Test `Setting_an_exception_keeps_every_other_line_as_written`.
7. (K) „Property-Code kopieren“ ohne Namensprüfung und ohne Hinweise – behoben: `EntityCode.Property` (freier Name, Hinweise als Kommentare); Test `Property_code_gets_a_free_name_and_the_notes_as_comments`.
8. (K) Die 5-s-Abfrage konnte sich stapeln und behielt das Profil vom Start – behoben (Sperre, Profil je Durchlauf); ADR nennt den Blick auf die Datei beim Rendern.
9. (K) Umschalter wählte über `selected` – auf `value` umgestellt.
10. (K) Ein „alle zeigen“ klappte beide Beziehungslisten auf – je Liste ein Schlüssel, Test.
11. (K) Reste (`Model_Open`, `.model-table`, veraltete Kommentare, Knopftext) – entfernt bzw. angepasst.
12. (K) Festes Datumsformat – nach der Kultur.
13. (M, zweiter Durchgang) Umbenannter Property-Code verlor die Spalte: Mit Namenskonvention mappte `Auftrag2` still auf `AUFTRAG2` – behoben in `Rename` (auch „Entity aus Tabelle“, dort schon vorher so), Assert im Test.
14. (K, zweiter Durchgang) Der Dialog nahm Muster an, die der Bericht ignoriert; Begründung von `MaxStars` falsch; Text ohne Plural – behoben (`ReportExceptions.IsUsable`, Doku, Plural).
15. (K, zweiter Durchgang) `With`/`Without`/`Entry` ungenutzt – entfernt.
16. (K, zweiter Durchgang) Dieser Nachtrag fehlte.

Beim ersten Unit-Testlauf nach den Korrekturen des zweiten Durchgangs scheiterte ein Core-Test einmal (Name nicht festgehalten); vier weitere Läufe und der volle Lauf waren grün.
