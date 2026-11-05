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

Versionen: Minor-Releases 3.x (nichts Inkompatibles). Ein Paket bekommt seine Version erst beim Release (`CHANGELOG.md`, Spalte „Release“ im Index); die Roadmap legt keine fest, weil es keine feste Reihenfolge gibt und Fix-Releases dazwischenkommen (Entscheidung des Nutzers, 2026-10-14). Release-Stand und Historie: die Tags und das `CHANGELOG.md`.

### Pakete nach WP-36
Ausgewählt mit dem Nutzer am 2026-10-14 aus einer Ideensammlung (Bewertung des Nutzers je Paket in Klammern). Abhängigkeiten: WP-41 baut auf dem Code-Index (WP-40), WP-43 auf dem Beziehungsgraphen (WP-42) – beide released; WP-45 braucht WP-44; WP-43 erscheint als Bereich der Berichtsseite (WP-72), braucht sie aber nicht zwingend. Die Abschnitte sind Vorschläge; „Offen“ wird beim Start mit dem Nutzer entschieden, dann wie üblich der Plan.

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
- Je Zeile: „Zeilen zeigen“ – das Filtermodell kennt kein `NOT EXISTS`, deshalb ein Tab mit `in`-Filter auf die verwaisten Werte (bis zu einer Grenze) oder die Abfrage im SQL-Editor; „Constraint anlegen“ bei 0 Verwaisten und PK/UK auf der Gegenseite, mit Index für den FK. Für Beziehungen aus dem C#-Modell gibt es den Weg schon (vorgezogen, `small-model-fk-constraint.md`): `TableDesignerDraft.AddForeignKey` und `TableTab.RequestForeignKey` (mit `OpenDesignerAt`) öffnen den Designer mit dem FK – der Bericht ruft denselben Weg auf und zeigt vorher die Zahl der Verwaisten.
- Kosten: volle Scans großer Tabellen über das VPN – Auswahl der Schemas/Beziehungen, je Beziehung abbrechbar, Laufzeit sichtbar, eigene Lese-Session (die Explorer-Session arbeitet einen Aufruf nach dem anderen ab). Auf Prod erlaubt (nur lesend).
- Platz in der Oberfläche: ein Bereich „Datenqualität“ der Berichtsseite (WP-72, Idee des Nutzers 2026-11-03), dort mit eigenem „Prüfen“ – die übrigen Bereiche der Seite kosten keine Abfragen.
- Offen: Ergebnis speichern, um Läufe zu vergleichen.

### WP-44 Daten-Diff
Branch `wp/44-data-diff`. Ein Weg über Flashback, die Erweiterung auf die Tabellen eines Mitschnitts kommt mit WP-45. Entscheidung des Nutzers (2026-10-28): direkt Flashback mit `MINUS`, der Snapshot im Speicher wandert in den Backlog (Ausweichweg ohne Recht oder für längere Zeiträume).
- **Startpunkt:** „Vergleich starten“ merkt die SCN per `SELECT TIMESTAMP_TO_SCN(SYSTIMESTAMP) FROM dual` (braucht kein V$-Recht). Später „Vergleichen“: der committete Stand jetzt gegen `tabelle AS OF SCN :start`, für den gefilterten Tab oder ganze Tabellen.
- **Diff in der Datenbank:** `… WHERE <Filter> MINUS … AS OF SCN :start WHERE <Filter>` und umgekehrt (eine Abfrage mit `UNION ALL` und Markierung). Über das VPN kommen nur die geänderten Zeilen; Schlüssel nur auf einer Seite = neu bzw. gelöscht, auf beiden = geändert, die Zellen vergleicht dann .NET (`RowComparison`). Reine Abfragen auf dem Leseweg, `AS OF` und `MINUS` entstehen im `QueryBuilder`. Mit Filter erscheinen Zeilen, die in den Filter hinein- oder herausgewandert sind, als neu bzw. gelöscht.
- **Eigene Lese-Session:** Die Workspace-Session sieht nach einem Flush ihre eigenen Änderungen; verglichen wird der committete Stand. Dieselbe Lese-Session braucht WP-43.
- **Grenzen:** `MINUS` vergleicht keine LOB/LONG/XMLTYPE – LOBs über `DBMS_LOB.GETLENGTH`, der Rest wird mit Hinweis ausgelassen. Schlüssel ist der PK, ohne PK die ROWID (nur stabil ohne Row Movement). Recht `FLASHBACK` nur für Tabellen anderer Owner nötig (ORA-01031 verständlich melden). Undo reicht je nach Tabelle unterschiedlich weit, beim Nutzer auf großen Tabellen etwa 4 h (ORA-01555/ORA-08180 mit diesem Hinweis melden); DDL dazwischen bricht den Vergleich (ORA-01466).
- Ergebnis nur im Speicher (Werte, auch von Prod).
- Offen: Obergrenze für geänderte Zeilen (abschneiden mit Hinweis); Auswahl der Tabellen bis WP-45 (Vorschlag: aktueller Tab plus Liste von Hand); Startpunkt je Tab oder je Workspace und ob er einen Neustart überlebt (nur die SCN, keine Werte); Darstellung (Grid mit Status-Spalte wie die Änderungsübersicht?).

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

