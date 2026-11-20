# WP-91 Explorer-Favoriten und „Zuletzt geöffnet“ → noch ohne Release

Branch `wp/91-favorites-recent` (von `main`), 2026-11-20, noch ohne Release.

## Planung
Aus der UX-Ideensammlung (2026-11-19) und dem Backlog („Schema und Explorer“: Favoriten; Schnellsuche: „Zuletzt geöffnet“). Favoriten setzt der Nutzer, „Zuletzt geöffnet“ füllt sich von selbst. Favoriten wollte der Nutzer ausdrücklich („steht auch auf meiner Liste“, 2026-11-19).

Plan am 2026-11-20 vom Nutzer so freigegeben („Ja, so umsetzen“), mit Claudes Vorschlägen zu den offenen Punkten:
- **Favoriten nach Art und angezeigtem Namen, ohne Owner, für alle Verbindungen** – wie die Filter-Vorlagen (WP-37): auf Dev markiert, ist die Tabelle auch auf Prod Favorit.
- **Symbol Lesezeichen** (Lucide `bookmark`): Der Stern steht schon für die Anzeigespalten, ein Symbol hat nur eine Bedeutung. **Nachtrag im E2E:** Auch `bookmark` war schon vergeben (Link auf die Ansicht, `LinkView`; `link` ist der Link auf Zeilen) – Claude hatte nur nach Stern und Pin gesucht. Gebaut mit Lucide `heart`, Frage an den Nutzer offen.
- **Favoriten-Block je Bereich:** unter *Tabellen* die Tabellen (und Views, solange sie keinen eigenen Bereich haben), unter *Views* die Views, unter *PL/SQL* die Einheiten.
- **„Zuletzt geöffnet“ zählt jedes Aktivieren** eines Tabellen- oder PL/SQL-Tabs, auch den Wechsel zwischen offenen Tabs.
- Leere Schnellsuche: Favoriten, offene Tabs, dann „Zuletzt geöffnet“ ohne die offenen; je Gruppe höchstens 10.

## Gebaut
- **Core `Schema/Favorites`:** `SchemaObjectKind` (Tabelle – Tabellen, Views und MViews teilen in Oracle einen Namensraum – oder die Art der PL/SQL-Einheit; Trigger haben einen eigenen), `Favorite(Kind, Name)` mit dem angezeigten Namen (`DisplayName`, bei einem Synonym dessen Name). `FavoriteStore` (`favorites.json`): kaputt, nicht lesbar oder neuer → Fehler, nie überschrieben; von Hand Geändertes (null, leere Namen, unbekannte Art als Zahl – ein unbekannter Text macht die Datei kaputt, eine neue Art erhöht deshalb die Dateiversion –, doppelt, über 500 Einträge, Namen über 260 Zeichen) fällt beim Lesen weg.
- **Core `Workspaces/RecentObjects`:** `RecentObject(Kind, Owner, Name, OpenedAt)` mit dem echten Objekt, damit es nach dem Schließen des Tabs im Schema wiedergefunden wird; `RecentObjectStore` auf dem `HistoryFileStore` (`recent-objects\{id}.json`, neueste zuerst, jedes einmal, höchstens 30). Wird mit der Verbindung gelöscht.
- **UI-State:** `FavoriteService` (wie `FilterPresetService`: einmal gelesen, vor einem gelungenen Lesen kein Schreiben). `RecentObjects` merkt sich je Verbindung den aktiven Tab – über `Shell.OnChanged`, auf dem UI-Thread; Zeitreise-, SQL- und LINQ-Tabs zählen nicht. Ein Besuch erscheint sofort, Lesen und Schreiben laufen der Reihe nach; was die Datei beim Lesen oder als Antwort auf ein Schreiben hält, kommt hinter die Besuche der Sitzung, nie an ihre Stelle (Code-Review). `QuickPaletteSearch.Favorites`/`Recent` stellen die Gruppen der leeren Schnellsuche zusammen.
- **Explorer:** jeder Eintrag in einer `.item-row` mit einem eigenen Knopf `.fav-toggle` (Herz, `Icons.Favorite`) daneben (ein Knopf im Knopf wäre kein gültiges HTML) – beim Überfahren sichtbar, bei einem Favoriten immer und gefüllt. Oben in der Liste der Block „Favoriten“ des Bereichs, die Suche filtert ihn mit; eine nicht lesbare Datei steht dort als Fehler. „Im Explorer zeigen“ markiert den Eintrag in der Liste darunter, nicht den im Block.
- **Objektmenüs:** `FavoriteAction` in der Leiste von `TableMenuBody` und `PlSqlMenuBody` (auch am Tab-Kopf; dort hatte das PL/SQL-Menü bisher keine Leiste).
- Texte in `SchemaViewText`, `ShellText`, `WorkspaceText` (Englisch und Deutsch).
- Tests: `FavoritesTests`, `RecentObjectStoreTests` (Core), `FavoriteServiceTests`, `RecentObjectsTests`, zwei Fälle in `QuickPaletteSearchTests` (UI).

