# Backlog

Ideen und Themen, die während der Arbeit auftauchen, aber nicht zum aktuellen Arbeitspaket gehören.
Geplante Versionen und Arbeitspakete stehen in `CLAUDE.md`; hier landet alles, was noch nicht eingeplant ist.

- CI-Pipeline anlegen, sobald das Hosting feststeht (Linux-Job: Core + Tests inkl. Testcontainers; Windows-Job: ganze Solution).
- Synonymketten auflösen (Synonym auf Synonym, auch über mehrere Schemas); bisher werden nur direkte Synonyme auf Tabellen/Views/MViews gezeigt.
- Gespeicherte Abfragen (`SavedQuery`) im Workspace, sobald es den freien SQL-Editor gibt.
- Oberfläche für die Workspace-Notizen (Feld `Workspace.Notes` existiert bereits).
- Shortcuts zum Wechseln der Workspaces (z. B. Ctrl+1…9) und eine Workspace-Übersicht pro Verbindung.
- Virtuelle FKs (aus WP-06 herausgenommen): manuell definierte Beziehungen für Schemas ohne Constraints, mit Dialog zum Anlegen/Bearbeiten. Empfehlung: pro Verbindung speichern, nicht pro Workspace (sie beschreiben das Schema). Modell: `FkSource.Manual`.
- FK-Vorschläge per Namenskonvention (`KUNDE_ID` → `KUNDE`/`KUNDEN`, deren einspaltiger PK), als „vermutet“ navigierbar oder als virtueller FK übernehmbar. Modell: `FkSource.Convention`.
- FK-Navigation über Spalten mit TIMESTAMP WITH TIME ZONE, BINARY_FLOAT/DOUBLE oder NUMBER mit mehr als 28 Stellen (bisher als „nicht möglich“ markiert).
- Export: vollständige LOB-Werte (CLOB/BLOB) für markierte Zeilen nachladen statt sie als NULL zu exportieren; „Alle Zeilen des Filters exportieren“ (nicht nur geladene/markierte).
- Native Dialoge: zuletzt verwendeten Export-Ordner merken.
- Spalten ausblenden (Variante C der Spaltensuche): Auswahl per Häkchen, pro Tab gespeichert, Vorlagen wie „nur FKs“. Entschieden: Export (Tabelle kopieren, CSV) nimmt dann nur die sichtbaren Spalten, INSERT immer alle.
- Spaltensuche auch in Spaltenkommentaren (`ColumnInfo.Comment`, seit v1.7 geladen) und – mit v3 – in C#-Property-Namen.
- Weitere Objekt-Details (Fortsetzung von v1.7, gleiches Muster: `ISchemaReader`-Methode + Integrationstest, weiterer Eintrag im Umschalter – ab etwa zehn Einträgen die seltenen unter „Mehr ▾“): Trigger (`ALL_TRIGGERS`, Rumpf als LONG, PL/SQL-Hervorhebung), Synonyme auf das Objekt (teilweise schon unter „Abhängigkeiten“), Berechtigungen (`ALL_TAB_PRIVS`/`ALL_COL_PRIVS`), Statistik (`ALL_TAB_STATISTICS`/`ALL_TAB_COL_STATISTICS`). Partitionen, Audit und „Access“ vorerst nicht. DDL: abhängige Objekte (Indizes, Kommentare, Grants) über `DBMS_METADATA.GET_DEPENDENT_DDL` ergänzen; Storage-Klauseln weglassen ginge nur über `SET_TRANSFORM_PARAM` (PL/SQL, passt nicht zur Lesesperre).
- Editieren (Fortsetzung von WP-09): PK-Werte bestehender Zeilen ändern (bewusst nur lesbar: ändert die Zeilenidentität, referenzierende FKs); weitere Typen (INTERVAL, TIMESTAMP WITH TIME ZONE, BINARY_FLOAT/DOUBLE, NUMBER > 28 Stellen); mehrere Zellen aus Excel einfügen; Ctrl+Z für einzelne ausstehende Zellen. LOBs kommen mit WP-10 (Editor-Dialog).
- Lange offene Schreib-Transaktionen: Die Statusleiste warnt ab 10 min, und der Keep-alive hält die Session offen. Optional Erinnerung als Benachrichtigung oder automatischer Rollback nach einstellbarer Zeit.
- Tastenkürzel für die Ansichten eines Tabs (z. B. Alt+1 … Alt+6); Ctrl+1 … 9 bleibt für das Wechseln der Workspaces reserviert.
