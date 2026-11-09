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

### WP-45 Mitschnitt mit Quellzeile und Datenänderungen
(braucht WP-40 und WP-44.) Branch `wp/45-recording-source-data`. Fortsetzung von WP-33 (ADR 0023).
- **Quellzeile je Statement:** EventPipe liefert zu Ereignissen den Aufrufstapel; der erste Rahmen außerhalb von EF Core/`System`/`Microsoft` ist die Methode des Nutzers, über den Index aus WP-40 mit Datei und Zeile. Aus „18× Lazy Loading `Auftrag.Positionen`“ wird „… aus `AuftragService.GetOffene`, `AuftragService.cs:142`“, ohne Code-Änderung und ohne `TagWithCallSite`. **Spike zuerst:** Stapel in Echtzeit inklusive Methoden-Rundown, Kosten für die App.
- **Datenänderungen (Daten-Diff Stufe 3):** „Aufzeichnen“ merkt die SCN der zugeordneten Verbindung; die Zieltabellen der INSERT/UPDATE/DELETE/MERGE-Statements des Mitschnitts bestimmen, welche Tabellen „Stopp“ per Flashback (WP-44) vergleicht. Ergebnis: alle Änderungen der App in allen betroffenen Tabellen, ohne vorher etwas auszuwählen. Audit-Tabellen (WP-26) auf Wunsch mit, weil ihre Trigger nicht im Mitschnitt stehen.
- Feldliste bleibt fest (ADR 0023): aus dem Stapel nur Methodennamen, nie Werte – Nachtrag zum ADR.
- Die Quellzeile erscheint in der Anzeige „Herkunft im Code“ aus WP-76 (Serverlast, seit 3.64.0) als sicherer Treffer vor den Kandidaten (Vorschlag, nicht bestätigt). Gelernte Zuordnungen `SQL_ID` → Datei:Zeile ließen sich speichern und in der Serverlast auch für Prod nachschlagen (gleicher Build → gleicher SQL-Text → gleiche `SQL_ID`).
- Offen: Ergebnis des Spikes; ob die Quellzeile nur für die Top-Statements ermittelt wird (Kosten).

### WP-47 Startseite „Seit deinem letzten Besuch“ mit Schema-Cache
Branch `wp/47-since-last-visit`.
- **Voraussetzung, aus dem Backlog („Reaktionszeit über das VPN“, Punkt 1):** letzten Schema-Stand je Verbindung speichern, den Explorer sofort daraus füllen und im Hintergrund abgleichen (stale-while-revalidate, sichtbares „Stand von …“). Vorher messen, wie dort vorgeschlagen.
- **Startseite nach dem Verbinden:** Unterschiede zum letzten Stand über den Schema-Vergleich (WP-20, zwei Snapshots derselben Verbindung) – neue/entfernte Tabellen und Spalten, geänderte Typen, neu ungültige Objekte –, neue Abweichungen zum C#-Modell (WP-27), offene Transaktionen anderer Workspaces, zuletzt benutzte Workspaces. Der Release-Check (Backlog, „später“) käme dort als weiterer Block dazu.
- Offen: eigene Seite oder Hinweis „3 Änderungen seit …“, der die Seite öffnet; was vor dem Abgleich erlaubt ist (Backlog-Eintrag).

