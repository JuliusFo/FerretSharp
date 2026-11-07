# WP-76 Herkunft im Code für Top-Statements → Release 3.64.0

Branch `wp/76-statement-code-origin` (von `main`), 2026-11-08. Nachtrag zu ADR 0030. Baut auf WP-40 (Code-Index) und WP-75 (Serverlast).

## Planung (aus `docs/roadmap.md`)
- Wunsch des Nutzers (2026-11-08): In der Serverlast sehen, welche Stellen im Code die meiste Last verursachen – ohne laufende App und ohne Code-Änderung in ihr. WP-45 hilft dafür nicht, weil es eine lokal angehängte App braucht; daher ein eigenes Paket, unabhängig von WP-45 (Entscheidung des Nutzers, 2026-11-08). Der Rest dieses Abschnitts ist Vorschlag (nicht bestätigt).
- **Kandidaten statt sicherer Zuordnung:** Aus dem SQL-Text eines Statements (`SqlAnalysis`: Tabellen samt Joins, mit Binds verglichene Spalten) und dem Code-Index (WP-40: je Methode `Query`, `Filter`, `Expression`, `Write` mit Datei und Zeile) über `ClrModelMapping` (Tabelle ↔ Entity, Spalte ↔ Property) eine Rangliste der Methoden, deren Abfrage passt – mit Begründung („filtert `KUNDE_ID`, `STATUS`; liest `AUFTRAG` und `POSITION`“) und Sprung in Visual Studio wie in „Verwendung“.
- **Anzeige „Herkunft im Code“** in den Details der Serverlast, später auch in „Statements der App“. Sichere Treffer aus WP-45 kommen in dieselbe Anzeige vor die Kandidaten – so baut keins das andere doppelt.
- **Vorher messen:** am echten Projekt des Nutzers (nur lokal, nichts davon ins Repo), wie viele Kandidaten ein typisches Top-Statement bekommt; Abfragen nur nach PK/FK passen vermutlich auf viele Methoden.
- Bekannte Grenzen: generische Repositories (`Set<T>()` im generischen Code, die Entity kennt der Index dort nicht; die Filter-Lambda steht beim Aufrufer); von EF eingesetzte Konstanten statt Binds (dafür die Spalten im `WHERE` allgemein, kleine Erweiterung von `SqlAnalysis`); INSERT/UPDATE/DELETE aus `SaveChanges` nur über Schreibzugriffe auf die Entity, deutlich unschärfer.
- Nicht in WP-76: Datei:Zeile als Kommentar im SQL-Text (`TagWithCallSite()` oder ein `DbCommandInterceptor` in der App) auswerten (Entscheidung des Nutzers, 2026-11-08; Backlog).
- Offen: Schwelle, ab der Kandidaten gar nicht erst gezeigt werden; ob die Bewertung auch `ORDER BY` und Navigations (`Include`) heranzieht.
- Beim Start mit dem Plan freigegeben (2026-11-08, „Ja, umsetzen“), die Vorschläge zu den offenen Punkten unverändert: Test- und Mapping-Code sind keine Kandidaten; die besten 5, die übrigen auf Klick, keine Mindestschwelle außer einer Wertung über null; Statements ohne Tabelle des Modells blenden den Abschnitt aus (der Index wird für sie gar nicht erst gelesen); Navigations zählen, `ORDER BY` nicht; die Messung am echten Projekt macht der Nutzer mit einem Test-Build.

## Gebaut
- **Core:**
  - `Query/SqlEditor/SqlConditions` (`SqlConditionColumn`): Spalten, nach denen ein Statement in einem WHERE filtert – jede Ebene, auch in Funktionsaufrufen und Listen, Spalte gegen Spalte (Join, Korrelation) und die Auswahlliste einer Unterabfrage nicht. Unqualifizierte Wörter können Schlüsselwörter sein, der Aufrufer behält nur bekannte Spalten.
  - `ClrModel/Usage/CodeOriginFinder`: `TablesOf` (Tabellen des Statements, die zum Modell gehören, ohne Index – entscheidet, ob die Solution überhaupt gelesen wird) und `Find` (Rangliste der Methoden mit Gründen `CodeOriginReason`: fragt ab, liest über Navigation, lädt per Lazy Loading, filtert nach, schreibt). Tabellen über Name und Owner (Owner des Statements bzw. Parsing-Schema gegen Schema der Entity bzw. Default-Schema; Views mit ihrem Schema und ihren Spalten), Entities auf derselben Tabelle (Owned, TPH) zählen mit. Bewertung (Vorschlag, nicht bestätigt): +3 je abgefragter Tabelle, +2 je über Navigation erreichter (+1 für Lazy Loading, wenn das Statement nur nach Schlüsseln filtert), +2 je gemeinsamer Filterspalte, +3 je geschriebener Tabelle eines DML-Statements, −1 je Tabelle, die die Methode nicht berührt, je Filterspalte und je `Include` der Methode, die das Statement nicht hat. Ein Filter allein zählt als Abfrage der Entity (generische Repositories). Angezeigt wird die erste passende Verwendung der Methode nach Zeile.
- **UI:**
  - `CodeOrigins` (Components/Schema): Abschnitt „Herkunft im Code“ in den Details der Serverlast, unter dem SQL-Text; baut den Index beim ersten Bedarf (`CodeUsageService.EnsureAsync`), Ergebnis je Statement und Index zwischengespeichert; Klick öffnet die Stelle wie in „Verwendung“ (`CodeUsageOpener`).
  - `CodeOriginRows` (State/Model): Gründe als eine Zeile, die ersten 5 (`Initial`).
  - Texte `ModelViewText.CodeOrigin_*` (Englisch, Deutsch), Glossar „Herkunft im Code“, CSS unter „Server load“.
- **Tests:** Core `SqlConditionsTests`, `CodeOriginFinderTests` (Filter, Join über Navigation, Lazy Load nach Schlüssel, DML, generisches Repository, Abzüge, Statements ohne Tabelle des Modells, View mit Schema); UI `CodeOriginRowsTests`.

## E2E
(folgt)
