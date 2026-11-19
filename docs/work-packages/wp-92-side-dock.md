# WP-92 Dock rechts mit Spalten-Inspektor → noch ohne Release

Branch `wp/92-side-dock` (von `main`), 2026-11-19, noch ohne Release. ADR 0052.

## Planung
Aus der UX-Ideensammlung vom 2026-11-19 (`docs/roadmap.md`, „Pakete aus der UX-Ideensammlung“). Mockup `docs/images/mockups/wp-92-dock-inspector.png`; der Nutzer fand es gut.
- **Ein Dock für die rechte Seite** mit Icon-Leiste: Formular, offene Änderungen, Hilfe (WP-89), neu der Inspektor, später die Workspace-Übersicht (WP-39). Klick wechselt, zweiter Klick schließt; die Leiste zeigt die Zahl der offenen Änderungen.
- **Spalten-Inspektor:** folgt der fokussierten Zelle – Typ, C#-Property, Kommentar, Wert der Zeile mit Anzeigename, FK, Indizes mit der Position der Spalte, Statistik, Verwendung im Code mit Sprung; Links zur Spaltenansicht, Filter, Namen kopieren.
- Entschieden (Nutzer, 2026-11-19):
  - **Nur ein Panel zur Zeit** (1a). Formular und offene Änderungen gehen dann nicht mehr gleichzeitig.
  - **Das Dock gehört dem Fenster** (2), nicht dem Tab: Es bleibt beim Tabwechsel offen, sein Inhalt folgt dem aktiven Tab.
  - Claudes Empfehlungen zum Plan bestätigt („deine Empfehlungen passen“): **A** die Hilfe öffnet im Dock ohne eigenen SQL-Tab; **B** das Formular bleibt im SQL- und LINQ-Tab neben dem Ergebnis (nur von der Leiste gesteuert), über die volle Höhe später, falls es stört; **C** die Leiste ist immer sichtbar, solange eine Verbindung gezeigt wird.
- Im Plan korrigiert: Eine Migration der Workspaces ist nicht nötig – das Format bleibt, „offen“ und „Breite“ des Formulars werden nur nicht mehr gelesen.

## Gebaut
- **Core:**
  - `AppSettings.Dock` (`DockPanel?`: None, Inspector, Form, Changes, Help) und `DockWidth`; `ShownDock`/`ShownDockWidth` nehmen Einstellungen vor dem Dock auf (`ChangesPanelOpen`, `ChangesPanelWidth`), beide `[JsonIgnore]`.
  - `ISchemaReader.GetColumnStatisticsAsync` (`ALL_TAB_COL_STATISTICS`, ein Roundtrip je Tabelle) mit `ColumnStatistics`; `SchemaCache` reicht durch (nicht gecacht).
  - `IndexAdvice.PositionsOf` mit `IndexPosition` (Stelle der Spalte je Index, `Leads` = vorn, sichtbar, nutzbar; diese zuerst).
  - Aktion `ShortcutAction.ToggleInspector` ohne Standardtaste; Hinweis der Hilfe-Aktion nennt das Dock.
  - `FormTabState`: `Open`/`Width` nur noch für ältere Dateien, geschrieben wird nur „Leere ausblenden“.
- **UI-Zustand:**
  - `ShellState.Dock` mit `ToggleDock`/`ShowDock`/`CloseDock` (schließt nur, wenn der Panel noch offen ist), `RestoreDock` (unbekannter Wert → zu), `DockChanged` (die Shell speichert `Dock` und `ChangesPanelOpen`), `HelpTopic`; `ChangesOpen`/`ToggleChanges`/`CloseChanges` darüber. `ShowScriptHelp` öffnet nur noch das Dock.
  - `DockPanels`: Reihenfolge der Leiste, `ShowsIn`/`Unavailable` (Formular in Tabellen-Tabs mit Datenansicht, SQL- und LINQ-Tabs; Inspektor nur in der Datenansicht; Änderungen und Hilfe überall), Kürzel und Beschriftung je Panel.
  - `PanelSizes.DockWidth` (`--dock-width` am App-Root, auch aus `ChangesPanelWidth`).
  - `ColumnInspection` (reine Klasse: Spalte, PK, Beziehungen in beide Richtungen, Indizes, Statistik, `NoLeadingIndex`) und `ColumnInspectorData` (je Tab in `TableTab.Inspection`, nicht gespeichert).
  - Entfernt: `IFormTab.FormOpen`/`FormWidth`, `SqlTab.HelpOpen`, `TabCommand.ShowHelp`.
