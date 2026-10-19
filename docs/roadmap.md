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
Ausgewählt mit dem Nutzer am 2026-10-14 aus einer Ideensammlung (Bewertung des Nutzers je Paket in Klammern). Reihenfolge nach Abhängigkeiten: erst kleine Pakete mit sofortigem Nutzen (WP-37 … WP-39), dann zwei Fundamente, die je mehrere Pakete tragen – der Code-Index (WP-40 → WP-41, WP-45) und der Beziehungsgraph (WP-42 → WP-43, WP-46) –, das Paket mit Mitschnitt, Quellzeile und Datenänderungen (WP-45) erst, wenn seine Teile stehen. Die Abschnitte sind Vorschläge; „Offen“ wird beim Start mit dem Nutzer entschieden, dann wie üblich der Plan.

### WP-38 Befehlspalette, Schnellsuche, Spaltenprofil
(Befehlspalette, Spaltenprofil: „gut“.) Branch `wp/38-palette-quick-open-profile`.
- **Befehlspalette:** Suche über alle Aktionen aus `ShortcutMap.Definitions` (Wortsuche wie seit R5b überall), mit der Taste daneben (`ShortcutService.Label`); nicht verfügbare Aktionen ausgegraut. Nach der Regel aus `docs/architecture.md` 3 bekommt sie **keine Standardtaste** (Ctrl+Shift+P vergibt der Nutzer selbst).
- **Schnellsuche (Ctrl+P-Stil, aus dem Backlog):** Tabellen, Views und PL/SQL der sichtbaren Verbindung aus dem `SchemaCache`, dazu C#-Entity-Namen (`Kunde` findet `KUNDEN`); Enter öffnet den Tab. Ebenfalls ohne Standardtaste.
- **Spaltenprofil:** im Header-Menü (`ColumnMenu`) „Werte zählen“: häufigste Werte mit Anzahl (Top N), NULL-Anteil, Anzahl verschiedener Werte, Min/Max – unter dem aktiven Filter, in einer Abfrage aus dem `QueryBuilder` (`GROUP BY … ORDER BY COUNT(*) DESC FETCH FIRST n`), auf dem Leseweg der Workspace-Session (abbrechbar, Laufzeit sichtbar – große Tabellen über VPN). Werte über die Präsentationsschicht (Enum-Namen, Anzeigespalten). Klick auf einen Wert fügt eine Filterzeile `=` hinzu. Nicht für LOB/LONG.
- Offen: Top N (Vorschlag 20) und ob „Weitere laden“; Profil auch im Ergebnis von SQL-Editor/LINQ-Konsole (Vorschlag: nein, dort fehlt das Filtermodell); Gruppierung der Schnellsuche nach Präfix oder C#-Namespace (beides Backlog); Klick auf einen Wert mit Shift „als ODER zur letzten Gruppe“ (Filtergruppen seit WP-50).

### WP-39 Workspaces anheften und wechseln
(Aus dem Backlog, dort als ein Paket vorgeschlagen.) Branch `wp/39-workspace-pins`.
- Workspaces pinnen, Varianten aus dem Backlog: a) fester Platz vorn in der Leiste, b) Schutz vor versehentlichem Schließen/Löschen, c) beim Verbinden immer mit öffnen; d) über Verbindungen hinweg ist größer (ein Workspace gehört heute genau einer Verbindung) – Vorschlag a–c, d bleibt im Backlog.
- Workspace-Übersicht je Verbindung (Tabs, offene Transaktion, zuletzt benutzt, Notizen) und Wechseln per Tastenkürzel.
- Offen: welche der Varianten a–c; Ctrl+1 … 9 ist im Backlog für das Wechseln reserviert, aber neue Aktionen bekommen keine Standardtaste (`docs/architecture.md` 3) – Ausnahme dafür oder vom Nutzer belegen lassen; Oberfläche für `Workspace.Notes` mit in dieses Paket?