## E2E
Eigene Testinstanz (`--lang=de`, eigener Datenordner) gegen einen eigenen Container mit dem Beispielschema, zwei Verbindungen (Dev, Prod) auf denselben Container. Klicks, Rechtsklick und Überfahren mit echter Windows-Eingabe (Probe bestanden).
- Gefunden und behoben: Das Lesezeichen (`bookmark`) war schon das Symbol von „Link kopieren“ – im Objektmenü standen zwei gleiche Symbole nebeneinander. Jetzt Lucide `heart` (Frage an den Nutzer offen, siehe Planung).
- Der Klick aufs Herz öffnet keinen Tab; der Favoriten-Block erscheint nur im Bereich der Art (Tabellen-Favoriten nicht unter *PL/SQL*).
- Ein Neustart behält Favoriten und „Zuletzt geöffnet“; auf Prod gelten die Favoriten von Dev, „Zuletzt geöffnet“ ist dort leer.
- Hell und dunkel geprüft; Log ohne Warnungen.
- Nicht im E2E: eine kaputte `favorites.json` (Fehler oben in der Liste) – nur in den Unit-Tests.

## Nachtrag: Code-Review (2026-11-20)
Eigener Reviewer-Agent über den ganzen Branch; kein S- und kein M-Befund. Entscheidung des Nutzers: „Ja, alle“ (sieben Befunde), nach dem zweiten Durchgang „Ja, alle drei“.
1. Klicks aufs Herz während des Speicherns verloren den vorigen, ein Doppelklick markierte zweimal → behoben: Umschalten nacheinander, die Liste erst nach dem Warten berechnet (Test `Clicks_while_saving_act_one_after_the_other`).
2. Eine neuere oder gesperrte `recent-objects`-Datei kürzte die Liste im Speicher auf den letzten Besuch → behoben; im zweiten Durchgang nachgebessert (A): Das Ergebnis eines Schreibens wird hinter die Besuche der Sitzung gesetzt, nie an ihre Stelle und nicht verworfen, damit nach einem gescheiterten ersten Lesen die Einträge der Datei wiederkommen (Tests `Visits_a_newer_file_does_not_take_stay_in_memory`, `What_a_file_holds_comes_back_with_the_next_write_after_a_first_read_failed`).
3. Eine unbekannte Art als Text macht die Datei kaputt (nur Zahlen fallen weg) → festgehalten an `SchemaObjectKind` und hier: Eine neue Art erhöht die Dateiversion.
4. Ab dem 11. Favoriten fehlte in der leeren Schnellsuche jeder Hinweis → „N weitere im Explorer“ (eigener Text; „Suche eingrenzen“ der Suchgruppen passte im E2E nicht in eine Box ohne Suche).
5. Im Objektmenü blieb das Herz eines Favoriten ein Umriss → gefüllt wie am Eintrag (`QuickAction.Class`).
6. Ungenutztes `FavoriteService.Version` → entfernt.
7. Leerzeile teilte die Liste im CHANGELOG → entfernt.
- Zweiter Durchgang, außerdem: B (veralteter Satz zu „Zuletzt geöffnet“ in diesem Protokoll) und C (Formatierung in einem Test) → behoben.
- Vorher schon da, nicht durch das Paket schlimmer: dasselbe Rennen beim Speichern in `FilterPresetService` (dort aus Dialogen), keine Größengrenze in Bytes in `VersionedJsonFile`.
- Fixes 4 und 5 im E2E geprüft (Testinstanz, echter Rechtsklick, 13 Favoriten).
