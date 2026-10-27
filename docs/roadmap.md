# Roadmap – geplante Pakete und offene Fragen

Geplante Pakete mit den Entscheidungen des Nutzers. Eine Reihenfolge gibt es nicht: Welches Paket als Nächstes kommt, entscheidet der Nutzer jeweils selbst (Entscheidung des Nutzers, 2026-10-27); festgehalten werden nur Abhängigkeiten („braucht WP-NN“). Beim Start eines Pakets den Abschnitt lesen und den Plan mit dem Nutzer abstimmen. Ist das Paket released, wandert sein Abschnitt als „Planung“ in die Paketdatei unter `docs/work-packages/` (dort auch alle abgeschlossenen Pakete). Nicht eingeplante Ideen: `docs/backlog.md`.

## GitHub-Issues und Labels

Entscheidung des Nutzers (2026-10-09). Issues bekommen eine Art und einen Status.
- **Art:** `enhancement`, `bug`, `documentation`, `question`. Optional der Bereich: `area: grid`, `area: sql-editor`, `area: linq` oder `area: schema`.
- **Status**, je Issue genau einer:
  - `needs decision`: Das Issue hat offene Fragen. Die Antworten des Nutzers kommen beim Start des Pakets hierher in den Abschnitt des Pakets, danach wird das Label entfernt.
  - `planned`: Das Issue ist als Paket eingeplant. In der Überschrift des Pakets hier steht der Link zum Issue.
  - `done`: Das Issue ist umgesetzt und released. `planned` wird entfernt, dazu kommt ein Abschlusskommentar mit Version, Entscheidungen und dem, was offen bleibt (Backlog). Dann wird das Issue geschlossen.
- Labels setzen dürfen nur Leute mit Triage-Rechten. Wer ein Issue anlegt, setzt in der Regel keine Labels; das geschieht beim Einplanen.

## Geplant

Versionen: Minor-Releases 3.x (nichts Inkompatibles). Ein Paket bekommt seine Version erst beim Release (`CHANGELOG.md`, Spalte „Release“ im Index); die Roadmap legt keine fest, weil es keine feste Reihenfolge gibt und Fix-Releases dazwischenkommen (Entscheidung des Nutzers, 2026-10-14). Der Release-Stand steht im Kopf der `CLAUDE.md`, die Historie im `CHANGELOG.md`.

### Pakete nach WP-36 (WP-37 … WP-47)
Ausgewählt mit dem Nutzer am 2026-10-14 aus einer Ideensammlung (Bewertung des Nutzers je Paket in Klammern). Abhängigkeiten: WP-41 baut auf dem Code-Index (WP-40), WP-43 auf dem Beziehungsgraphen (WP-42) – beide released; WP-45 braucht WP-44. Die Abschnitte sind Vorschläge; „Offen“ wird beim Start mit dem Nutzer entschieden, dann wie üblich der Plan.

### WP-55 Spaltenprofil
(„gut“; aus WP-38 herausgelöst, 2026-10-19. WP-38 ist umgesetzt: `docs/work-packages/wp-38-palette-quick-open.md`.) Branch `wp/55-column-profile`.
- **Spaltenprofil:** im Header-Menü (`ColumnMenu`; seit WP-61 als fünfte Aktion der Leiste, `QuickAction` mit Beschriftung) „Werte zählen“: häufigste Werte mit Anzahl (Top N), NULL-Anteil, Anzahl verschiedener Werte, Min/Max – unter dem aktiven Filter, in einer Abfrage aus dem `QueryBuilder` (`GROUP BY … ORDER BY COUNT(*) DESC FETCH FIRST n`), auf dem Leseweg der Workspace-Session (abbrechbar, Laufzeit sichtbar – große Tabellen über VPN). Werte über die Präsentationsschicht (Enum-Namen, Anzeigespalten). Klick auf einen Wert fügt eine Filterzeile `=` hinzu. Nicht für LOB/LONG.
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

