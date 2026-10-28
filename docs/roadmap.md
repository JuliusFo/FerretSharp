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

### WP-66 Verbindungsgruppen verwalten
Wünsche eines Kollegen, vom Nutzer am 2026-10-28 weitergegeben und um die Menüs und Tastenkürzel ergänzt. Branch `wp/66-connection-groups`. Ein Paket, weil alle Teile an Umschalter, Verbindungsseite und Verbindungsdialog hängen.

Ausgangslage: Eine Gruppe ist nur der Text `ConnectionProfile.Group`. Die Reihenfolge ist fest (`ConnectionSearch.Group`: alphabetisch, „Ohne Gruppe“ zuletzt). Der Aufklappzustand liegt flüchtig und doppelt in `ConnectionSwitcher` und `ConnectionsPage` (je ein `HashSet`). Am Gruppennamen hängen die Audit-Vorlage der Gruppe (`AppSettings.AuditTemplateGroups`, WP-51) und der aktive Arbeits-Klon (`work-clones.json`, WP-40).
- **Gruppe umbenennen:** „Umbenennen…“ im Menü der Gruppe. In Core übernimmt das `ConnectionManager.RenameGroupAsync(alt, neu)`: Es speichert alle Profile der Gruppe in einem Schreibvorgang und nimmt die Audit-Vorlage, den Arbeits-Klon, den Aufklappzustand und die Position mit. Heute bleibt ein solcher Eintrag nach dem Umbenennen als verwaist stehen. Ein Name, den es schon gibt (ohne Leerraum und Groß-/Kleinschreibung verglichen), legt die Gruppen nach einer Rückfrage zusammen. Haben beide eine Audit-Vorlage, behält die Zielgruppe ihre, und die Rückfrage sagt das. „Ohne Gruppe“ lässt sich nicht umbenennen. Offene Verbindungen zeigen den neuen Namen sofort (`ConnectionLabel`). Die Audit-Zeile liest die Vorlage beim nächsten Laden des Tabs wie bisher.
- **Aufklappzustand speichern:** ein Zustand für Umschalter und Verbindungsseite, gespeichert in einer neuen Datei `connection-groups.json`. Die Datei ist Zustand und keine Einstellung, deshalb gilt die Regel von `recent.json`: kaputt, nicht lesbar oder neuer heißt alles aufgeklappt, und eine neuere Datei bleibt stehen (`docs/architecture.md` 1.7 um eine Zeile ergänzen). Bei einer Suche ist wie bisher alles aufgeklappt.
- **Reihenfolge der Gruppen:** Gruppenköpfe lassen sich auf der Verbindungsseite ziehen (`dom.js dragToDrop` aus WP-50). „Nach oben“ und „Nach unten“ im Menü der Gruppe machen dasselbe per Tastatur. Gespeichert wird in `connection-groups.json`. Gruppen ohne gespeicherte Position stehen alphabetisch dahinter, „Ohne Gruppe“ bleibt zuletzt. `ConnectionSearch.Group` nimmt die Reihenfolge als Parameter, damit sie überall gleich gilt: Umschalter, Seite, Link-Dialog, Auswahl beim Export. Import und Export übernehmen sie nicht, weil sie lokaler Zustand ist.
- **„⋯“-Menüs nach dem Muster von WP-61:** gebaut aus den vorhandenen Bausteinen `QuickBar`/`QuickAction`, einem Kopf wie `ColumnMenuHead` und `menuKeys`. Die Menüs sind kurz, deshalb ohne Filterfeld.
  - *Verbindung* (`ConnectionActionsMenu`): Kopf mit Art-Badge, Adresse, User → Schema, Schreibschutz, Gruppe, zuletzt benutzt, verbunden/angezeigt und Tastenkürzel. Leiste mit Icons: Verbinden (hervorgehoben), Bearbeiten, Duplizieren, Adresse kopieren, Exportieren und, wenn die Verbindung offen ist, Trennen. „In Gruppe verschieben“ zeigt die vorhandenen Gruppen als Chips, dazu „Neue Gruppe…“ und „Ohne Gruppe“. „Löschen…“ bleibt eine rote Textzeile.
  - *Gruppe:* Kopf mit Name, Zahl der Verbindungen und davon verbunden. Leiste mit Beschriftung: Umbenennen, Neue Verbindung in dieser Gruppe (Gruppe vorbelegt), Exportieren, Nach oben, Nach unten. Darunter Zeilen, die nur erscheinen, wo sie gelten: Audit-Vorlage der Gruppe, Arbeits-Klon der Gruppe (beide springen zur vorhandenen Einstellung), Alle trennen.
  - *Für alle `MoreMenu`:* `KeyNavigation` an, also ↑ ↓ und Enter auch im „C# ▾“ der Filterleiste und in der Verwendungsansicht. Ein Rechtsklick auf eine Verbindung oder einen Gruppenkopf öffnet dasselbe Menü, auf der Verbindungsseite und im Umschalter. Neue Icons kommen aus Lucide 1.55 (ADR 0035).
