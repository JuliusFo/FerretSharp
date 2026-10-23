# Roadmap – geplante Pakete und offene Fragen

Was als Nächstes kommt, mit den Entscheidungen des Nutzers. Beim Start eines Pakets den Abschnitt lesen und den Plan mit dem Nutzer abstimmen. Ist das Paket released, wandert sein Abschnitt als „Planung“ in die Paketdatei unter `docs/work-packages/` (dort auch alle abgeschlossenen Pakete). Nicht eingeplante Ideen: `docs/backlog.md`.

## GitHub-Issues und Labels

Entscheidung des Nutzers (2026-10-09). Issues bekommen eine Art und einen Status.
- **Art:** `enhancement`, `bug`, `documentation`, `question`. Optional der Bereich: `area: grid`, `area: sql-editor`, `area: linq` oder `area: schema`.
- **Status**, je Issue genau einer:
  - `needs decision`: Das Issue hat offene Fragen. Die Antworten des Nutzers kommen beim Start des Pakets hierher in den Abschnitt des Pakets, danach wird das Label entfernt.
  - `planned`: Das Issue ist als Paket eingeplant. In der Überschrift des Pakets hier steht der Link zum Issue.
  - `done`: Das Issue ist umgesetzt und released. `planned` wird entfernt, dazu kommt ein Abschlusskommentar mit Version, Entscheidungen und dem, was offen bleibt (Backlog). Dann wird das Issue geschlossen.
- Labels setzen dürfen nur Leute mit Triage-Rechten. Wer ein Issue anlegt, setzt in der Regel keine Labels; das geschieht beim Einplanen.

## Geplant

Versionen: Minor-Releases 3.x (nichts Inkompatibles). Ein Paket bekommt seine Version erst beim Release (`CHANGELOG.md`, Spalte „Release“ im Index); die Roadmap legt keine fest, weil die Reihenfolge oft abweicht und Fix-Releases dazwischenkommen (Entscheidung des Nutzers, 2026-10-14). Der Release-Stand steht im Kopf der `CLAUDE.md`, die Historie im `CHANGELOG.md`.

### Pakete nach WP-36 (WP-37 … WP-47)
Ausgewählt mit dem Nutzer am 2026-10-14 aus einer Ideensammlung (Bewertung des Nutzers je Paket in Klammern). Reihenfolge nach Abhängigkeiten: erst kleine Pakete mit sofortigem Nutzen (WP-37 … WP-39), dann zwei Fundamente, die je mehrere Pakete tragen – der Code-Index (WP-40 → WP-41, WP-45) und der Beziehungsgraph (WP-42 → WP-43, WP-46) –, das Paket mit Mitschnitt, Quellzeile und Datenänderungen (WP-45) erst, wenn seine Teile stehen. Am 2026-10-22 hat der Nutzer das Beziehungsdiagramm (WP-46) direkt hinter WP-42 gezogen, vor WP-43, und um ein Diagramm konkreter Datensätze erweitert (WP-60, danach). Die Abschnitte sind Vorschläge; „Offen“ wird beim Start mit dem Nutzer entschieden, dann wie üblich der Plan.

### WP-55 Spaltenprofil
(„gut“; aus WP-38 herausgelöst, 2026-10-19 – gleicher Platz in der Reihenfolge. WP-38 ist umgesetzt: `docs/work-packages/wp-38-palette-quick-open.md`.) Branch `wp/55-column-profile`.
- **Spaltenprofil:** im Header-Menü (`ColumnMenu`) „Werte zählen“: häufigste Werte mit Anzahl (Top N), NULL-Anteil, Anzahl verschiedener Werte, Min/Max – unter dem aktiven Filter, in einer Abfrage aus dem `QueryBuilder` (`GROUP BY … ORDER BY COUNT(*) DESC FETCH FIRST n`), auf dem Leseweg der Workspace-Session (abbrechbar, Laufzeit sichtbar – große Tabellen über VPN). Werte über die Präsentationsschicht (Enum-Namen, Anzeigespalten). Klick auf einen Wert fügt eine Filterzeile `=` hinzu. Nicht für LOB/LONG.
- Offen: Top N (Vorschlag 20) und ob „Weitere laden“; Profil auch im Ergebnis von SQL-Editor/LINQ-Konsole (Vorschlag: nein, dort fehlt das Filtermodell); Klick auf einen Wert mit Shift „als ODER zur letzten Gruppe“ (Filtergruppen seit WP-50).