### WP-40 Verwendung im Code
(Fundament für WP-41 und WP-45.) Branch `wp/40-code-usage`.
- **Ziel:** Ansicht „Verwendung“ im Tabellen-Tab: je Spalte, welche Methoden der Solution die Property lesen oder schreiben und welche Queries die Tabelle anfassen, mit Datei und Zeile. Daraus: Markierung „im Code ungenutzt“ in der Spalten-Ansicht und ein Bericht der ungenutzten gemappten Properties; im Designer stehen beim Löschen einer Spalte ihre Verwendungen in der Bestätigung.
- **Technik:** die Build-Ausgaben der Projekte der Solution (nicht nur des DbContext-Projekts) per `System.Reflection.Metadata` lesen – Aufrufe der Getter/Setter von Entity-Properties (`call`/`callvirt`, in Expression Trees `ldtoken` auf den Getter, damit LINQ mitzählt), Zugriffe auf `DbSet<T>` – und über die portablen PDBs (Sequence Points) auf Datei und Zeile abbilden. Läuft in .NET 10 im Core, ohne MSBuild, ohne Roslyn-Workspace und ohne den ModelHost (keine Grenze aus ADR 0011). Neu aufbauen nach einem Build über `BuildOutputWatcher`, Ergebnis je Assembly (MVID) cachen. ADR (zweite Quelle neben dem ModelHost).
- **Grenzen** (in der Ansicht nennen): Zugriffe per String (`EF.Property<T>(x, "Email")`), Reflection und rohes SQL findet der IL-Weg nicht; Projekte ohne PDB nur mit Methodennamen.
- Offen: welche Projekte eingelesen werden (alle der `.sln`/`.slnx` des verknüpften Projekts, Testprojekte ausnehmen?); Klick auf eine Fundstelle – Pfad kopieren oder im Editor öffnen (Visual Studio über `devenv /Edit` steht im Backlog); welcher Klon (der verknüpfte Branch-Klon der Verbindung liefert die Pfade aus seinen PDBs). XML-Doku als Spaltenbeschreibung (Backlog, „später“) könnte hier mitlaufen.

### WP-41 Designer-Änderungen ins C#-Projekt
Branch `wp/41-designer-to-code`.
- Nach einer Änderung im Tabellen-Designer (WP-23) die passende Änderung an Entity-Klasse und `IEntityTypeConfiguration` vorschlagen: Spalte hinzu → Property und Konfiguration im Stil des Projekts (`PropertyCode`, `EntityCode`), Länge/NULL geändert → Facetten anpassen, Spalte gelöscht → Property entfernen. Neue Tabelle → neue Dateien wie „Entity aus Tabelle“.
- Dateien finden über den Index aus WP-40 (Dokument der Property-Getter, Implementierungen von `IEntityTypeConfiguration<T>`). Änderung als Roslyn-Syntax-Umbau (nur Syntax, kein Kompilieren; aktuelles Roslyn im .NET-10-Prozess), Kodierung, BOM und Zeilenenden der Datei bleiben.
- Vorschau als Diff, Schreiben erst nach Bestätigung und nur, wenn die Datei seit der Analyse unverändert ist; kein Build, kein Commit. Nach dem Build des Nutzers lädt das Modell neu, der Abgleich (WP-27) zeigt, ob es passt.
- ADR: FerretSharp schreibt zum ersten Mal in das Repo des Nutzers.
- Offen: Ziel-Klon (der Arbeits-Klon, in dem entwickelt wird – braucht „Arbeits-Repositories“ in den Einstellungen, Idee aus dem Backlog-Eintrag zu Visual Studio – oder der verknüpfte Branch-Klon); Umfang der ersten Stufe (Vorschlag: hinzufügen, Facetten ändern, löschen; Umbenennen später).