### WP-74 Wege zwischen zwei Tabellen
(Idee des Nutzers 2026-11-06; braucht WP-46 und WP-69, beide released.) Branch `wp/74-relation-paths`.
- **Idee:** Tabelle A und Tabelle B wählen, die Ansicht zeigt alle Ketten von Beziehungen, über die man von A nach B kommt, z. B. `AUFTRAG_POSITION ↑ AUFTRAG ↑ KUNDEN` oder `AUFTRAG_POSITION ↑ ARTIKEL ↓ … ↑ LIEFERANT`. Jeder Schritt geht zum Elternteil (↑, n:1) oder zu einem Kind (↓, 1:n). Nur in FK-Richtung fände man die meisten sinnvollen Wege nicht.
- **Entscheidungen des Nutzers (2026-11-06):**
  1. **Platz:** ein Modus der Ansicht „Beziehungen“ (WP-46). Neben „Ebenen“ kommt ein Feld „Weg zu …“ mit der Tabellensuche des Explorers dazu, die Tabelle des Tabs ist A.
  2. **Keine feste Grenze** für Länge und Zahl der Wege (der Nutzer hat oft lange Wege). Kosten: Die Zahl der Wege ohne Kreis wächst exponentiell mit der Länge, über Sammeltabellen schnell in die Millionen. Deshalb kommen die kürzesten Wege zuerst und erscheinen nach und nach. Die Suche läuft im Hintergrund, zeigt den Stand (Länge, Zahl der Wege), lässt sich abbrechen und mit „weitere Wege“ fortsetzen; das UI blockiert nie.
  3. **„Nicht über diese Tabelle“** gehört in die erste Stufe. Sammeltabellen (wer angelegt hat, Mandant) führen sonst in jeden Weg. Die Liste gilt je Verbindung und wird gespeichert.
  4. **„Abfrage bauen“** mit dem gewählten Weg gehört ins Paket.
- **Suche (Core, `Relations`):**
  - Über `RelationGraph` in beide Richtungen (`ParentsOf`, `ChildrenOf`), mit den Arten, die die Ansicht zeigt (`DiagramOptions.Sources`).
  - Wege ohne Kreis: Jede Tabelle kommt höchstens einmal vor, Selbstbezüge bleiben draußen, ausgeschlossene Tabellen auch (A und B selbst ausgenommen).
  - Zwei Beziehungen zwischen denselben Tabellen (Rechnungs- und Lieferkunde) ergeben zwei Wege, gewollt.
  - Vorschlag: eine Breitensuche von B aus misst den Abstand jeder Tabelle zu B. Eine Suche Länge für Länge (oder Yen, k kürzeste einfache Wege) verlängert einen Weg nur, solange B in den übrigen Schritten noch erreichbar ist. Als `IAsyncEnumerable` bzw. in Blöcken mit `CancellationToken`, damit die UI nachlädt und abbricht.
