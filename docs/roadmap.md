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
Ausgewählt mit dem Nutzer am 2026-10-14 aus einer Ideensammlung (Bewertung des Nutzers je Paket in Klammern). Abhängigkeiten: WP-43 baut auf dem Beziehungsgraphen (WP-42, released); WP-45 braucht WP-44; WP-43 erscheint als Bereich der Berichtsseite (WP-72), braucht sie aber nicht zwingend. Die Abschnitte sind Vorschläge; „Offen“ wird beim Start mit dem Nutzer entschieden, dann wie üblich der Plan.

### WP-39 Workspaces anheften und wechseln
(Aus dem Backlog, dort als ein Paket vorgeschlagen.) Branch `wp/39-workspace-pins`.
- Workspaces pinnen, Varianten aus dem Backlog: a) fester Platz vorn in der Leiste, b) Schutz vor versehentlichem Schließen/Löschen, c) beim Verbinden immer mit öffnen; d) über Verbindungen hinweg ist größer (ein Workspace gehört heute genau einer Verbindung) – Vorschlag a–c, d bleibt im Backlog.
- Workspace-Übersicht je Verbindung (Tabs, offene Transaktion, zuletzt benutzt, Notizen) und Wechseln per Tastenkürzel.
- Entschieden (Nutzer, 2026-11-12):
  - **a und b**, c nur, wenn Workspaces beim Verbinden heute nicht ohnehin alle wiederhergestellt werden (beim Start prüfen). b als Rückfrage, keine Sperre.
  - **Ctrl+1 … 9 als Ausnahme** von „neue Aktionen bekommen keine Standardtaste“ (`docs/architecture.md` 3), in den Einstellungen änderbar; die angehefteten Workspaces stehen vorn und haben damit feste Tasten. Der Grund der Regel bleibt: Hat der Nutzer eine dieser Tasten schon einer anderen Aktion gegeben, behält er sie, und der Wechsel bekommt sie nicht.
  - **`Workspace.Notes` kommt mit**, als Feld in der Workspace-Übersicht.
  - Standardname „Workspace N“ mit der kleinsten freien Nummer bleibt (nach Umbenennen von „Workspace 1“ heißt der nächste wieder „Workspace 1“).

### WP-43 Datenqualität der Beziehungen
Branch `wp/43-relationship-check`.
- Bericht je Verbindung über den Graphen aus WP-42: für jede Beziehung **ohne** FK-Constraint die Zahl verwaister Werte (`NOT EXISTS`), mit Beispielwerten; Enum-Spalten mit Werten, die das C#-Enum nicht kennt; deklarierte Constraints im Zustand DISABLED/NOVALIDATE mit verletzenden Zeilen.
- Je Zeile: „Zeilen zeigen“ – das Filtermodell kennt kein `NOT EXISTS`, deshalb ein Tab mit `in`-Filter auf die verwaisten Werte (bis zu einer Grenze) oder die Abfrage im SQL-Editor; „Constraint anlegen“ bei 0 Verwaisten und PK/UK auf der Gegenseite, mit Index für den FK. Für Beziehungen aus dem C#-Modell gibt es den Weg schon (vorgezogen, `small-model-fk-constraint.md`): `TableDesignerDraft.AddForeignKey` und `TableTab.RequestForeignKey` (mit `OpenDesignerAt`) öffnen den Designer mit dem FK – der Bericht ruft denselben Weg auf und zeigt vorher die Zahl der Verwaisten.
- Kosten: volle Scans großer Tabellen über das VPN – Auswahl der Schemas/Beziehungen, je Beziehung abbrechbar, Laufzeit sichtbar, eigene Lese-Session (die Explorer-Session arbeitet einen Aufruf nach dem anderen ab). Auf Prod erlaubt (nur lesend).
- Platz in der Oberfläche: ein Bereich „Datenqualität“ der Berichtsseite (WP-72, Idee des Nutzers 2026-11-03), dort mit eigenem „Prüfen“ – die übrigen Bereiche der Seite kosten keine Abfragen.
- Entschieden (Nutzer, 2026-11-12): **den letzten Lauf je Verbindung speichern**, keine Historie – je Beziehung Zahl der Verwaisten und Zeitpunkt. Die Seite zeigt den letzten Stand sofort, ohne volle Scans über das VPN; ein neuer Lauf zeigt die Änderung zum vorigen („12 → 3“).

