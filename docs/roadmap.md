# Roadmap – geplante Pakete und offene Fragen

Seit 3.14.0 aus der `CLAUDE.md` ausgelagert; Stand nach WP-26. Beim Start eines Pakets den Abschnitt lesen und den Plan mit dem Nutzer abstimmen. Nicht eingeplante Ideen: `docs/backlog.md`; abgeschlossene Pakete: `docs/work-packages.md`.

## GitHub-Issues und Labels

Entscheidung des Nutzers (2026-10-09). Issues bekommen eine Art und einen Status.
- **Art:** `enhancement`, `bug`, `documentation`, `question`. Optional der Bereich: `area: grid`, `area: sql-editor`, `area: linq` oder `area: schema`.
- **Status**, je Issue genau einer:
  - `needs decision`: Das Issue hat offene Fragen. Die Antworten des Nutzers kommen beim Start des Pakets hierher in den Abschnitt des Pakets, danach wird das Label entfernt.
  - `planned`: Das Issue ist als Paket eingeplant. In der Überschrift des Pakets hier steht der Link zum Issue.
  - `done`: Das Issue ist umgesetzt und released. `planned` wird entfernt, dazu kommt ein Abschlusskommentar mit Version, Entscheidungen und dem, was offen bleibt (Backlog). Dann wird das Issue geschlossen.
- Labels setzen dürfen nur Leute mit Triage-Rechten. Wer ein Issue anlegt, setzt in der Regel keine Labels; das geschieht beim Einplanen.

## Geplant (v4)

Pakete aus dem Backlog, nach v3 mit dem Nutzer ausgewählt (2026-10-05). Versionen: Minor-Releases 3.x (nichts Inkompatibles).

WP-22 (DDL im SQL-Editor) ist umgesetzt: Protokoll in `docs/work-packages.md`, ADR 0019. WP-23 (Tabellen-Designer + Entity aus Tabelle) ist in 3.22.0–3.24.0 umgesetzt: Protokoll in `docs/work-packages.md`, ADR 0020. R4a (Fehler aus dem Review 3.24.0) ist in 3.24.1 umgesetzt, R4b (Struktur) in 3.25.0, WP-26 (Audit-Tabellen aus einer Vorlage) in 3.26.0 (Protokolle mit den Entscheidungen des Nutzers in `docs/work-packages.md`, ADR 0021). WP-31 (Anzeigespalten) ist umgesetzt, aber noch nicht released (Protokoll in `docs/work-packages.md`, ADR 0022). Weitere Pakete sind noch nicht eingeplant – beim nächsten Mal mit dem Nutzer aus `docs/backlog.md` auswählen.

### WP-31 Anzeigespalten: Namen zu FK-Werten im Grid ([#11](https://github.com/JuliusFo/FerretSharp/issues/11))
Idee des Nutzers (2026-10-12): Zu `AUFTRAG.KUNDE_ID = 4711` steht im Grid gleich der Name des Kunden – `4711 · PetersPizza`. Dafür wird je Tabelle eine **Anzeigespalte** festgelegt (`KUNDE` → `NAME`, `SDLAND` → `BEZEICHNUNG`); jeder FK auf diese Tabelle zeigt deren Wert neben seinem eigenen.

Entscheidungen des Nutzers (2026-10-12):
- **Festgelegt in FerretSharp, nicht im C#-Code.** Verworfen: ein Attribut an der Entity (`[DisplayColumn(nameof(Name))]` oder ein eigenes), das der ModelHost ausliest – es käme auf Prod erst nach dem Merge nach `release` an (ein Klon je Datenbank, CLAUDE.md Abschnitt 7) und ginge nur mit C#-Projekt.
- **Eine Datei, globaler Pfad** in den Einstellungen wie `AuditTemplatePath` (WP-26), z. B. im Repo der Firma, damit die Kollegen dieselben Anzeigespalten haben. Ohne Pfad liegt die Datei im Datenordner. FerretSharp schreibt nie in den Klon einer Verbindung und macht kein Git; committen tut der Nutzer.
- **Schlüssel ist der Tabellenname ohne Owner** – das Schema ist auf Prod/Test/Dev dasselbe, der Schemaname kann abweichen.
- **Anzeige in derselben Zelle** (Wert, daneben gedämpft der Name), keine eigene Spalte. In den Optionen abschaltbar.