### WP-42 Beziehungsgraph und Teilgraph kopieren
(Fundament für WP-43 und WP-46.) Branch `wp/42-subgraph-copy`.
- **Beziehungsgraph** im Core: Kanten aus deklarierten FKs und Navigations des C#-Modells, später auch `Manual`/`Convention` (`FkSource`), je Kante Spaltenpaare und Quelle.
- **Kopieren:** markierte Zeilen eines Tabs als Start; Eltern werden immer mitgenommen (sonst FK-Verletzung), Kinder je Kante wählbar mit begrenzter Tiefe. Vorschau als Baum mit Zeilenzahl je Tabelle. Lesen auf der Quelle nur über den Leseweg, in Blöcken mit `in` (Grenze 1000 Werte je Liste).
- **Ziel:** ein Workspace einer anderen offenen Verbindung; Einfügen in dessen Sandbox-Transaktion in FK-Reihenfolge (topologisch sortiert), als **eine** Aktion im Undo-Stapel (ADR 0018); Commit wie immer nur auf Befehl. Ziel Prod nur nach Freischalten; Quelle Prod ist erlaubt (nur lesend).
- **Konflikte:** gleicher Schlüssel im Ziel vorhanden → überspringen, aktualisieren oder abbrechen, in der Vorschau sichtbar. Spalten, die im Ziel fehlen oder abweichen, warnen (wie der Schema-Vergleich); Identity ALWAYS lässt sich nicht einfügen; Trigger im Ziel (z. B. Audit) laufen mit – Hinweis.
- ADR (Kopieren über Verbindungen hinweg).
- Offen: Standardtiefe für Kinder; Standard bei Konflikten; Spalten beim Kopieren maskieren (Namen, E-Mail) – in diesem Paket oder später; neue Schlüssel im Ziel (Sequenzen, Umschreiben der FKs) – Vorschlag: später; Zyklen im Graphen (Vorschlag: melden statt auflösen).

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

### WP-46 Beziehungsdiagramm
(braucht WP-42.) Branch `wp/46-relationship-diagram`.
- Nachbarschaft einer Tabelle über 1–2 Ebenen als Diagramm, Kanten nach `FkSource` gefärbt (deklariert, C#-Modell, später manuell/vermutet), Knoten mit Tabellen- und Entity-Namen; Klick öffnet den Tab, Doppelklick erweitert den Knoten. „Als Mermaid kopieren“ für Doku und Tickets.
- Offen: Platz (Ansicht im Tabellen-Tab oder eigener Tab); Layout – SVG aus Razor mit einem einfachen Schichten-Layout im Core (keine neue Bibliothek, keine Logik in JS) oder eine Layout-Bibliothek wie elkjs lokal unter `wwwroot/lib` (dann ADR zum Stack).

### WP-47 Startseite „Seit deinem letzten Besuch“ mit Schema-Cache
Branch `wp/47-since-last-visit`.
- **Voraussetzung, aus dem Backlog („Reaktionszeit über das VPN“, Punkt 1):** letzten Schema-Stand je Verbindung speichern, den Explorer sofort daraus füllen und im Hintergrund abgleichen (stale-while-revalidate, sichtbares „Stand von …“). Vorher messen, wie dort vorgeschlagen.
- **Startseite nach dem Verbinden:** Unterschiede zum letzten Stand über den Schema-Vergleich (WP-20, zwei Snapshots derselben Verbindung) – neue/entfernte Tabellen und Spalten, geänderte Typen, neu ungültige Objekte –, neue Abweichungen zum C#-Modell (WP-27), offene Transaktionen anderer Workspaces, zuletzt benutzte Workspaces. Der Release-Check (Backlog, „später“) käme dort als weiterer Block dazu.
- Offen: eigene Seite oder Hinweis „3 Änderungen seit …“, der die Seite öffnet; was vor dem Abgleich erlaubt ist (Backlog-Eintrag).

### Pakete aus der Ideensammlung 2026-10-17 (WP-52 … WP-54)
Vom Nutzer aus einer Sammlung von Oracle-Funktionen und Bibliotheken als Pakete übernommen (2026-10-17). Alle klein und unabhängig von WP-38 … WP-47. WP-52 (Warten und Fortschritt laufender Statements) ist vorgezogen und in 3.39.0 released: `docs/work-packages/wp-52-waits-and-progress.md`; WP-53 (Export als Excel-Datei) ebenso in 3.40.0: `docs/work-packages/wp-53-xlsx-export.md`; WP-54 (alle Zeilen des Filters exportieren, mit A13) ist umgesetzt: `docs/work-packages/wp-54-export-all-rows.md`.

## Offene UX-Fragen
- Workspaces: Standardname ist „Workspace N“ mit der kleinsten freien Nummer (nach Umbenennen von „Workspace 1“ heißt der nächste wieder „Workspace 1“) – so lassen oder fortlaufend zählen?