### WP-65 Zusammengesetzte Anzeigenamen
(Wunsch eines Kollegen, geplant mit dem Nutzer am 2026-10-27; Fortsetzung von WP-31, ADR 0022.) Branch `wp/65-composite-display-names`. Ein Name aus mehreren Spalten der Zieltabelle (`NACHNAME - VORNAME`, `NUMMER - BEZEICHNUNG`), in Stufe 2 auch über einen weiteren FK hinweg.
- **Stufe 1 – Spaltenliste je Tabelle:**
  - Modell (`Core/Display`): `DisplayColumns` hält je Tabelle eine geordnete Liste statt einer Spalte (`ColumnsOf`, `IndexesIn` – Einträge für Spalten, die die DB nicht hat, fallen weg; jede Spalte einzeln gegen `NotDisplayableReason`). `DisplayLink.DisplayColumn` wird zur Liste, `LookupRow.Display` zu den Werten je Spalte.
  - Abfrage: `QueryBuilder.BuildLookup` holt alle Spalten in einer Abfrage (`SELECT key, col1, col2 … WHERE key IN (…)`) – kein zusätzlicher Roundtrip; IN-Größen, Timeout, Cache je Tab und Workspace-Session bleiben wie in ADR 0022. Spaltennamen gegen das Schema geprüft und gequotet, nie freie SQL-Ausdrücke aus der geteilten Datei (sie kommt per `git pull` von anderen).
  - Zusammensetzen in C#, nicht in SQL (kein `||`): jeder Teil über die Präsentationsschicht (Datum, Zahl, Enum-Name wie im Grid), NULL- und leere Teile fallen weg, Trenner ` - `, gekürzt wird das Ganze (60 Zeichen, `DisplayNames.MaxTextLength`). Alle Namen holen sich ihren Text über `DisplayNames` – Grid, Formular, Export (WP-54, `RowExport`/`PresentedExport`) –, das Beschriften im Datensatz-Diagramm (`RecordDiagramView`, liest heute selbst `IndexIn`) zieht nach.
  - Datei (`DisplayColumnSource`): neues Feld `displayNames` (Tabelle → Liste), nur für Tabellen mit mehr als einer Spalte; `displayColumns` behält für diese Tabellen die erste Spalte, damit ältere Versionen wenigstens den ersten Teil zeigen. Gibt es beide, gilt `displayNames`. Die Datei bekommt Version 2, sobald sie `displayNames` enthält – ältere Versionen lesen sie dann nur und können nichts markieren, was der Liste widerspräche (bestehende Regel: neuere Version → nur lesen). Ohne `displayNames` bleibt Version 1.
  - UI: Kontextmenü des Spaltenkopfs (`ColumnMenu`) – „Als Anzeigespalte verwenden“ ersetzt wie heute die Liste durch diese eine Spalte, neu „Zum Namen hinzufügen“ hängt an (nur, wenn die Tabelle schon einen Namen hat) und „Aus dem Namen entfernen“ nimmt eine Spalte heraus. Badge mit Position („Name 1“, „Name 2“) im Grid-Kopf und in der Spaltenansicht (`ColumnsView`); ohne zweite Spalte bleibt es „Name“. Einstellungen › Anzeigespalten zählt Tabellen wie bisher.
- **Stufe 2 – Name über einen weiteren FK (höchstens eine Ebene):**
  - Ist eine Spalte des Namens selbst ein FK mit Anzeigename (geplant über `DisplayLinks` auf der Zieltabelle), steht an ihrer Stelle der Name ihres Ziels: Position → Auftrag `4711 · PetersPizza`, wenn der Name des Auftrags `KUNDE_ID` ist und der des Kunden `NAME`.
  - Kosten: ein zweiter Lookup je Block für die Werte der zweiten Ebene (gleiche Warteschlange, gleiches Timeout, eigener Cache-Eintrag); kein JOIN, damit ein fehlendes Recht auf die zweite Tabelle nur deren Teil abschaltet. Keine Rekursion über eine Ebene hinaus, also kein Zyklusproblem (auch nicht bei Selbstverweisen wie `PARENT_ID`).
  - Nicht gefunden auf der zweiten Ebene: der Rohwert statt des Namens.
- Tests: Core (`DisplayColumnsTests`/`DisplayColumnSourceTests` für Datei, Versionen und beide Felder, `DisplayLinksTests`, `DisplayNamesTests` für Zusammensetzen, NULL-Teile, Kürzen und zweite Ebene, `QueryBuilderTests` für den Lookup mit mehreren Spalten, `ReadOnlyTests`), Integration `DisplayLookupTests` (mehrere Spalten, zweite Ebene), E2E in eigener Instanz und eigenem Container wie WP-31.
- Doku: Nachtrag zu ADR 0022 (Dateiformat Version 2, Zusammensetzen in C#, zweite Ebene) statt eines neuen ADR; `docs/architecture.md` (Anzeigespalten), Texte Englisch/Deutsch.
- Entschieden mit dem Nutzer (2026-10-27):
  - Trenner ` - ` (Bindestrich mit Leerstellen; ` · ` trennt schon Wert und Name), fest für alle Tabellen – eine Vorlage wie `{NACHNAME}, {VORNAME}` erst bei Bedarf.
  - Reihenfolge ändern nur über Entfernen und neu Hinzufügen, kein eigener Dialog.
  - Stufe 2 im selben Paket, als zweiter Schritt: Stufe 1 ist für sich releasefähig.
  - Zweite Ebene zeigt nur den Namen des Ziels (`PetersPizza`), nicht Wert und Name.

## Offene UX-Fragen
- Workspaces: Standardname ist „Workspace N“ mit der kleinsten freien Nummer (nach Umbenennen von „Workspace 1“ heißt der nächste wieder „Workspace 1“) – so lassen oder fortlaufend zählen?