### WP-45 Mitschnitt mit Quellzeile und Datenänderungen
(braucht WP-40 und WP-44.) Branch `wp/45-recording-source-data`. Fortsetzung von WP-33 (ADR 0023).
- **Quellzeile je Statement:** EventPipe liefert zu Ereignissen den Aufrufstapel; der erste Rahmen außerhalb von EF Core/`System`/`Microsoft` ist die Methode des Nutzers, über den Index aus WP-40 mit Datei und Zeile. Aus „18× Lazy Loading `Auftrag.Positionen`“ wird „… aus `AuftragService.GetOffene`, `AuftragService.cs:142`“, ohne Code-Änderung und ohne `TagWithCallSite`. **Spike zuerst:** Stapel in Echtzeit inklusive Methoden-Rundown, Kosten für die App.
- **Datenänderungen (Daten-Diff Stufe 3):** „Aufzeichnen“ merkt die SCN der zugeordneten Verbindung; die Zieltabellen der INSERT/UPDATE/DELETE/MERGE-Statements des Mitschnitts bestimmen, welche Tabellen „Stopp“ per Flashback (WP-44) vergleicht. Ergebnis: alle Änderungen der App in allen betroffenen Tabellen, ohne vorher etwas auszuwählen. Audit-Tabellen (WP-26) auf Wunsch mit, weil ihre Trigger nicht im Mitschnitt stehen.
- Feldliste bleibt fest (ADR 0023): aus dem Stapel nur Methodennamen, nie Werte – Nachtrag zum ADR.
- Die Quellzeile erscheint in der Anzeige „Herkunft im Code“ aus WP-76 (Serverlast, seit 3.64.0) als sicherer Treffer vor den Kandidaten (Vorschlag, nicht bestätigt). Gelernte Zuordnungen `SQL_ID` → Datei:Zeile ließen sich speichern und in der Serverlast auch für Prod nachschlagen (gleicher Build → gleicher SQL-Text → gleiche `SQL_ID`).
- Entschieden (Nutzer, 2026-11-12): Umfang erst nach dem Spike festlegen. Bevorzugt: den Aufrufstapel je **eindeutigem Statement-Text** nur beim ersten Auftreten auflösen – alle Statements abgedeckt, Kosten wachsen mit den verschiedenen Statements statt mit den Ausführungen. „Nur Top-Statements“ nur, wenn schon das zu teuer ist.
- Offen: Ergebnis des Spikes.

### WP-47 Startseite „Seit deinem letzten Besuch“ mit Schema-Cache
Branch `wp/47-since-last-visit`.
- **Voraussetzung, aus dem Backlog („Reaktionszeit über das VPN“, Punkt 1):** letzten Schema-Stand je Verbindung speichern, den Explorer sofort daraus füllen und im Hintergrund abgleichen (stale-while-revalidate, sichtbares „Stand von …“). Vorher messen, wie dort vorgeschlagen.
- **Startseite nach dem Verbinden:** Unterschiede zum letzten Stand über den Schema-Vergleich (WP-20, zwei Snapshots derselben Verbindung) – neue/entfernte Tabellen und Spalten, geänderte Typen, neu ungültige Objekte –, neue Abweichungen zum C#-Modell (WP-27), offene Transaktionen anderer Workspaces, zuletzt benutzte Workspaces. Der Release-Check (Backlog, „später“) käme dort als weiterer Block dazu.
- Entschieden (Nutzer, 2026-11-12):
  - **Hinweis „3 Änderungen seit …“**, der die Seite öffnet; ohne Änderungen kein Hinweis. Keine Seite, die bei jedem Verbinden aufgeht (12+ Verbindungen).
  - **Vor dem Abgleich ist alles Lesende erlaubt** (Tabellen öffnen, filtern, navigieren – Daten sind ohnehin live; FK-Navigation über eine inzwischen gelöschte Beziehung scheitert mit Fehlermeldung). **Was auf dem Schema aufbaut, wartet auf den Abgleich:** Designer, DDL, Schema-Vergleich, Code-Generierung.