### WP-79 SQL-Bedingung als Filter eines Tabellen-Tabs
Branch `wp/79-sql-where-filter`. Anlass (2026-11-09): Rückmeldung aus dem Kollegenkreis – viele filtern in SQL Developer bzw. HORA fast immer über das WHERE-Feld der Tabellenansicht und steigen ohne so ein Feld kaum um. Ihr typischer Fall: `IN` auf eine andere Tabelle, darin ein weiteres `IN`. Geplant mit dem Nutzer: dieses Paket zuerst, der Beziehungsfilter (WP-80) später für die, die klicken statt tippen.
- **Eigener Modus statt neuer Filterart:** Ein Tab filtert entweder über die Filterleiste oder über eine selbst geschriebene Bedingung (ohne `WHERE`). Die Bedingung ist kein `FilterCondition` – die Invariante „Filtermodell 1:1 als LINQ-`Where`“ bleibt. Folge im SQL-Modus: kein Link, keine Vorlage, kein LINQ-Export, kein Spaltenprofil-Filter; der Tab-Titel zeigt die Bedingung gekürzt.
- **Was bleibt:** Der Tab bleibt ein Tabellen-Tab (ROWID/PK, Sortierung PK → ROWID, Paging) – Bearbeiten, FK-Sprung, Anzeigespalten, Präsentation, Export und Zählen wie bisher. Das ist der Gewinn gegenüber dem SQL-Editor.
- **Eingabe:** einzeiliges Monaco-Feld an der Stelle der Filterleiste (wächst bei Zeilenumbrüchen), Vervollständigung der Spalten der Tabelle und der Tabellen/Spalten wie im SQL-Editor, Enter wendet an, ein Oracle-Fehler (`ORA-00904` …) steht am Feld. Zuletzt benutzte Bedingungen je Tabelle zur Auswahl.
- **Umschalter Filterleiste ↔ SQL:** Wechsel zu SQL startet mit dem WHERE des aktuellen Filters, Bind-Werte als Literale eingesetzt (Datum/Zahl im festen Format, `TO_DATE`/`TO_TIMESTAMP`). Zurück zur Filterleiste verlustfrei nur bei unverändertem Text, sonst nach Rückfrage „Bedingung verwerfen?“. Kein Rückweg SQL → Filterzeilen (wäre ein Parser).
- **Prüfung vor dem Einsetzen** (Korrektheit, nicht Sicherheit – die Lesesperre bleibt unverändert und prüft das ganze Statement): Tokenizer des SQL-Editors; Klammern ausgeglichen, auf oberster Ebene kein `;`, `ORDER BY`, `UNION`/`INTERSECT`/`MINUS`, `FETCH`/`OFFSET`, `FOR UPDATE`; eingesetzt als `WHERE (\n…\n)`, damit ein `--` am Ende Sortierung und Paging nicht auskommentiert. Kombination mit Zeitreise (WP-78, `AS OF`) prüfen.
- **Prod:** Literale der Bedingung wie beim SQL-Editor nicht ins Log (`SqlRedaction`); der Verlauf behält sie (Literale bleiben, `CLAUDE.md` Invarianten). Workspace-Datei speichert Modus und Text.
- **Anzeige (Entscheidung des Nutzers, 2026-11-09):** Beide Eingaben nie gleichzeitig aktiv – je Tab ein Modus, das Feld steht an der Stelle der Filterleiste, Standard die Filterleiste; eine Einstellung „Filter beginnt mit: Filterleiste / SQL-Bedingung“, damit die SQL-Developer-Gewohnten das Feld immer sofort haben. Kein UND aus beiden (dann gälten Link und LINQ nur halb).
- **Tabs mit mitgebrachtem Filter (Entscheidung des Nutzers, 2026-11-09):** Der Inhalt bestimmt den Modus. FK-Sprung, Link und Vorlage bringen Filterzeilen und öffnen in der Filterleiste wie heute, auch wenn die Einstellung auf SQL-Bedingung steht; die Einstellung gilt nur für Tabs ohne Filter (z. B. aus dem Explorer). So bleiben Link und LINQ des Sprung-Filters erhalten; wer tippen will, schaltet um (der Filter wird als Text übernommen). Ein Sprung aus einem Tab im SQL-Modus nimmt wie bisher die Werte der markierten Zeilen mit.
- Offen: ob der Wechsel per Tastenkürzel geht (neue Aktionen ohne Standardtaste, `docs/architecture.md` 3); ADR für den Modus neben dem Filtermodell.

### WP-80 Beziehungsfilter („hat … / hat keine …“)
Branch `wp/80-relation-filter`. Die geklickte Form desselben Falls wie WP-79 (`IN` auf eine andere Tabelle, verschachtelt), aber im Filtermodell – also mit Link, Vorlage und LINQ. Braucht WP-79 nicht.
- Filter über einen **Beziehungspfad** wie bei der FK-Navigation (A → B → C), Quellen wie überall (`Declared`, `Manual`, `Convention`, `ClrModel`); Bedingungen mit der gewohnten Filterleiste auf der Zieltabelle, inklusive ODER-/NICHT-Gruppen (WP-50). Die Tiefe kommt über den Pfad, nicht über verschachtelte Gruppen (die bleiben eine Ebene, Entscheidung WP-50).
- SQL: `IN`/`EXISTS` je Stufe; Art „hat keine …“ als `NOT EXISTS` mit derselben NULL-Sorgfalt wie die NICHT-Gruppen. LINQ: Referenz `x.B.C.Status == …`, Collection `x.Bs.Any(b => b.Cs.Any(c => …))`; ohne Navigation im C#-Modell der ganze Beziehungsfilter als Kommentar (wie eine Gruppe ohne C#-Form).
- Links: neues Format (`v=3`), ältere FerretSharp lehnen ab.
- Erledigt nebenbei den Punkt „das Filtermodell kennt kein `NOT EXISTS`“ aus WP-43 („Zeilen zeigen“ ohne Grenze für verwaiste Werte).
- Offen (Fragen an den Nutzer, 2026-11-09): Laufen die `IN` der Kollegen über bekannte Beziehungen oder über beliebige Spaltenpaare (Ad-hoc-Paarung „Spalte hier ↔ Spalte dort“ ginge als SQL, nicht sauber als LINQ)? Bedingungen auch auf Zwischentabellen (B) oder nur am Ende des Pfads (deutlich einfachere Oberfläche)?

## Offene UX-Fragen
- Workspaces: Standardname ist „Workspace N“ mit der kleinsten freien Nummer (nach Umbenennen von „Workspace 1“ heißt der nächste wieder „Workspace 1“) – so lassen oder fortlaufend zählen?
- Verwendung im Code, „kein Index“ (small-usage-filter-without-index): die Markierung auch in der Spaltenansicht und im Hinweis des Designers? Ein Knopf „Index anlegen“ (Entwurf in der Index-Ansicht wie bei Fremdschlüsseln ohne Index)?
- Markierte ganze Spalten (WP-58): Ctrl+C bei bekannter Zeilenzahl über der Grenze gar nicht erst lesen? Heute liest es bis zur Grenze und meldet dann „nichts kopiert“.
