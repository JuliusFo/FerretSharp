# WP-65 Zusammengesetzte Anzeigenamen

Branch `wp/65-composite-display-names`, begonnen am 2026-10-27. Fortsetzung von WP-31 (ADR 0022, Nachtrag WP-65).

## Auftrag (2026-10-27)
Wunsch eines Kollegen des Nutzers: Bei manchen Tabellen sagt eine Spalte allein zu wenig (Vor- und Nachname, Nummer und Bezeichnung); der Name neben FK-Werten soll aus mehreren Spalten bestehen können. Mit dem Nutzer am selben Tag als Paket geplant, mit einer zweiten Stufe über einen weiteren FK.

## Planung (aus `docs/roadmap.md`)
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

## Beim Start entschieden (2026-10-27, vom Nutzer freigegeben)
- „Zum Namen hinzufügen“ ist eine Menüzeile (`MenuEntry`), keine fünfte Schnellaktion – die Leiste hält ihren Platz für WP-55.
- „Als Anzeigespalte verwenden“ ersetzt einen mehrteiligen Namen durch diese eine Spalte; „Aus dem Namen entfernen“ nimmt nur eine Spalte heraus.

## Gebaut
- **Core (`Core/Display`):**
  - `DisplayColumns` hält je Tabelle eine geordnete Liste (`ColumnsOf`, `IndexesIn`, `With`, `Adding`, `Removing`; leere und doppelte Namen fallen weg).
  - `DisplayColumnDocument`: neues Feld `displayNames` (nur Tabellen mit mehr als einer Spalte), `displayColumns` behält deren erste Spalte; `Set` schreibt Version 2 nur mit `displayNames`, sonst 1. Beim Lesen gewinnt `displayNames`. `DisplayColumnSource.AddAsync`/`RemoveAsync` neben `SetAsync`, alle über dasselbe frische Lesen der Datei.
  - `DisplayLink.Parts` (`DisplayPart(Column, Via)`) statt `DisplayColumn`; `Label` (`NACHNAME - VORNAME`), `SameAs` (auch eine Ebene tiefer). `DisplayLinks.Resolve` lässt Spalten weg, die das Ziel nicht hat oder die nicht als Text lesbar sind; `PlanAsync` sieht im Ziel nur die FKs über Anzeigespalten an (lädt keine Details anderer Tabellen) und hängt sie per `Nest` an – eine Ebene, der innere Link ohne `Via`.
  - Lookup: `QueryBuilder.BuildLookup(target, key, displayColumns, keys)` liest alle Spalten in einer Abfrage; `LookupRow.Values`, `IRowLookup`/`OracleDataAccess` (auch `NestedLookup`) mit Spaltenliste.
  - `DisplayNames`: Formatierer `(Ziel, Spalte, Wert)`, Teile über `Compose` mit `Separator` ` - ` (NULL/leer fällt weg, Teile getrimmt – CHAR-Padding), gekürzt wird der ganze Name; Namen je Schlüssel gecacht. Eine Ebene tiefer: `Found` sammelt die Werte der `Via`-Teile, `Missing` liefert sie als `DisplayLookup` mit `Part` (`Source` = innerer Link); bis zur Antwort, bei „nicht gefunden“ und nach einem Fehler steht der Wert. Fehler/Timeout dort schalten nur diesen Teil ab (`Wants`), `Dropped` stellt die Schlüssel wieder an. `FillAsync` (Export aller Zeilen) arbeitet in einer Schleife, bis nichts mehr fehlt.
  - Export: Überschrift `KUNDE_ID · NACHNAME - VORNAME` (`PresentedExport`).
- **UI:**
  - `GridDisplayNames` stellt nach jedem `Found` neu in die Warteschlange (Lookups eine Ebene tiefer), vergleicht Pläne über `SameAs`, meldet einen abgeschalteten Teil mit eigenem Text (`Grid_DisplayPartOff`) ohne neue Spaltenköpfe.
  - Badges: `GridColumn.DisplayBadge` statt `bool DisplayColumn`, Text aus `GridColumn.DisplayBadgeOf` („Name“, bei mehreren „Name 1“, „Name 2“ …) im Grid-Kopf (`grid.js`) und in der Spaltenansicht.
  - Spaltenmenü: Stern-Aktion „Als Anzeigespalte verwenden“, bei einer Spalte eines mehrteiligen Namens „Aus dem Namen entfernen“; hat die Tabelle einen Namen ohne diese Spalte, die Zeile „Zum Namen hinzufügen: …“. Meldungen nennen den neuen Namen.
  - Export aller Zeilen nimmt die Darstellung der inneren Ziele vorab mit (formatiert außerhalb des UI-Threads); Datensatz-Diagramm setzt den Namen aus den eigenen Zeilenwerten zusammen (ohne zweite Ebene).
  - Texte Englisch/Deutsch (`GridText`).
- **Tests:** Core (`DisplayColumnSourceTests` Datei mit beiden Feldern und Versionen, `DisplayLinksTests` Teile und zweite Ebene, `DisplayNamesTests` Zusammensetzen, Kürzen, zweite Ebene samt Fehler, Abbruch, Kopie und Export, `QueryBuilderTests` mehrere Spalten), UI (`GridDisplayNamesTests` Folge-Lookups und Fehler eine Ebene tiefer, `DisplayBadgeTests`), Integration `DisplayLookupTests` (mehrere Spalten in einem Lookup, zweite Ebene gegen Oracle 23).

## Offen
- Datensatz-Diagramm: keine zweite Ebene (bräuchte eigene Lookups).
- Vorlage für den Trenner (`{NACHNAME}, {VORNAME}`) nur bei Bedarf (Entscheidung des Nutzers).
