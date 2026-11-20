# WP-91 Explorer-Favoriten und „Zuletzt geöffnet“ → noch ohne Release

Branch `wp/91-favorites-recent` (von `main`), 2026-11-20, noch ohne Release.

## Planung
Aus der UX-Ideensammlung (2026-11-19) und dem Backlog („Schema und Explorer“: Favoriten; Schnellsuche: „Zuletzt geöffnet“). Favoriten setzt der Nutzer, „Zuletzt geöffnet“ füllt sich von selbst. Favoriten wollte der Nutzer ausdrücklich („steht auch auf meiner Liste“, 2026-11-19).

Plan am 2026-11-20 vom Nutzer so freigegeben („Ja, so umsetzen“), mit Claudes Vorschlägen zu den offenen Punkten:
- **Favoriten nach Art und angezeigtem Namen, ohne Owner, für alle Verbindungen** – wie die Filter-Vorlagen (WP-37): auf Dev markiert, ist die Tabelle auch auf Prod Favorit.
- **Symbol Lesezeichen** (Lucide `bookmark`): Der Stern steht schon für die Anzeigespalten, ein Symbol hat nur eine Bedeutung.
- **Favoriten-Block je Bereich:** unter *Tabellen* die Tabellen (und Views, solange sie keinen eigenen Bereich haben), unter *Views* die Views, unter *PL/SQL* die Einheiten.
- **„Zuletzt geöffnet“ zählt jedes Aktivieren** eines Tabellen- oder PL/SQL-Tabs, auch den Wechsel zwischen offenen Tabs.
- Leere Schnellsuche: Favoriten, offene Tabs, dann „Zuletzt geöffnet“ ohne die offenen; je Gruppe höchstens 10.

## Gebaut
- **Core `Schema/Favorites`:** `SchemaObjectKind` (Tabelle – Tabellen, Views und MViews teilen in Oracle einen Namensraum – oder die Art der PL/SQL-Einheit; Trigger haben einen eigenen), `Favorite(Kind, Name)` mit dem angezeigten Namen (`DisplayName`, bei einem Synonym dessen Name). `FavoriteStore` (`favorites.json`): kaputt, nicht lesbar oder neuer → Fehler, nie überschrieben; von Hand Geändertes (null, leere Namen, unbekannte Art, doppelt, über 500 Einträge, Namen über 260 Zeichen) fällt beim Lesen weg.
- **Core `Workspaces/RecentObjects`:** `RecentObject(Kind, Owner, Name, OpenedAt)` mit dem echten Objekt, damit es nach dem Schließen des Tabs im Schema wiedergefunden wird; `RecentObjectStore` auf dem `HistoryFileStore` (`recent-objects\{id}.json`, neueste zuerst, jedes einmal, höchstens 30). Wird mit der Verbindung gelöscht.
- **UI-State:** `FavoriteService` (wie `FilterPresetService`: einmal gelesen, vor einem gelungenen Lesen kein Schreiben). `RecentObjects` merkt sich je Verbindung den aktiven Tab – über `Shell.OnChanged`, auf dem UI-Thread; Zeitreise-, SQL- und LINQ-Tabs zählen nicht. Ein Besuch erscheint sofort, Lesen und Schreiben laufen der Reihe nach; das zuletzt besuchte Objekt bleibt vorn, auch wenn ein Lesen oder ein früheres Schreiben später fertig wird. `QuickPaletteSearch.Favorites`/`Recent` stellen die Gruppen der leeren Schnellsuche zusammen.
- **Explorer:** jeder Eintrag in einer `.item-row` mit einem eigenen Knopf `.fav-toggle` daneben (ein Knopf im Knopf wäre kein gültiges HTML) – beim Überfahren sichtbar, bei einem Favoriten immer und gefüllt. Oben in der Liste der Block „Favoriten“ des Bereichs, die Suche filtert ihn mit; eine nicht lesbare Datei steht dort als Fehler. „Im Explorer zeigen“ markiert den Eintrag in der Liste darunter, nicht den im Block.
- **Objektmenüs:** `FavoriteAction` in der Leiste von `TableMenuBody` und `PlSqlMenuBody` (auch am Tab-Kopf; dort hatte das PL/SQL-Menü bisher keine Leiste).
- Texte in `SchemaViewText`, `ShellText`, `WorkspaceText` (Englisch und Deutsch).
- Tests: `FavoritesTests`, `RecentObjectStoreTests` (Core), `FavoriteServiceTests`, `RecentObjectsTests`, zwei Fälle in `QuickPaletteSearchTests` (UI).

## E2E
Noch offen.