- **Tastenkürzel je Verbindung:** ein Feld „Tastenkürzel“ im Verbindungsdialog, mit derselben Aufnahme (`shortcuts.js record`) und Prüfung (`ShortcutMap.Check`: kein Ctrl+Alt, Ctrl, Alt oder F-Taste nötig, Warnung bei Tasten der Editoren) wie auf der Einstellungsseite. Ist die Taste vergeben, sagt der Dialog, wem sie gehört, und bietet an, sie zu übernehmen.
  - *Wirkung:* Ist die Verbindung offen, wird sie angezeigt wie bei Alt+O, sonst läuft `Shell.Connect`. Passwortabfrage, Prod-Markierung und Schreibschutz gelten wie beim Klick im Umschalter. In einem offenen Dialog bewirkt die Taste nichts, wie die übrigen globalen Tasten.
  - *Gespeichert in den Einstellungen, nicht im Profil:* `AppSettings.ConnectionShortcuts` (Profil-Id → Taste). So kennt `ShortcutMap` alle Tasten, und Konflikte werden in beide Richtungen geprüft. Ein Import bringt keine Tasten anderer mit. Die Einstellungsseite bleibt die Übersicht aller Tasten: Sie bekommt einen Bereich „Verbindungen“ mit Name, Taste und einem Sprung in den Dialog. Wird eine Verbindung gelöscht, fällt auch ihre Taste weg.
  - *Sichtbar* ist die Taste in der Zeile im Umschalter (`Kbd`), im Kopf des Verbindungsmenüs und in der Befehlspalette. Es gibt keine Standardtaste (`docs/architecture.md` 3). Ctrl+1 … 9 bleibt für das Wechseln der Workspaces reserviert (WP-39), Alt+Ziffer ist frei.
- Kein ADR geplant: Die Bausteine gibt es schon, es kommt keine neue Abhängigkeit dazu und kein neuer Weg zur Datenbank.
- Offen:
  - Tastenkürzel nur in FerretSharp oder auch systemweit? Systemweit hieße, FerretSharp aus einem anderen Programm nach vorne zu holen und zu verbinden: `RegisterHotKey` im App-Projekt hinter einem Host-Interface, und die Taste fehlt dann anderen Programmen. Vorschlag: nur in FerretSharp, systemweit in den Backlog.
  - Verbindungen per Ziehen zwischen Gruppen verschieben? Vorschlag: nein, die Chips im Menü reichen, und im Popover des Umschalters ist Ziehen fummelig. In den Backlog.
  - Soll der Aufklappzustand im Umschalter und auf der Seite wirklich gemeinsam gelten? Vorschlag: ja.

## Offene UX-Fragen
- Workspaces: Standardname ist „Workspace N“ mit der kleinsten freien Nummer (nach Umbenennen von „Workspace 1“ heißt der nächste wieder „Workspace 1“) – so lassen oder fortlaufend zählen?
