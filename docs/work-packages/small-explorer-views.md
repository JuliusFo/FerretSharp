# Klein: Views als eigener Bereich im Explorer

Branch `feat/explorer-views` (von `main`), 2026-10-21.

## Anlass
Frage des Nutzers: Views per Option nicht bei den Tabellen, sondern als eigener Bereich „Tabellen | Views | PL/SQL“, und trotzdem in der Explorer-Suche zu finden.

Entscheidungen des Nutzers:
- Die Suche bleibt wie in WP-28: Sie filtert den aktiven Bereich und nennt Treffer in den anderen („3 Treffer unter PL/SQL“, Klick wechselt). Eine gruppierte Trefferliste über alle Bereiche untereinander war besprochen und wurde verworfen, die Schnellsuche (WP-38) deckt das ab.
- Ohne Antwort übernommen (Vorschlag aus der Bewertung, offen zur Nachfrage): Materialized Views stehen unter Views; die Einstellung ist standardmäßig aus, der Explorer bleibt also wie bisher.

## Gebaut
- Einstellung „Views als eigener Bereich“ unter Einstellungen › Darstellung (`AppSettings.ViewsInOwnSection`, `SettingKey.ExplorerViews`).
- `ExplorerSection.Views`; die Zuordnung liegt rein in `ExplorerList` (`Sections`, `Shown`, `IsIn`, `OtherHits`), getestet in `ExplorerListTests`. Wird die Einstellung ausgeschaltet, während Views gewählt ist, zeigt der Explorer die Tabellen (`Shown`); der gewählte Bereich bleibt gemerkt.
- Der Hinweis auf andere Bereiche nennt jetzt jeden Bereich mit Treffern, in der Reihenfolge des Umschalters, je ein Link.
- Der Views-Bereich zählt ungültige Views im Umschalter wie PL/SQL. Anzahl im Kopf, Platzhalter der Suche und Text der leeren Liste je Bereich; der Tooltip „Tabellen, Views und Materialized Views“ entfällt, solange Views getrennt sind.
- Der Explorer rendert bei einer Änderung der Einstellungen nur neu, wenn sich diese Einstellung geändert hat (alle Explorer offener Verbindungen bleiben gemountet).
- „+“ (neue Tabelle) bleibt wie im PL/SQL-Bereich auch unter Views sichtbar.

## E2E
Nicht geprüft: Docker lief nicht, also kein eigener Test-DB-Container; ohne Schema zeigt der Explorer keine Liste.