### WP-80 Beziehungsfilter („hat … / hat keine …“)
Branch `wp/80-relation-filter`. Die geklickte Form desselben Falls wie WP-79 (`IN` auf eine andere Tabelle, verschachtelt; SQL-Bedingung, `docs/work-packages/wp-79-sql-where-filter.md`), aber im Filtermodell – also mit Link, Vorlage und LINQ. Braucht WP-79 nicht.
- Filter über einen **Beziehungspfad** wie bei der FK-Navigation (A → B → C), Quellen wie überall (`Declared`, `Manual`, `Convention`, `ClrModel`); Bedingungen mit der gewohnten Filterleiste auf der Zieltabelle, inklusive ODER-/NICHT-Gruppen (WP-50). Die Tiefe kommt über den Pfad, nicht über verschachtelte Gruppen (die bleiben eine Ebene, Entscheidung WP-50).
- SQL: `IN`/`EXISTS` je Stufe; Art „hat keine …“ als `NOT EXISTS` mit derselben NULL-Sorgfalt wie die NICHT-Gruppen. LINQ: Referenz `x.B.C.Status == …`, Collection `x.Bs.Any(b => b.Cs.Any(c => …))`; ohne Navigation im C#-Modell der ganze Beziehungsfilter als Kommentar (wie eine Gruppe ohne C#-Form).
- Links: neues Format (`v=3`), ältere FerretSharp lehnen ab.
- Erledigt nebenbei den Punkt „das Filtermodell kennt kein `NOT EXISTS`“ aus WP-43 („Zeilen zeigen“ ohne Grenze für verwaiste Werte).
- Entschieden (Nutzer, 2026-11-12, zu den Fragen vom 2026-11-09):
  - **Nur bekannte Beziehungen**, aus allen vier Quellen (also auch `Manual`). Ein Spaltenpaar ohne Beziehung geht über die SQL-Bedingung (WP-79); der Pfad-Auswähler bietet „Beziehung anlegen…“ an, damit der Filter als LINQ übersetzbar bleibt.
  - **Bedingungen zuerst nur am Ende des Pfads.** Bedingungen auf Zwischentabellen später; das Linkformat `v=3` sieht dafür schon eine Liste je Stufe vor, damit es nicht noch einmal bricht.

### Kleine Themen
Entschieden mit dem Nutzer am 2026-11-12 (vorher offene UX-Fragen).
- **„Kein Index“ in Spalten-Ansicht und Designer, „Index anlegen“** (`small-usage-filter-without-index.md`): Die Spalten-Ansicht markiert gefilterte Spalten ohne Index wie „ungenutzt“; der Hinweis des Designers nennt sie; der Hinweis bekommt einen Knopf „Index anlegen“, der einen Entwurf in der Index-Ansicht öffnet wie bei Fremdschlüsseln ohne Index.
- **Ctrl+C auf markierte ganze Spalten über der Grenze** (WP-58): Ist die Zeilenzahl bekannt und Zeilen × Spalten über 100 000, wird nicht gelesen, sondern sofort gemeldet, dass es zu viele Zellen sind, mit einem Knopf zum Export. Bei unbekannter Zeilenzahl bleibt es wie heute (lesen bis zur Grenze, dann „nichts kopiert“).


## Offene UX-Fragen
(keine)
