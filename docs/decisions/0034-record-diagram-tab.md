# ADR 0034: Datensatz-Diagramm als eigener Tab, schrittweise gelesen

- Status: akzeptiert
- Datum: 2026-10-23
- Paket: WP-60

## Kontext

WP-60 zeigt konkrete Datensätze und ihre Verbindungen als Diagramm, in der Darstellung des Beziehungsdiagramms (WP-46, ADR 0033). Anders als dort steht nicht alles im Schema: Jede Zeile muss gelesen werden, über das VPN mit teuren Roundtrips, und eine Beziehung kann zu zehntausenden Zeilen führen (die Verträge eines Vertriebsmitarbeiters). Zu entscheiden war, wo das Diagramm lebt, was davon gespeichert wird und wie weit gelesen wird.

## Entscheidung

- **Ein eigener Tab im Workspace** (`RecordTab`, Entscheidung des Nutzers): Die Startzeilen sind eine Auswahl, keine Eigenschaft einer Tabelle – eine Ansicht im Tabellen-Tab passte nicht, ein Dialog verlöre das Diagramm beim Schließen. Der Tab liest auf der Session seines Workspaces und sieht so auch dessen nicht committete Zeilen.
- **Gespeichert werden Schlüssel und Schritte, nie Zeilen:** `RecordTabState` hält die Starttabelle, die Schlüssel der Startzeilen als Text (`RecordKeys`: die Filtertexte aus `FkNavigation.FilterValue`, die exakt zurückgelesen werden; ROWID unverändert) und die Schritte (`RecordStep`: eine Seite einer Zeile aufklappen, einer Beziehung folgen, eine Gruppe öffnen). Beim Zeigen liest der Tab die Zeilen neu und spielt die Schritte ab (`RecordDiagram.ReplayAsync`); was nicht mehr passt, überspringt seinen Schritt. Ein Schlüssel ohne exakten Text (BINARY_DOUBLE …) wird nicht gespeichert. Die Werte der Schlüssel stehen damit in der Workspace-Datei, wie es für den Arbeitsstand ohnehin gilt (`docs/architecture.md` 1.2).
- **Lesen nur auf Klick, eine Abfrage je Beziehung:** Ein neues Diagramm klappt die Startzeilen nach beiden Seiten auf; danach lädt jeder Griff „+n“ die offenen Beziehungen einer Seite, das Kontextmenü eine. „+n“ zählt Beziehungen, nicht Zeilen: Eine `COUNT`-Abfrage je Beziehung und Zeile wäre über das VPN teurer als das Lesen selbst.
- **Grenzen:** höchstens 2 000 Zeilen je Diagramm. `RowTraversal.FollowAsync` liest dafür höchstens eine Zeile über die Grenze (`IDataAccess.ReadByKeysAsync(…, maxRows, …)` bricht den Reader ab) und fügt bei Überschreitung **nichts** ein – eine Beziehung kommt ganz oder gar nicht. Sie erscheint dann als Meldung mit „Im Tab zeigen“ (ein FK-Sprung von denselben Zeilen). Mehr als 12 neue Zeilen über eine Beziehung einer Zeile werden zu einer Gruppe.
- **Ein Faden:** Das Diagramm ändert sich nur auf dem UI-Thread; die Ansicht ruft über `CallDbAsync`, die Datenbankaufrufe selbst sind asynchron. Ein Lesen zur Zeit, abbrechbar über `RunProgress`.
- **Kopieren:** Markierte Zeilen einer Tabelle starten den Dialog von WP-42 unverändert (`CopyTargets.Draft`). Die Vorschau der Kopie gibt es zusätzlich als Diagramm der Tabellen (`CopyPreviewDiagram`, Spalten in der Reihenfolge des Einfügens).

## Folgen

- Ein wiederhergestellter Tab kostet beim ersten Zeigen so viele Abfragen wie seine Schritte Beziehungen haben; restaurierte Tabs mounten aber ohnehin erst beim ersten Zeigen.
- Kommt das C#-Modell nach dem ersten Laden, lädt der Tab neu, wenn sich die Menge der Beziehungen wirklich ändert – nicht bei jedem Build des verknüpften Projekts, der eine neue, gleiche Liste bringt.
- Eine Kopie aus Zeilen mehrerer Tabellen und der Start aus einem SQL- oder LINQ-Ergebnis bleiben im Backlog.

## Nachtrag: mehrere Ebenen (WP-62, 2026-10-25)

- **Ebene** ist ein Schritt entlang einer Beziehung, in beide Richtungen: Kunde → Adressen (Kinder) → Land (Elternteil der Adresse) sind zwei Ebenen. Eingestellt im Kopf des Diagramms (1–4, Standard 1, gespeichert im Tab); sie gilt beim Öffnen, für „Zurücksetzen“ und für die Griffe „+n“. Einer einzelnen Beziehung aus dem Kontextmenü wird eine Ebene weit gefolgt.
- **Abbiegeregel:** Ein Pfad darf von abwärts (Kinder) nach aufwärts (Eltern) abbiegen, nie von aufwärts wieder abwärts. Sonst kämen über jeden Elternteil seine anderen Kinder herein – über das Land alle Adressen des Landes, über den Kunden alle seine anderen Aufträge. Links der Startzeilen geht es also nur aufwärts; was rechts als Kind erreicht wird, bekommt in der nächsten Ebene auch seine Eltern (die Nachschlagetabellen). Dieselbe Richtung wie beim Kopieren (WP-42), dort ohne Tiefe.
- **Lesen:** eine Breitensuche über `ExpandSideAsync`. Ausgangspunkt jeder Ebene sind die Zeilen, die die vorige neu gezeigt hat; eine Abfrage je Beziehung und Ebene für alle ihre Zeilen. Zeilen einer Gruppe gehen nicht weiter, die Grenze von 2 000 Zeilen bleibt – sie gilt je Beziehung über alle Zeilen der Ebene, also gröber als beim Aufklappen einer Zeile.
- **Schritte:** `RecordStep.Depth` (Standard 1, ältere Dateien gehen eine Ebene). Das Abspielen sucht auf den aktuellen Daten neu, statt die erreichten Zeilen zu speichern.