- **Komponenten:**
  - `Shell/DockRail` (Knöpfe mit `aria-pressed`, Tooltip mit Kürzel und Grund, Zahl der offenen Änderungen bis „99+“; nimmt den Fokus nicht), `Shell/DockResizeHandle` (eine Breite für alle, speichert `DockWidth` und leert `ChangesPanelWidth`).
  - Shell: rechts neben den Seiten der Panel der Shell (Änderungen, Hilfe) und die Leiste.
  - `TabView`: `.tabview-split` mit Formular bzw. Inspektor rechts über die volle Höhe, nur im aktiven Tab und in der Datenansicht; verlässt das Formular den Tab, nimmt `RowFormPanel.TakeTypedValues` getippte Werte als ausstehende Änderungen.
  - `SqlResultGrid`/`ResultFooter`/`SqlView`/`LinqView`: Formular über das Dock, nur im aktiven Tab.
  - `FormPanelFrame`, `ChangesPanel`, `HelpPanel`: `.dock-panel` mit `DockResizeHandle`; `SqlView` ohne eigenes Hilfe-Panel (Knopf schaltet das Dock).
  - `Grid/ColumnInspector`: liest Indizes und Statistik einmal je Tab (Explorer-Session, abbrechbar, Knopf „neu lesen“), stößt wie die Spaltenansicht den Index der Code-Verwendung an, folgt `FocusChanged`/`RowsChanged` des Grids; „Nach diesem Wert filtern“ legt eine Filterzeile an (`=` bzw. `IS NULL`, nicht im SQL-Modus und in der Zeitreise).
  - Icons `Inspector` (Lucide `scan-search`), `OpenChanges` (`list-checks`); `Form` und `Help` für die Leiste mitbenutzt.
  - CSS: `.dock-panel`, `.dock-rail`, `.dock-btn` (aktiv, nicht verfügbar), `.dock-count`, `.tabview-split`, `.inspector-*`, `.chip-note`; die alten Breiten von `.form-panel`/`.changes-panel` entfernt.
- **Texte:** `ShellText.Dock_*`, `GridText.Inspector_*`, `SettingsText` (Aktion und Hinweis des Inspektors, Hinweise zu Änderungen und Hilfe), `SchemaViewText.Tab_FormTitle*` – Englisch und Deutsch.
- **Tests:** `DockTests` (Öffnen, Ersetzen, Schließen nur durch den eigenen Knopf, Ereignisse; Start mit unbekanntem Wert; Panels je Tab-Art), `ColumnInspectionTests`, `ObjectDetailsTests.A_column_s_indexes_list_its_place_the_ones_it_leads_first` (Core), `SettingsStoreTests.The_dock_takes_the_change_overview_of_settings_before_it` (und nullable Enums im Round-Trip), `FormTabsTests` (nur „Leere ausblenden“; alte Datei mit offenem Formular), `ScriptTabsTests.The_help_on_scripts_opens_in_the_dock_beside_any_tab`, `PanelSizesTests` (Dock-Breite), Integration `ObjectDetailsTests.Column_statistics_come_per_column_once_the_table_is_analyzed` (neue Tabelle `OD_STAT`, analysiert).

