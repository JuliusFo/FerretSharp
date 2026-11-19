# ADR 0052: Ein Dock rechts – ein Panel zur Zeit, für das ganze Fenster

- Status: akzeptiert
- Datum: 2026-11-19
- Paket: WP-92 (`docs/work-packages/wp-92-side-dock.md`)

## Kontext

Die rechte Seite hatte drei Panels mit drei Mechaniken: das Formular in jedem Tab (`TabView` bzw. `SqlResultGrid`, offen und Breite je Tab gespeichert), die offenen Änderungen neben allen Tabs (`ChangesPanel`, offen und Breite in den Einstellungen) und die Hilfe neben dem SQL-Editor (`Tab.HelpOpen`, im Wechsel mit Verlauf und Skripten). Die Workspace-Übersicht aus WP-39 und ein Inspektor der Spalte wollten ebenfalls dorthin. Aus der UX-Ideensammlung vom 2026-11-19; Mockup `docs/images/mockups/wp-92-dock-inspector.png`.

## Entscheidung (Entscheidungen des Nutzers, 2026-11-19)

- **Ein Dock mit einer Leiste** am rechten Rand (`DockRail`): ein Knopf je Panel – Spalten-Inspektor, Formular, offene Änderungen (mit Zahl), Hilfe. Klick öffnet, ein zweiter schließt. Die Leiste ist immer sichtbar, solange eine Verbindung gezeigt wird.
- **Nur ein Panel zur Zeit.** Formular und offene Änderungen gehen damit nicht mehr gleichzeitig; zwei Panels übereinander wären eine spätere Ergänzung.
- **Das Dock gehört dem Fenster, nicht dem Tab:** Es bleibt beim Tabwechsel offen, sein Inhalt folgt dem aktiven Tab. Der gewählte Panel und **eine** Breite für alle stehen in den Einstellungen (`AppSettings.Dock`, `DockWidth`; die Breite als `--dock-width` am App-Root). Ein Tab ohne passenden Inhalt (Formular in einem PL/SQL-Tab, Inspektor außerhalb der Datenansicht) zeigt keinen Panel; die Leiste behält die Wahl, markiert sie als hier nicht verfügbar und nennt den Grund im Tooltip.
- **Inhalte bleiben, wo ihre Daten sind:** Formular und Inspektor brauchen Grid und Struktur des Tabs und werden vom Tab gezeichnet – in Tabellen-Tabs als rechte Spalte über die volle Höhe (`.tabview-split`), im SQL- und LINQ-Tab weiter neben dem Ergebnis (jedes Ergebnis hat sein Formular). Offene Änderungen und Hilfe gehören keinem Tab und kommen von der Shell. Alle sitzen am selben Ort mit derselben Breite (`.dock-panel`). Kein Portal, kein Umhängen von DOM.
- **Nur der aktive Tab zeichnet:** Tabs bleiben gemountet; ein Formular in jedem besuchten Tab würde je Zeile lesen und zählen. Verlässt das Formular den Tab (anderer Tab, anderes Panel, andere Ansicht), werden getippte Werte zu ausstehenden Änderungen wie beim Zeilenwechsel (`RowFormPanel.TakeTypedValues`).
- **Die Hilfe braucht keinen SQL-Tab mehr** (Nachtrag zu ADR 0051): Sie öffnet im Dock neben jedem Tab.
- **Spalten-Inspektor:** folgt der fokussierten Zelle; Typ, C#-Property, Kommentar, Wert der Zeile mit Anzeigename, Beziehungen in beide Richtungen, Indizes mit der Stelle der Spalte (`IndexAdvice.PositionsOf`), Statistik (`ISchemaReader.GetColumnStatisticsAsync`, neu, `ALL_TAB_COL_STATISTICS`) und Verwendung im Code. Indizes und Statistik liest er einmal je Tab (je ein Roundtrip auf der Explorer-Session), wenn er gezeigt wird; ein Knopf liest neu.

## Folgen

- Kein neues Dateiformat: Ältere Einstellungen öffnen die Änderungsübersicht im Dock, wenn sie offen war, mit ihrer Breite (`ShownDock`, `ShownDockWidth`); `ChangesPanelOpen` wird weiter mitgeschrieben, damit eine ältere Version sie findet. Workspace-Dateien behalten `FormTabState`, gelesen wird nur noch „Leere ausblenden“; ein offenes Formular aus einer Datei vor 3.72 öffnet nicht wieder.
- Eine neue Aktion „Spalten-Inspektor zeigen/ausblenden“ ohne Standardtaste; Alt+Enter, Ctrl+Shift+U und „Hilfe: SQL-Skripte“ steuern das Dock.
- Die Workspace-Übersicht aus WP-39 kommt als weiterer Panel ins Dock, ein weiteres Hilfe-Thema als weiterer Inhalt der Hilfe – keine neue Mechanik.
- Formular über die volle Höhe auch im SQL- und LINQ-Tab hieße, `SqlView` umzubauen (Formular des gezeigten Ergebnisses neben Editor und Ergebnis); bewusst nicht in WP-92 (Backlog).