- **Reihenfolge der Wege:**
  1. Kürzere zuerst.
  2. Dann Wege nur mit ↑-Schritten: Sie liefern genau eine B-Zeile je A-Zeile. Je Weg sichtbar als „1 : 1“ bzw. „1 : n“ (ein ↓-Schritt vervielfacht die Zeilen).
  3. Dann nach Art: nur Constraints vor C#-Modell vor anderen.
- **Ansicht:**
  - Links die Liste der Wege als Kette mit Pfeilen und Tabellen- bzw. Entity-Namen, dazu Länge und „1 : 1“/„1 : n“.
  - Rechts das Diagramm aller gefundenen Wege zusammen: A ganz links, B ganz rechts, dazwischen die Tabellen nach ihrem Abstand zu A. In diesem Modus gilt „Eltern links, Kinder rechts“ nicht, die Richtung zeigt der Punkt am referenzierenden Ende wie bisher (`DiagramLayout` ist allgemein über Knoten und Schichten, Bögen innerhalb einer Spalte kann es schon).
  - Überfahren oder Wählen eines Wegs hebt seine Kanten hervor.
  - Je Weg: „Abfrage bauen“, „Als Mermaid kopieren“, „Als Text kopieren“ (`A.KUNDE_ID → KUNDEN.ID → …`).
  - „Nicht über diese Tabelle“ im Kontextmenü eines Knotens und in der Liste; die ausgeschlossenen Tabellen stehen sichtbar über der Liste und lassen sich dort wieder zulassen.
- **„Abfrage bauen“:**
  - Ein Weg ist schon die Form eines Knotens in WP-69: `QueryDesignState` mit einem Knoten je Präfix des Wegs, Schritte als `QueryStep(Relation, ToChild)`. Der Weg geht als neue Abfrage des Tabs hinein (Modus aktiv, B gewählt).
  - Hat der Tab schon eine Abfrage, nachfragen: ersetzen oder den Weg anhängen (`QueryDesignState.Add` je Schritt, vorhandene Präfixe bleiben).
  - Ein ↓-Schritt ist dort „ein Kind wählen“ (das erste nach Sortierung, WP-69). Das steht als Hinweis am Weg, damit „1 : n“ nicht stillschweigend zu einer Zeile wird.
- **Speichern der ausgeschlossenen Tabellen:** Vorschlag in `settings.json` wie die Tasten der Verbindungen (`AppSettings.ConnectionShortcuts`): Verbindungs-ID → Tabellen als Owner und Name (kein `OWNER.NAME`-Text, gequotete Namen dürfen Punkte enthalten). Eine eigene Klasse mit Vergleich nach Inhalt wie `SettingsMap`, damit `AppSettings` ein Wert bleibt. `null` von Hand beim Laden zu leer (`SettingsStore.LoadAsync`), gelöschte Verbindungen räumt `ConnectionGroups.CleanUpAsync` mit ab wie die Tasten.
- **Später, eigener Schritt:** dasselbe im Datensatz-Diagramm (WP-60/62). Von einer markierten Zeile in A würden die Zeilen in B entlang eines gewählten Wegs geladen, über die vorhandene Traversierung.
- Offen: ob „weitere Wege“ eine feste Blockgröße hat (Vorschlag 50) oder bis zum Abbruch weiterläuft; ob die Wahl von B mit dem Tab gespeichert wird (Vorschlag: ja, wie die Abfrage aus WP-69; die Wege selbst nur im Speicher).

## Offene UX-Fragen
- Workspaces: Standardname ist „Workspace N“ mit der kleinsten freien Nummer (nach Umbenennen von „Workspace 1“ heißt der nächste wieder „Workspace 1“) – so lassen oder fortlaufend zählen?
- Verwendung im Code, „kein Index“ (small-usage-filter-without-index): die Markierung auch in der Spaltenansicht und im Hinweis des Designers? Ein Knopf „Index anlegen“ (Entwurf in der Index-Ansicht wie bei Fremdschlüsseln ohne Index)?
- Markierte ganze Spalten (WP-58): Ctrl+C bei bekannter Zeilenzahl über der Grenze gar nicht erst lesen? Heute liest es bis zur Grenze und meldet dann „nichts kopiert“.
