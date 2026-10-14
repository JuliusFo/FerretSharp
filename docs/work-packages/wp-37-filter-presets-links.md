# WP-37 Filter-Vorlagen und Links → Release 3.35.0 (Teil 1: Vorlagen; Teil 2: Links offen)
Branch `wp/37-filter-presets-links`, gestartet 2026-10-14. Ausgewählt mit dem Nutzer am 2026-10-14 (Vorlagen und Links je „richtig cool“). Planung und Entscheidungen für Teil 2 (Links) stehen weiter in `docs/roadmap.md` und kommen mit dessen Release hierher.

## Entscheidungen des Nutzers (2026-10-14, beim Start, nach den Vorschlägen)
- **Speicherort:** lokal im Datenordner (`filter-presets.json`), Schlüssel ist der **Tabellenname ohne Owner** wie bei den Anzeigespalten (ADR 0022) – eine Vorlage gilt auf allen Verbindungen. Teilen mit den Kollegen später (Backlog): persönliche Vorlagen mit festen IDs gehören nicht in die geteilte Datei, und „lokal oder geteilt“ je Vorlage bräuchte mehr UI und Konfliktfälle.
- **Anwenden ersetzt** die Filterzeilen; „+“ neben jeder Vorlage hängt ihre Zeilen an. Die Sortierung ersetzt eine Vorlage nur, wenn sie eine hat.
- **Zwei Releases:** erst die Vorlagen (nur Core und UI), dann die Links (ADR, Registrierung, Übergabe an die laufende Instanz); die Links nutzen das Filtermodell, wie die Vorlagen es speichern.
- Für Teil 2 schon entschieden (Roadmap): Ein Link öffnet im zuletzt aktiven Workspace der Verbindung, immer als neuer Tab, nach einer Bestätigung; Links dürfen auf Prod Werte enthalten.

## Gebaut (Teil 1)
- **Core** (`Query/FilterPresets.cs`): `FilterPreset(Id, Name, Table, Filters, Sorts)` – das Filtermodell (`FilterCondition`, `SortSpec`) unverändert, also weiter 1:1 als LINQ übersetzbar. `FilterPresets.Apply(preset, table, current, append)` liefert Zeilen, Sortierung und fehlende Spalten: Zeilen auf Spalten, die die Tabelle nicht hat, kommen deaktiviert (sie zeigen, was die Vorlage wollte, und blockieren das Anwenden der anderen nicht); Sortierungen auf solchen oder nicht sortierbaren Spalten (LOB) entfallen, ohne gültige Sortierung bleibt die des Tabs. Namen: höchstens 60 Zeichen, je Tabelle eindeutig ohne Groß-/Kleinschreibung.
- **Datei** (`FilterPresetStore`, `AppPaths.FilterPresetsFile`): versioniert über `VersionedJsonFile`, geschrieben über `AtomicJsonFile`, Enums als Text. Kaputt oder neuer → Fehler, nie überschrieben (sie hält die Vorlagen aller Tabellen); von Hand auf `null` gesetzte Einträge und Listen werden ausgelassen bzw. leer (Regel in `docs/architecture.md` 1.7).
- **UI:** `FilterPresetService` (Singleton, nicht verbindungsgebunden): liest einmal; scheitert das Lesen, wird **nichts geschrieben**, bis ein neuer Versuch (Menü öffnen) gelingt – sonst ersetzte die erste neue Vorlage eine nur kurz gesperrte Datei. `PresetMenu` („Vorlagen ▾“ in der Filterleiste, mit und ohne Filterzeilen, ein `PopoverMenu`): Klick wendet an, „+“ hängt an, ✎ benennt um, ✕ löscht nach Bestätigung, Tooltip je Vorlage mit Zeilen und Sortierung (`FilterPresetText`). „Als Vorlage speichern…“: nur gültige Zeilen (Fehler wie beim Anwenden an den Zeilen), Inhalt im Dialog, „Sortierung mitspeichern“ vorausgewählt, wenn sortiert; gleicher Name → „Vorlage ersetzen?“ (die Id bleibt). Ausgegraut ohne Filterzeilen und Sortierung.
- **Sortierung setzen:** `grid.js setSorts` über `applyColumnState` (mit `defaultState: { sort: null }`); AG Grid lädt dann selbst ab Zeile 0 – `TabView.RefreshAsync(sorts)` ruft deshalb **nicht** zusätzlich `refresh`. Im Log genau eine Abfrage je angewendeter Vorlage (E2E).
- Filterleiste: Eine Zeile auf einer fehlenden Spalte zeigt „Gibt es in dieser Tabelle nicht“ und ihren Operator (sonst blieb die Auswahl leer, im E2E gefunden); beim Anwenden eine Meldung mit den fehlenden Spalten.
- Tests: Core 1919 (`FilterPresetsTests`: Schlüssel, Ersetzen/Anhängen, fehlende und nicht sortierbare Spalten, Namen, Hin- und Rückweg, Handänderungen, kaputte/neuere Datei), UI 326 (`FilterPresetServiceTests`: über alle Schemas, Ersetzen bei gleichem Namen, Umbenennen, Löschen, ungültiger Name, nichts speichern ohne gelesene Datei, Tooltip), Integration 208.

## E2E (2026-10-15)
Eigener Container `ferret-e2e-wp37` (Port 1537, danach entfernt), eigene Instanz mit `--data-dir`, eigenem WebView2-Ordner (eine Instanz des Nutzers lief), zwei Verbindungen auf denselben Container, Passwort ohne Speichern; deutsch/dunkel, hell per Emulation, englisch/dunkel. Klicks mit echter Windows-Eingabe (`SendInput`), Tippen per CDP.
- Vorab angelegte Datei mit einer Vorlage auf `GIBTS_NICHT`: Menü zeigt sie, Anwenden filtert nach NAME, die zweite Zeile deaktiviert mit Hinweis, Meldung erscheint.
- Sortieren nach UMSATZ ↓, „Als Vorlage speichern…“ (Fokus im Namensfeld, Enter speichert), Leeren und Sortierung zurück, Vorlage anwenden → Zeilen, Pfeil im Kopf und Fußzeile „Sortierung: UMSATZ ↓“, eine Abfrage.
- „+“ hängt an (4 Zeilen); Umbenennen auf einen vergebenen Namen in anderer Schreibweise abgelehnt, Fehler verschwindet beim Tippen; Speichern unter vorhandenem Namen fragt („Ersetzen“) und behält die Id; Löschen nach Bestätigung; Esc schließt das Menü.
- Zweite Verbindung: dieselbe Vorlage für KUNDEN, Anwenden mit einer Abfrage. Nach Neustart stellt der Workspace auch die deaktivierten Zeilen wieder her.
- Gefunden und behoben: leere Operator-Auswahl bei fehlender Spalte; die Meldung nannte immer auch „die Sortierung danach entfällt“ (jetzt nur „hat keine Spalte …“, den Rest zeigt die Zeile).
- Log ohne `[ERR]`/`[WRN]`, kein Eintrag im Credential Manager.

## Offen
- Teil 2: Links (`ferretsharp://`), siehe `docs/roadmap.md`.
- Anzeige, welche Vorlage angewendet ist und ob die Zeilen seitdem geändert wurden (Frage in der Roadmap); Teilen mit den Kollegen (Backlog).