## Offen (Backlog)
- Formular über die volle Höhe auch im SQL- und LINQ-Tab (B): `SqlView` müsste das Formular des gezeigten Ergebnisses neben Editor und Ergebnis legen.
- Inspektor im Ergebnis von SQL-Editor und LINQ-Konsole für Spalten mit bekannter Herkunft (WP-48).
- Zwei Panels übereinander (z. B. Formular und Änderungen), falls „eins zur Zeit“ im Alltag stört.
- „Spaltenansicht“ aus dem Inspektor springt noch nicht zur Spalte (die Spaltenansicht hat keinen Sprung von außen).
- Der Ansichtsschalter eines Tabellen-Tabs hat zehn Einträge und bricht neben dem Dock in zwei Zeilen um; „Mehr ▾“ für die seltenen (`docs/architecture.md` 2) wäre die Alternative.

## E2E
Eigene Testinstanz (`--lang=de`, hell und dunkel, eigener Datenordner) gegen einen eigenen Container mit dem Beispielschema (`ferret-e2e-wp92`, Port 1532, Schema analysiert), Verbindung mit verknüpftem `samples`-Projekt. Klicks mit echter Windows-Eingabe (Probe bestanden), Tasten und Auslesen per CDP.
- Leiste: Inspektor, Formular, Hilfe per echtem Klick; Ctrl+Shift+U öffnet die Änderungen. Ein Panel zur Zeit, `aria-pressed` folgt, Zahl der ausstehenden Änderungen an der Leiste.
- Inspektor an KUNDE_ID, AUFTRAG_ID und STATUS (Klick in die Zelle): Typ, C#-Property, Wert, Beziehungen (ausgehend und eingehend), Indizes mit „Spalte 1 von 1“, Statistik, Warnung ohne Index (STATUS), Verwendung; „Nach diesem Wert filtern“ legt `KUNDE_ID = 29` an.
- Formular im Dock: im Tabellen-Tab über die volle Höhe, im SQL-Tab neben dem Ergebnis (Entscheidung B). Getippter, nicht bestätigter Wert im Formular, dann „+ SQL“ (echter Klick): der Wert ist eine ausstehende Änderung (Statusleiste „1 ausstehend“).
- PL/SQL-Tab: Inspektor und Formular als nicht verfügbar markiert, Grund im Tooltip; die Wahl bleibt.
- Breite: Ziehen am Rand mit echter Maus 420 → 520, `dockWidth` in `settings.json`; nach dem Neustart öffnen Dock und Breite wie verlassen, Tabs werden wiederhergestellt.
- Log ohne `[ERR]`/`[WRN]`. Integrationstest `ObjectDetailsTests` (Statistik) gegen einen Testcontainer grün.

Befunde (alle behoben, mit erneutem Lauf geprüft):
1. Die Werkzeugleiste eines Tabellen-Tabs wurde neben dem Dock abgeschnitten („+ Zeile“, „Formular“, „Spalte“ und der Ansichtsschalter ab „Verwendung“ unsichtbar; Teile schienen hinter der Leiste durch) – vorher stand das Formular nur neben dem Grid. Jetzt bricht die Leiste um, der Ansichtsschalter nur, wenn er nicht in eine Zeile passt.
2. „Verwendung im Code“ sagte „Kein Code nutzt die Property …“ und listete darunter dreimal dieselbe Mapping-Zeile. Jetzt nur Anwendungscode, jede Stelle einmal; Tests und Mapping als Zahl mit Link zur Ansicht „Verwendung“.
3. Beziehungen zeigten „Constraints“ als Herkunft (die Beschriftung des Diagramm-Schalters). Jetzt keine Marke für Constraints, „aus C#-Modell“ wie im Zellmenü.
4. Der aktive Leistenknopf zeigte eine Klammer (Inset-Schatten mit Rundung) – nur noch Fläche und Farbe.
5. Ein neuer SQL-Tab ohne Ergebnis zeigte „Formular“ aktiv und nichts. Jetzt gilt das Formular dort als nicht verfügbar (SQL-Tab nur mit gezeigter Abfrage, LINQ-Tab mit Lauf).
6. Ohne Index standen „Kein Index enthält diese Spalte“ und die Warnung untereinander – nur noch die Warnung.
7. Im Kopf der Hilfe stand das ✕ direkt hinter dem Titel und der Kopf war niedriger als bei den anderen Panels.