### WP-39 Workspaces anheften und wechseln
(Aus dem Backlog, dort als ein Paket vorgeschlagen.) Branch `wp/39-workspace-pins`.
- Workspaces pinnen, Varianten aus dem Backlog: a) fester Platz vorn in der Leiste, b) Schutz vor versehentlichem Schließen/Löschen, c) beim Verbinden immer mit öffnen; d) über Verbindungen hinweg ist größer (ein Workspace gehört heute genau einer Verbindung) – Vorschlag a–c, d bleibt im Backlog.
- Workspace-Übersicht je Verbindung (Tabs, offene Transaktion, zuletzt benutzt, Notizen) und Wechseln per Tastenkürzel.
- Offen: welche der Varianten a–c; Ctrl+1 … 9 ist im Backlog für das Wechseln reserviert, aber neue Aktionen bekommen keine Standardtaste (`docs/architecture.md` 3) – Ausnahme dafür oder vom Nutzer belegen lassen; Oberfläche für `Workspace.Notes` mit in dieses Paket?

### WP-41 Designer-Änderungen ins C#-Projekt
Branch `wp/41-designer-to-code`. Baut auf WP-40 (released 3.43.0, ADR 0030): dessen Index der Verwendungen und der aktive Arbeits-Klon je Gruppe tragen WP-41 und WP-45.
- Nach einer Änderung im Tabellen-Designer (WP-23) die passende Änderung an Entity-Klasse und `IEntityTypeConfiguration` vorschlagen: Spalte hinzu → Property und Konfiguration im Stil des Projekts (`PropertyCode`, `EntityCode`), Länge/NULL geändert → Facetten anpassen, Spalte gelöscht → Property entfernen. Neue Tabelle → neue Dateien wie „Entity aus Tabelle“.
- Dateien finden über den Index aus WP-40 (Dokument der Property-Getter, Implementierungen von `IEntityTypeConfiguration<T>`). Änderung als Roslyn-Syntax-Umbau (nur Syntax, kein Kompilieren; aktuelles Roslyn im .NET-10-Prozess), Kodierung, BOM und Zeilenenden der Datei bleiben.
- Vorschau als Diff, Schreiben erst nach Bestätigung und nur, wenn die Datei seit der Analyse unverändert ist; kein Build, kein Commit. Nach dem Build des Nutzers lädt das Modell neu, der Abgleich (WP-27) zeigt, ob es passt.
- ADR: FerretSharp schreibt zum ersten Mal in das Repo des Nutzers.
- **Ziel ist der aktive Arbeits-Klon der Gruppe (WP-40, `WorkClones`), nie der verknüpfte Branch-Klon** (der muss dem Stand der DB entsprechen und wird gepullt). Die Dateien des Arbeits-Klons werden selbst untersucht – sein Feature-Branch kann vom Branch-Klon abweichen, der Index aus WP-40 ist nur ein Hinweis, wo zu suchen ist. Die Vorschau nennt Ordner und Branch des Ziels und bietet den Wechsel an; ohne passenden Arbeits-Klon wird nicht geschrieben. Warnung, wenn der Klon auf dem Branch einer Verbindung steht (z. B. `dev`) oder die Zieldatei ungespeicherte Änderungen hat.
- Offen: Umfang der ersten Stufe (Vorschlag: hinzufügen, Facetten ändern, löschen; Umbenennen später); woran „Branch einer Verbindung“ erkannt wird (Branches der verknüpften Klone oder Liste von Hand).

### WP-42 Beziehungsgraph und Teilgraph kopieren
Umgesetzt auf `wp/42-subgraph-copy` (Release offen, E2E steht noch aus): Planung, Entscheidungen und Stand in `docs/work-packages/wp-42-subgraph-copy.md`, ADR 0032. Für WP-46/WP-60 da: `RelationGraph` und `RowTraversal.FollowAsync` (Core/Relations).

