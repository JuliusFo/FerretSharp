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
- Je Zeile: „Zeilen zeigen“ – das Filtermodell kennt kein `NOT EXISTS`, deshalb ein Tab mit `in`-Filter auf die verwaisten Werte (bis zu einer Grenze) oder die Abfrage im SQL-Editor; „Constraint anlegen“ bei 0 Verwaisten und PK/UK auf der Gegenseite, mit Index für den FK. Für Beziehungen aus dem C#-Modell gibt es den Weg schon (vorgezogen, `small-model-fk-constraint.md`): `TableDesignerDraft.AddForeignKey` und `TableTab.DesignerForeignKey` öffnen den Designer mit dem FK – der Bericht ruft denselben Weg auf und zeigt vorher die Zahl der Verwaisten.
- Kosten: volle Scans großer Tabellen über das VPN – Auswahl der Schemas/Beziehungen, je Beziehung abbrechbar, Laufzeit sichtbar, eigene Lese-Session (die Explorer-Session arbeitet einen Aufruf nach dem anderen ab). Auf Prod erlaubt (nur lesend).
- Offen: Ergebnis speichern, um Läufe zu vergleichen; Platz in der Oberfläche (Seite oben rechts wie der Schema-Vergleich oder Ansicht je Verbindung).

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

### WP-69 Abfrage aus dem Beziehungsdiagramm
(Idee des Nutzers, als Paket eingeplant am 2026-10-31; Zuschnitt und Ausgabe überarbeitet am 2026-11-02 an einem typischen Ausdruck aus dem Projekt des Nutzers.) Branch `wp/69-diagram-query`. Baut auf dem Beziehungsdiagramm (WP-46), dem Graphen (WP-42), den C#-Namen und Navigations (WP-12), der LINQ-Erzeugung (WP-15) und der LINQ-Konsole (WP-13).
- **Einstieg:** im Beziehungsdiagramm ein Modus „Abfrage bauen“, außerdem im Objektmenü (WP-70, `TableMenuBody`) „Abfrage bauen“, das die Ansicht „Beziehungen“ im Modus öffnet.
- **Modus „Abfrage bauen“:** Die Fokus-Tabelle ist die Wurzel. Gewählt werden **Kanten**, nicht Tabellen. Ein Knoten der Abfrage ist ein **Pfad aus Kanten** ab der Wurzel (`RelationGraph.PathKey`), keine Tabelle (Entscheidung, 2026-11-02): Dieselbe Tabelle kann über zwei Wege vorkommen (Rechnungs- und Lieferkunde, ein Selbstbezug, dieselbe Tabelle über zwei verschiedene Ketten). Im Diagramm bleibt sie ein Knoten mit mehreren markierten Kanten; in der Seitenleiste und im SQL (eigenes Kürzel) sind es getrennte Zweige. Spalten werden nicht in den Knoten angehakt (verschieden hohe Knoten brächten das Layout aus WP-46 durcheinander), sondern in einer Seitenleiste als Baum (`Position → Auftrag → Kunde → Name`); darunter die Ausgabe in Reitern LINQ | SQL | Vorschau.
- **Erste Stufe** (Entscheidung, 2026-11-02; vorher nur Eltern):
  - **Eltern** (n:1), auch über mehrere Ebenen (`Position → Auftrag → Kunde`) – je Zeile der Wurzel höchstens eine Zeile, die Zeilenzahl bleibt. In LINQ eine Navigation (`p.Auftrag!.Kunde!.Name`, EF wählt den Join nach required/optional), im SQL ein INNER JOIN nur bei FK-Spalten NOT NULL mit durchgesetztem Constraint (ENABLED und VALIDATED – `ForeignKeyInfo` kennt den Status dafür noch nicht), sonst LEFT JOIN (Beziehungen aus dem C#-Modell, manuell oder per Konvention garantieren nichts).
  - **Ein Kind wählen** (1:n auf genau eine Zeile): Filter und Sortierung am Kind-Knoten, davon die erste Zeile (`a.Positionen.Where(…).OrderBy(…).FirstOrDefault()`), von dort weiter zu dessen Eltern. Die Zeilenzahl der Wurzel bleibt. Ohne gewählte Sortierung nach dem Primärschlüssel (sonst ist „die erste“ zufällig). In LINQ ein eigener Ausdruck für das gewählte Kind, den die Werte per LinqKit `Invoke` benutzen; im SQL `OUTER APPLY (… FETCH FIRST 1 ROW ONLY)` (ab 12c).
  - Später (zweite Stufe): Kinder als verschachtelte Liste (`Positionen = a.Positionen.Select(…).ToList()`, im SQL ein LEFT JOIN, der die Zeilen vervielfacht) und Kind-Filter, die die Wurzel einschränken (`Any`/`EXISTS`). Handarbeit bleiben `SelectMany`/`Concat` über zwei Wege und `??` zwischen zwei Pfaden.
- **Filter an Knoten:** je Knoten Bedingungen im vorhandenen Filtermodell (Spalte, Operator, Werte, Gruppen seit WP-50), übersetzt mit derselben Semantik wie das Grid (`LinqFilter` – muss dafür Parameter, Pfad und Quelle frei nehmen, heute `x`, `x.Property` und `db.…`). An der Wurzel und an Eltern-Knoten schränkt die Bedingung die Zeilen der Wurzel ein (`p.Auftrag!.Kunde!.Land == …`; im SQL im `WHERE`). Eine Zeile ohne Elternteil bleibt dabei nach den NULL-Regeln des Grids drin, wo der Operator NULL einschließt („≠“, „ist NULL“), sonst fällt sie heraus – in LINQ und SQL gleich. Am gewählten Kind bestimmt die Bedingung, welches Kind gewählt wird.
- **Ausgabe LINQ** (Entscheidung, 2026-11-02; ersetzt „ein Prädikat und eine Projektion“): im Stil der Extensions des Nutzers ein Ausdruck **je Wert**, LinqKit zum Zusammensetzen.
  - Eine Spalte der Wurzel steht direkt in der Projektion bzw. Zuordnung (`a => a.Nummer`). Jeder Wert über einen Pfad bekommt ein Feld: `internal static readonly Expression<Func<Auftrag, string?>> KundeName = a => a.Kunde!.Name;`. Optionale Navigations mit `!`, Werttypen über einen optionalen Pfad nullbar gecastet (`(long?)a.Kunde!.Nummer`).
  - Das gewählte Kind als Hilfsausdruck (`private static readonly Expression<Func<Auftrag, Position?>> ErstePosition = a => a.Positionen.Where(…).OrderBy(…).FirstOrDefault();`), Werte darüber mit `Linq.Expr((Auftrag a) => ErstePosition.Invoke(a)!.Artikel.Name).Expand()` – durch `Expand()` bei der Definition braucht der Aufrufer kein `AsExpandable()`.
  - Filter der Wurzel und der Eltern-Knoten als Prädikat `Expression<Func<Auftrag, bool>> OffeneAuftraege = a => …;` (entfällt ohne Filter).
  - Standard: die Projektion `a => new AuftragUebersicht { Nummer = a.Nummer, KundeName = KundeName.Invoke(a), … }` mit `.Expand()`; ohne LinqKit im Projekt stehen die Pfade direkt in der Projektion. Nutzung: `db.Auftraege.Where(OffeneAuftraege).Select(Uebersicht)`.
  - **Zeilenvorlage je verknüpftem Projekt** (optional): Projekte mit eigener Mapping-Bibliothek geben Kopf, eine Zeile je Wert und Fuß mit Platzhaltern vor (`{Entity}`, `{Dto}`, `{Expression}`, `{Property}`); FerretSharp setzt nur Text ein, wie bei den Audit-Vorlagen. Mit Vorlage ersetzt die Liste der Zuordnungen die Projektion.
  - **DTO:** Schalter „DTO mit erzeugen“ (Standard an; aus, wenn es das DTO schon gibt). `record` oder `class`; Namen der Properties aus dem Pfad (`KundeName`), in der Vorschau änderbar.
  - Beziehungen ohne Navigation (Constraint, den das Modell nicht abbildet, manuell oder per Konvention) gehen nur in die SQL-Ausgabe; der LINQ-Reiter nennt die Kante (Entscheidung, 2026-11-02 – in einen Ausdruck über eine Entity passt kein Join ohne Navigation).
- **Filterwerte** (Entscheidung, 2026-10-31): fest im Ausdruck, je Bedingung ein Schalter „als Parameter“ – ob ein Wert fest ist (`Status == Offen`) oder wechselt (Datum, ID), entscheidet der Nutzer, nicht eine Regel. Mit Parametern wird das Prädikat eine Methode: `public static Expression<Func<Auftrag, bool>> OffeneAuftraege(DateTime ab) => a => a.Datum >= ab && …;`, Parametername aus der Property, änderbar. In der LINQ-Konsole stehen Parameter als lokale Variablen mit dem Wert aus dem Filter, damit die Probe sofort läuft.
- **Stil fest vorgegeben** (Entscheidung, 2026-10-31): In der Vorschau Name der statischen Klasse, Namen der Ausdrücke, Name des DTO, `record` oder `class`, Namespace, „DTO mit erzeugen“; die letzte Wahl wird je verknüpftem Projekt gemerkt. Den Stil am Projekt lernen (Code-Index WP-40) steht im Backlog.
- **Ausgabe SQL:** erst eine `SELECT`-Vorschau über den Leseweg (echte Zeilen, mit Binds), dann Kopieren, „Im SQL-Editor öffnen“ oder `CREATE VIEW` über den Schemaweg mit dem DDL-Baustein aus WP-23. Joins entstehen in `Core/Query` (der `QueryBuilder` kennt heute nur eine Tabelle mit festem Kürzel), der DDL-Text in `Core/Ddl`.
  - Eine View kennt keine Binds: Werte als Literale (`OracleLiteral`). Bedingungen „als Parameter“ bleiben aus der View draußen, ein Hinweis über dem DDL nennt sie (Entscheidung, 2026-11-02).
  - `CREATE VIEW` ohne `OR REPLACE`; ist der Name vergeben, ein Hinweis (Prüfung aus WP-68).
  - LOB-Spalten sind in der ersten Stufe nicht wählbar.
- **Weitergabe** (Entscheidung, 2026-10-31): Kopieren und „In LINQ-Konsole öffnen“ (zeigt das SQL von EF und die Zeilen – Probe ohne Build). Nicht ins Projekt schreiben; das kommt frühestens mit dem Unterbau aus WP-41.
- **Zustand mit dem Tab gespeichert** (Entscheidung, 2026-10-31): gewählte Kanten (als Pfade), Spalten, Filter und Sortierung (samt „als Parameter“), Namen und ob der Modus an ist – eine Abfrage entsteht oft über mehrere Tage. Das Aufklappen des Diagramms bleibt wie in WP-46 nur im Speicher. Fehlt beim Wiederherstellen eine Spalte oder Beziehung (Schema geändert), fällt sie mit Hinweis heraus, wie Tabs auf gelöschten Tabellen.

## Offene UX-Fragen
- Workspaces: Standardname ist „Workspace N“ mit der kleinsten freien Nummer (nach Umbenennen von „Workspace 1“ heißt der nächste wieder „Workspace 1“) – so lassen oder fortlaufend zählen?
- Verwendung im Code, „kein Index“ (small-usage-filter-without-index): die Markierung auch in der Spaltenansicht und im Hinweis des Designers? Ein Knopf „Index anlegen“ (Entwurf in der Index-Ansicht wie bei Fremdschlüsseln ohne Index)?
- Markierte ganze Spalten (WP-58): Ctrl+C bei bekannter Zeilenzahl über der Grenze gar nicht erst lesen? Heute liest es bis zur Grenze und meldet dann „nichts kopiert“.
