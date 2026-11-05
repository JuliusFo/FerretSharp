# ADR 0044: Wege zwischen zwei Tabellen ohne feste Grenze

- Status: akzeptiert
- Datum: 2026-11-06
- Paket: WP-74 (`docs/work-packages/wp-74-relation-paths.md`)

## Kontext

Die Ansicht „Beziehungen“ (WP-46) zeigt die Nachbarschaft einer Tabelle. Der Nutzer will sehen, über welche Ketten von Beziehungen man von einer Tabelle zu einer anderen kommt, auch über lange Wege („wir haben gerne mal lange Wege“) – und den Weg dann als Abfrage (WP-69) weiterbauen. Die Zahl der Wege ohne Kreis zwischen zwei Tabellen wächst exponentiell mit der Länge; in einem Schema mit 425 Entities und Sammeltabellen (wer angelegt hat, Mandant), an denen fast jede Tabelle hängt, sind es schnell Millionen. Eine feste Grenze für Länge oder Zahl wollte der Nutzer nicht.

## Entscheidung

- **Keine feste Grenze, sondern Blöcke** (`RelationPathSearch`, Core): Die Suche liefert die Wege kürzeste zuerst, je Aufruf höchstens einen Block (50 in der Ansicht), und macht beim nächsten Aufruf dort weiter. Sie läuft auf einem Hintergrund-Thread, zeigt die Länge, bei der sie ist, lässt sich abbrechen und danach fortsetzen. Sie endet von selbst, wenn alle Wege gefunden sind.
- **Verfahren:** Eine Breitensuche vom Ziel aus gibt jeder Tabelle ihren Abstand zum Ziel (ohne die Regel „jede Tabelle einmal“, also nie mehr als ein Weg braucht). Dann eine Tiefensuche Länge für Länge (iterative Vertiefung): Ein Weg geht nur weiter, solange das Ziel in den übrigen Schritten erreichbar bleibt. Wurde eine Länge durchsucht, ohne einen Weg wegen der Länge abzuschneiden, gibt es keine längeren – die Suche ist fertig. Kürzere Wege werden bei jeder Länge erneut durchlaufen; bei exponentiellem Wachstum kostet das einen konstanten Faktor.
- **Eigener Stapel statt Iterator:** Ein C#-Iterator ist nach einer Ausnahme beendet, ein Abbruch hätte die Suche verloren. Der Stapel (`Frame` mit Tabelle und nächstem Schritt) wird erst nach der Prüfung des Tokens verändert, also steht er nach einem Abbruch, wie er war.
- **Wege ohne Kreis in beide Richtungen:** Jeder Schritt geht zum Elternteil oder zu einem Kind, jede Tabelle kommt höchstens einmal vor, Selbstbezüge zählen nicht, zwei Beziehungen zwischen denselben Tabellen ergeben zwei Wege. Reihenfolge innerhalb einer Länge: nur Schritte zu Eltern („1 : 1“) vor Wegen mit einem Schritt zu Kindern, dann nach der schwächsten Art (Constraint vor C#-Modell vor anderen) – da Länge für Länge gesucht wird, sortiert sich ein neuer Weg nur unter die seiner Länge.
- **Ausgeschlossene Tabellen** gelten je Verbindung und stehen in `settings.json` (`AppSettings.PathAvoidedTables`, Typ `TableListMap`): Verbindungs-ID → Tabellen mit Owner und Name getrennt (ein gequoteter Name darf einen Punkt enthalten), nach Inhalt verglichen wie `SettingsMap`. Gelöschte Verbindungen räumt `ConnectionGroups` mit ab wie die Tasten der Verbindungen. Keine eigene Datei: Die Liste ist klein, gehört zum Nutzer und braucht keine eigenen Lese- und Fehlerregeln.

## Folgen

- Lange Wege sind erreichbar, aber jeder weitere Block kostet mehr, je länger die Wege werden; ein Block kann bei großen Längen lange brauchen, bis er 50 Wege findet – deshalb Fortschritt und Abbrechen.
- Die gefundenen Wege leben nur im Speicher; gespeichert wird mit dem Tab nur das Ziel (`TabState.PathTarget`).
- Wege im Datensatz-Diagramm (Zeilen entlang eines Wegs) sind ein eigener späterer Schritt (Roadmap/Backlog).