### WP-46 Beziehungsdiagramm
Umgesetzt auf `wp/46-relationship-diagram` (Release offen, kommt mit WP-42): Planung, Entscheidungen und Stand in `docs/work-packages/wp-46-relationship-diagram.md`, ADR 0033. Für WP-60 da: `DiagramLayout` (Schichten-Layout über Knoten-IDs, stabil beim Wachsen) und die Darstellung in `RelationsView`.

### WP-60 Datensatz-Diagramm
(Idee des Nutzers, 2026-10-22; braucht WP-42 und WP-46.) Branch `wp/60-record-diagram`.
- Von markierten Zeilen eines Tabs aus die verbundenen Datensätze als Diagramm: Knoten sind Zeilen (Tabellen-/Entity-Name, Schlüssel, Anzeigespalte über die Präsentationsschicht), Kanten die Beziehungen aus dem Graphen von WP-42, die diese Zeilen tatsächlich verbinden. Doppelklick lädt Eltern oder Kinder einer Kante nach, Klick öffnet den Tab mit Filter auf die Zeile.
- Laden über die Traversierung aus WP-42 (Leseweg der Workspace-Session, Blöcke mit `in`, abbrechbar), Darstellung und Layout aus WP-46. Viele Kinder an einer Kante werden zu einem Knoten zusammengefasst („Position ×200“), aufklappbar. Werte nur im Speicher.
- **Verbindung zum Kopieren (WP-42):** Knoten markieren und „Teilgraph kopieren“ mit diesen Zeilen als Start; die Kopier-Vorschau von WP-42 als Diagramm mit Zeilenzahl und Konflikten am Knoten.
- Offen: Grenze, ab der zusammengefasst wird; ob das Diagramm die Tabellen-Vorschau von WP-42 ersetzt oder ergänzt; Start auch aus einem Ergebnis von SQL-Editor/LINQ-Konsole, wenn die Herkunft bekannt ist (WP-48); „Als Mermaid kopieren“ auch hier (enthält Werte).

### WP-43 Datenqualität der Beziehungen
Branch `wp/43-relationship-check`.
- Bericht je Verbindung über den Graphen aus WP-42: für jede Beziehung **ohne** FK-Constraint die Zahl verwaister Werte (`NOT EXISTS`), mit Beispielwerten; Enum-Spalten mit Werten, die das C#-Enum nicht kennt; deklarierte Constraints im Zustand DISABLED/NOVALIDATE mit verletzenden Zeilen.
- Je Zeile: „Zeilen zeigen“ – das Filtermodell kennt kein `NOT EXISTS`, deshalb ein Tab mit `in`-Filter auf die verwaisten Werte (bis zu einer Grenze) oder die Abfrage im SQL-Editor; „Constraint anlegen“ bei 0 Verwaisten und PK/UK auf der Gegenseite, über den DDL-Baustein aus WP-23 (mit Index für den FK).
- Kosten: volle Scans großer Tabellen über das VPN – Auswahl der Schemas/Beziehungen, je Beziehung abbrechbar, Laufzeit sichtbar, eigene Lese-Session (die Explorer-Session arbeitet einen Aufruf nach dem anderen ab). Auf Prod erlaubt (nur lesend).
- Offen: Ergebnis speichern, um Läufe zu vergleichen; Platz in der Oberfläche (Seite oben rechts wie der Schema-Vergleich oder Ansicht je Verbindung).