Plan (umgesetzt wie hier beschrieben; Abweichungen und Messung im Protokoll):
- Format git-freundlich: JSON, Schlüssel sortiert, ein Eintrag je Zeile, mit Version und einem Objekt `displayColumns`, damit später weitere Einträge hineinpassen (z. B. virtuelle FKs, Backlog). Vor jedem Schreiben neu lesen und Fremdänderungen übernehmen (`AtomicJsonFile`); nach einem `git pull` per File-Watcher neu laden. Einträge, deren Tabelle oder Spalte in der aktuellen DB fehlt, werden still ignoriert. Neues persistentes, geteiltes Format → ADR.
- Bedienung: im Kontextmenü des Spaltenkopfs „Als Anzeigespalte verwenden“ (Häkchen, eine je Tabelle, eine neue ersetzt die alte), Badge im Kopf neben PK/FK, dieselbe Markierung in der Spaltenliste der Struktur. Nicht wählbar: LOB, LONG, RAW. Geht auch auf schreibgeschützten Verbindungen (es wird nicht in die DB geschrieben).
- Werte holen: nicht per JOIN in der Grid-Abfrage (fehlende Grants auf die Zieltabelle würden das ganze Grid brechen, nicht eindeutige Beziehungen ohne Constraint Zeilen doppeln), sondern je geladenem Block nachladen: verschiedene FK-Werte sammeln, `SELECT <PK>, <Anzeigespalte> FROM <Ziel> WHERE <PK> IN (…)` (SQL in `Core/Query`, Leseweg, Binds), Cache je Workspace, nachgereicht ins Grid (erst der Wert, dann der Name). Auf der Session des Workspace, damit nicht committete Zeilen und der Snapshot der Read-only-Transaktion stimmen. Cache nach Flush/Commit/Rollback und F5 verwerfen.
- Last (Frage des Nutzers): Je Block und FK-Spalte eine Abfrage über den eindeutigen Index des Ziels mit höchstens 500, meist wenigen verschiedenen Werten – billiger als die Grid-Abfrage selbst; kleine Nachschlagetabellen (Land, Status) sind nach dem ersten Block ganz im Cache. Was zählt, sind Roundtrips über das VPN und die Sperre der Session, nicht die Arbeit der DB. Vorkehrungen:
  - IN-Listen auf wenige feste Größen auffüllen (z. B. 1/10/50/100/500, letzten Wert wiederholen), sonst entsteht je Anzahl ein eigener Cursor (Hard Parse, Shared Pool).
  - Nur nachschlagen, wenn die Zielspalten einen eindeutigen Index haben (deklarierte FKs immer; bei FKs aus dem C#-Modell ohne Constraint prüfen) – sonst kein Name statt Full Table Scan je Block. Ziel ist eine View: beim Start entscheiden (abweisen oder nur mit Hinweis).
  - Timeout und Abbruch je Abfrage; nach Timeout oder fehlendem Recht (ORA-00942, ORA-01031) die Spalte für den Tab abschalten, nicht wiederholen. Blöcke, die AG Grid beim schnellen Scrollen verwirft, nicht mehr nachschlagen.
  - Beim Start messen; reichen die Roundtrips nicht, die Abfragen eines Blocks zu einer (`UNION ALL`) zusammenfassen.
- Gilt für alle FK-Quellen (`Declared`, `ClrModel`, später `Manual`), zunächst nur einspaltige FKs. Ist die Anzeigespalte ein Enum oder konvertierter Bool, über die `ValueTable` der Zieltabelle darstellen.
- Wert ohne Treffer: „nicht gefunden“ markieren – macht verwaiste Verweise sichtbar (viele Beziehungen ohne Constraint), auch direkt beim Bearbeiten einer FK-Zelle.
- Filtern, Sortieren, Bearbeiten, Kopieren und Export bleiben bei den Rohwerten (Präsentationsschicht). Formularansicht und Zeilenvergleich zeigen den Namen mit.
- Später (Backlog): Auswahl der Zielzeilen mit Namen beim Bearbeiten einer FK-Zelle, Filtern/Sortieren nach dem Namen, mehrspaltige FKs, mehrere Spalten als Anzeige (Vorname + Nachname).

Beim Start selbst entschieden (einfachste Variante; Rückfrage an den Nutzer offen):
- **Views als Ziel** bekommen keine Namen: Sie haben keinen erzwungenen eindeutigen Schlüssel.
- **Cache je Tab** statt je Workspace, geleert bei jedem Lesen ab der ersten Zeile (F5, Filter, nach Schreiben, Commit, Rollback).
- Spalten mit Namen sind linksbündig und 220 px breit (vorher Zahl rechtsbündig, 140 px).
- Der Zeilenvergleich zeigt noch keine Namen (Backlog).

## Offene UX-Fragen
- Shortcut-Belegung für Commit/Rollback – vorläufig, Nutzerfeedback einholen.
- Workspaces: Standardname ist „Workspace N“ mit der kleinsten freien Nummer (nach Umbenennen von „Workspace 1“ heißt der nächste wieder „Workspace 1“). Shortcuts zum Wechseln (z. B. Ctrl+1…9) und eine Oberfläche für die Notizen fehlen noch.
