# ADR 0042: Berichtsseite C#-Modell ↔ Datenbank und Datei der Ausnahmen

- Status: akzeptiert
- Datum: 2026-11-03
- Paket: WP-72 (`docs/work-packages/wp-72-model-report.md`)

## Kontext

Was C#-Modell und Datenbank trennt, stand verstreut: der Abgleich (WP-11/27) als lange Listen auf der Model-Seite – bei einem Arbeits-Repo weit über 1000 Zeilen, die niemand liest –, die ungenutzten Spalten (WP-40) darunter, die Beziehungen nur im Modell im Graphen (WP-42) ohne Übersicht. Tabellen ohne Entity fehlten ganz: Der Abgleich geht von den Entities aus. Der Nutzer wollte eine Seite, die auf einen Blick zeigt, wie gut Repo und Datenbank zusammenpassen, gut sortiert und aufklappbar, in der Leiste oben (Idee und Entscheidungen 2026-11-03, Spike abgenommen).

## Entscheidung

- **Eine Seite „Bericht“ oben rechts** (`ShellPage.Report`, Icon Lucide `clipboard-check`), neben Schema-Vergleich und Einstellungen, für die gezeigte Verbindung; ein Umschalter im Kopf zeigt eine andere verbundene (`ConnectionHub.Show`, die Seiten folgen ihr). Ohne verbundene Verbindung ein Leerzustand, ohne verknüpftes Modell der Hinweis zum Verknüpfen.
- **Nur, was geladen ist** (`ModelReport.Build`, reine Klasse im Core): die Abbildung des Modells mit den Spalten, die sie ohnehin liest (`ClrModelMapping.ColumnsOf`, neu behalten), Tabellen, deklarierte Schlüssel und Audit-Tabellen des Schema-Caches, der Code-Index, wenn er eingelesen ist, und die Ausnahmen. Keine Abfrage, die Seite ist sofort da; die Zeile unter dem Kopf nennt den Stand jeder Quelle. Daraus folgt: keine Spaltenzahl und keine Zeilen laut Statistik bei Tabellen ohne Entity, kein Hinweis „kein Index“ und nur der Primärschlüssel als Gegenseite eines Constraints (Unique Keys und Indizes werden je Tabelle gelesen, nicht für alle). Teure Prüfungen (WP-43) kommen später als eigener Bereich mit eigenem „Prüfen“.
- **Bereiche:** Tabellen ohne Entity (Schemas der Verbindung und aller, in denen das Modell Tabellen hat; Views zuschaltbar), Spalten ohne Property (nach Tabelle gruppiert, NOT NULL ohne Default markiert), Modell ohne Gegenstück, abweichende Spalten (WP-27), Beziehungen nur im Modell bzw. nur in der Datenbank (Vergleich über Tabellen und Spaltenpaare wie `SchemaCache.CombineForeignKeys`), ungenutzte Spalten (WP-40, eingelesen auf Anforderung). Aktionen nutzen vorhandene Wege: „Entity erzeugen“ öffnet „Entity aus Tabelle“ (`TableTab.OpenEntityCode`), „Constraint anlegen“ den Designer wie die Constraints-Ansicht, „Property-Code kopieren“ und „Navigation kopieren“ (`EntityCode.Navigation`) gehen nur in die Zwischenablage – in Dateien schreibt erst WP-41.
- **Die Model-Seite verliert ihre Listen** an den Bericht (Entscheidung des Nutzers) und behält Projekt bzw. Modelldatei, Laden, Bauen und Export mit einer Zeile Zahlen und „Bericht öffnen“.
- **Aufgeklappte Abschnitte** gelten für die Sitzung (`ReportView`, statisch), nicht gespeichert (Entscheidung des Nutzers).
- **Bewusste Ausnahmen in einer eigenen Datei** (`report-exceptions.json`, `ReportExceptionSource`), nicht in der der Anzeigespalten, die die Anzeige im Grid beschreibt (Entscheidung des Nutzers): Version und je Eintrag eine Zeile `"Tabelle oder Muster": "Begründung"` unter `tablesWithoutEntity`, sortiert für Git; Schlüssel ohne Owner wie bei den Anzeigespalten (ADR 0022), Groß-/Kleinschreibung egal, `*` als Platzhalter (`AUD_*`, `*_BAK`). Unbekannte Teile bleiben beim Schreiben (`JsonExtensionData`), so ist das Format offen für „Spalte bewusst ohne Property“ und „Beziehung bewusst ohne Constraint“. Ungültiges JSON wird gemeldet und nie überschrieben, eine neuere Version nur gelesen. Pfad wie die Audit-Vorlage: global (`AppSettings.ReportExceptionsPath`), je Verbindungsgruppe (`ReportExceptionsGroups`, wandert beim Umbenennen der Gruppe mit), sonst im Datenordner. Audit-Tabellen nach der Vorlage der Verbindung blendet der Bericht von selbst aus; ausgeblendete sind gezählt und einblendbar.
- **Die Datei ist fremde Eingabe** (geteiltes Repo): Namen werden nur verglichen, nie Teil von SQL oder Pfaden; Muster werden ohne Backtracking abgeglichen (`ReportExceptions.Fits`, linear); Name und Begründung begrenzt (160 bzw. 500 Zeichen), die Datei wie alle über `CachedFileRead` auf 4 MB. Angezeigt nur über Blazor (escaped).

## Folgen

- **Abweichung von der Planung:** kein eigener Watcher für die Ausnahmedatei. Die Seite fragt beim Rendern über `CachedFileRead` (wie die Audit-Vorlage): Eine Änderung auf der Platte (git pull) zählt beim nächsten Rendern nach höchstens drei Sekunden; Watcher und Abfrage der Anzeigespalten brauchen deren Zustand für das Grid, der Bericht nicht.
- Der Bericht rechnet bei jeder Änderung der Abbildung, der Tabellenliste, der Beziehungen, der Audit-Vorlage oder der Ausnahmen neu – in Millisekunden, auch für 425 Entities.
- Verlauf, Markierung „neu seit …“ und eine Übersicht über alle Verbindungen stehen im Backlog.