### WP-44 Daten-Diff
Branch `wp/44-data-diff`. Zwei Stufen, die dritte kommt mit WP-45.
- **Stufe 1, Snapshot:** den Filter eines Tabs festhalten (Rohwerte, Grenze z. B. 50 000 Zeilen), später „Vergleichen“: neue, gelöschte und geänderte Zeilen über den Schlüssel, geänderte Zellen markiert (`RowComparison`). Funktioniert überall, sieht nur, was im Filter liegt.
- **Stufe 2, Flashback (ganze Tabellen, ohne Snapshot vorher):** Startpunkt per `SELECT TIMESTAMP_TO_SCN(SYSTIMESTAMP) FROM dual` merken, später `tabelle AS OF SCN :start` gegen den aktuellen Stand über den Schlüssel vergleichen – reine Abfragen auf dem Leseweg (`AS OF` entsteht im `QueryBuilder`). Verglichen wird der committete Stand auf einer Session ohne eigene Änderungen. Grenzen: Recht `FLASHBACK` auf die Tabelle (oder Owner), Undo muss reichen (ORA-01555/ORA-08180 verständlich melden), DDL dazwischen bricht den Vergleich, LOBs nur über Länge.
- Snapshot und Ergebnis nur im Speicher (Werte, auch von Prod).
- Offen: Grenze für Stufe 1; Auswahl der Tabellen für Stufe 2 bis WP-45 (Vorschlag: Liste von Hand); Darstellung (Grid mit Status-Spalte wie die Änderungsübersicht?).

### WP-45 Mitschnitt mit Quellzeile und Datenänderungen
(braucht WP-40 und WP-44.) Branch `wp/45-recording-source-data`. Fortsetzung von WP-33 (ADR 0023).
- **Quellzeile je Statement:** EventPipe liefert zu Ereignissen den Aufrufstapel; der erste Rahmen außerhalb von EF Core/`System`/`Microsoft` ist die Methode des Nutzers, über den Index aus WP-40 mit Datei und Zeile. Aus „18× Lazy Loading `Auftrag.Positionen`“ wird „… aus `AuftragService.GetOffene`, `AuftragService.cs:142`“, ohne Code-Änderung und ohne `TagWithCallSite`. **Spike zuerst:** Stapel in Echtzeit inklusive Methoden-Rundown, Kosten für die App.
- **Datenänderungen (Daten-Diff Stufe 3):** „Aufzeichnen“ merkt die SCN der zugeordneten Verbindung; die Zieltabellen der INSERT/UPDATE/DELETE/MERGE-Statements des Mitschnitts bestimmen, welche Tabellen „Stopp“ per Flashback (WP-44) vergleicht. Ergebnis: alle Änderungen der App in allen betroffenen Tabellen, ohne vorher etwas auszuwählen. Audit-Tabellen (WP-26) auf Wunsch mit, weil ihre Trigger nicht im Mitschnitt stehen.
- Feldliste bleibt fest (ADR 0023): aus dem Stapel nur Methodennamen, nie Werte – Nachtrag zum ADR.
- Offen: Ergebnis des Spikes; ob die Quellzeile nur für die Top-Statements ermittelt wird (Kosten).

### WP-47 Startseite „Seit deinem letzten Besuch“ mit Schema-Cache
Branch `wp/47-since-last-visit`.
- **Voraussetzung, aus dem Backlog („Reaktionszeit über das VPN“, Punkt 1):** letzten Schema-Stand je Verbindung speichern, den Explorer sofort daraus füllen und im Hintergrund abgleichen (stale-while-revalidate, sichtbares „Stand von …“). Vorher messen, wie dort vorgeschlagen.
- **Startseite nach dem Verbinden:** Unterschiede zum letzten Stand über den Schema-Vergleich (WP-20, zwei Snapshots derselben Verbindung) – neue/entfernte Tabellen und Spalten, geänderte Typen, neu ungültige Objekte –, neue Abweichungen zum C#-Modell (WP-27), offene Transaktionen anderer Workspaces, zuletzt benutzte Workspaces. Der Release-Check (Backlog, „später“) käme dort als weiterer Block dazu.
- Offen: eigene Seite oder Hinweis „3 Änderungen seit …“, der die Seite öffnet; was vor dem Abgleich erlaubt ist (Backlog-Eintrag).

## Offene UX-Fragen
- Workspaces: Standardname ist „Workspace N“ mit der kleinsten freien Nummer (nach Umbenennen von „Workspace 1“ heißt der nächste wieder „Workspace 1“) – so lassen oder fortlaufend zählen?
