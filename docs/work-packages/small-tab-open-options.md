# Klein: Ansicht und Zeilen beim Öffnen einer Tabelle → Release offen

Branch `feat/tab-open-options` (von `main`), 2026-11-05.

## Anlass
Idee des Nutzers: zwei Einstellungen – auf welchem Unter-Tab eine neu geöffnete Tabelle startet (bisher immer „Daten“) und ob die Datenansicht ihre Zeilen sofort lädt. Bei der Bewertung fiel auf: Das Grid war immer gemountet (nur per CSS versteckt), ein Tab auf „Spalten“ las seine erste Seite also trotzdem.

Entscheidungen des Nutzers (2026-11-05):
- Beide Einstellungen **global**, nicht je Verbindung (je Verbindung → Backlog).
- Wiederhergestellte Tabs laden ihre Zeilen wie bisher (sie waren geladen und haben ihre Filter; ohnehin lädt nur der aktive Tab).
- Alle Unter-Tabs als Wahl, in der Reihenfolge des Umschalters (Vorschlag, angenommen); „Verwendung“ fällt ohne verknüpftes C#-Projekt auf „Daten“ zurück.
- Kleines Thema statt eigenem Paket (kein ADR: das Grid folgt dem Muster der Detailansichten, „nicht geladen“ ist flüchtig).

## Gebaut
- **Grid erst beim ersten Zeigen** (`TableTab.DataShown`, nicht gespeichert): `TabView` mountet Filterleiste, Grid, Formular und Fußzeile erst, wenn die Datenansicht einmal gezeigt war, und behält sie dann (auch beim erneuten Mounten nach Workspace- oder Verbindungswechsel). Ein Tab, der auf einer anderen Ansicht öffnet – Link auf „Spalten“, Synonyme oder Rechte aus dem Objektmenü, Designer –, liest keine Zeilen mehr im Hintergrund.
- **„Ansicht beim Öffnen einer Tabelle“** (`AppSettings.OpenView`, `TabMode`, Standard `Data`): `ShellState.OpenTable` ohne `mode` öffnet einen **neuen** Tab auf der Ansicht der Einstellung (`TableOpening.View`, ein unbekannter Wert → Daten) und merkt das (`TableTab.ViewFromSetting`, jedes Setzen von `Mode` löscht es). `TabView` fällt dann ohne verknüpftes Projekt von „Verwendung“ auf „Daten“ zurück (`TableOpening.Shown`). Ein offener Tab behält seine Ansicht; FK-Sprung (`OpenFiltered`), Link, Duplikat und Wiederherstellen bringen ihre eigene mit. Die Einstellungen kommen über `ShellState.CurrentSettings` (Shell.razor reicht `AppSettingsService.Current` durch).
- **„Zeilen beim Öffnen laden“** (`AppSettings.LoadRows`, `RowLoadMode.Immediately`/`OnRequest`): Ein neuer Tab aus `OpenTable` bekommt `TableTab.RowsDeferred` (nicht gespeichert; das Duplikat übernimmt es). Das Grid entsteht normal, `FerretGrid.GetRows` antwortet aber ohne Abfrage mit einer leeren Seite (`lastRow` 0), solange `TableTab.StillDeferred` gilt: erste Zeile und Sortierung wie im Tab. Ein Klick auf einen Spaltenkopf kommt mit anderer Sortierung an (vor `OnSortChanged`, `ui-grid.md`) und lädt; „Laden“ in der Fußzeile, F5, Filter anwenden und Filter-Vorlagen gehen über `TabView.RefreshAsync`, das den Merker löscht. Ohne Änderung an `grid.js`: Ein leeres Ergebnis kennt das Infinite Row Model schon (leerer Filter), `refresh` lädt danach neu.
- Fußzeile: „Zeilen nicht geladen“ mit Knopf „Laden“ (Tooltip mit der Taste für Neu laden); „Zeilen zählen“ geht schon vorher.
- Einstellungen › Datenansicht (`SettingKey.OpenView`/`LoadRows`, Einträge in `SettingsCatalog`, Werte in `SettingsOptions`), Texte Englisch und Deutsch. „Zeilen beim Öffnen laden“ ist eine `SettingsChoice`; die Ansicht ein natives `<select>` (`.settings-select`): Zehn Knöpfe passten bei 1440 px gerade noch, drückten den Hinweis aber in eine schmale Spalte (im E2E gesehen).
- Tests: `SettingsStoreTests` (Standardwerte), UI `TableOpeningTests` (unbekannte Ansicht, Rückfall „Verwendung“, `StillDeferred`), `ShellStateTabsTests` (neuer Tab nach Einstellung, offener Tab, Ansicht verlangt, FK-Sprung, Duplikat).

## E2E
2026-11-05, eigener Container (`New-SampleDb.ps1 -Name ferret-e2e-tabopen -Port 1534`, danach entfernt), `--lang=de`, Abfragen über die Zeilen `Rows 0+ of …` im Log gezählt. Geprüft: Ansicht „Spalten“ öffnet ohne Grid und ohne Abfrage, „Daten“ lädt einmal, Zurückwechseln fragt nicht erneut; „Auf Anforderung“ – „Laden“ (echte Maus) und Kopfklick (echte Maus, eine Abfrage in der neuen Sortierung), F5 und Strg+Enter (CDP-Tasten) laden, „Zeilen zählen“ vorher lässt das Grid leer und fragt keine Zeilen ab, ein FK-Sprung lädt sofort; nach Neustart lädt der wiederhergestellte, nie geladene aktive Tab; „Verwendung“ ohne Projekt öffnet „Daten“; Ansicht „Indizes“ mit „Auf Anforderung“ zusammen; Einstellungssuche findet beide Zeilen. Log ohne `[ERR]`/`[WRN]`.
- Gefunden: die zehn Knöpfe der Ansicht (oben, jetzt Auswahlfeld).
- Werkzeug: `Start-TestInstance.ps1` sah den Debug-Port der eigenen Instanz nicht als belegt (`Get-NetTCPConnection` fand keinen Listener, Eigentümer 0) und beendete die eigene Instanz als „fremd“; gestartet wie im Skript, nur ohne diese Prüfung (einzige FerretSharp-Instanz, Pfad im Scratchpad geprüft). Backlog.
- Offene UX-Frage: Das Grid ohne Zeilen ist leer bis auf die Köpfe, nur die Fußzeile sagt „Zeilen nicht geladen“. Reicht das, oder ein Hinweis in der Mitte des Grids?
