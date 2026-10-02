# Backlog

Ideen und Themen, die während der Arbeit auftauchen, aber nicht zum aktuellen Arbeitspaket gehören.
Geplante Versionen und Arbeitspakete stehen in `CLAUDE.md`; hier landet alles, was noch nicht eingeplant ist.

- CI-Pipeline anlegen, sobald das Hosting feststeht (Linux-Job: Core + Tests inkl. Testcontainers; Windows-Job: ganze Solution).
- Grid-Upgrade: Auf dem Entwicklungsrechner ist bereits eine DevExpress-Paketquelle eingerichtet – prüfen, ob eine Lizenz für das DevExpress-WPF-Grid vorhanden ist (siehe CLAUDE.md Backlog).
- Synonymketten auflösen (Synonym auf Synonym, auch über mehrere Schemas); bisher werden nur direkte Synonyme auf Tabellen/Views/MViews gezeigt.
- Gespeicherte Abfragen (`SavedQuery`) im Workspace, sobald es den freien SQL-Editor gibt.
- Oberfläche für die Workspace-Notizen (Feld `Workspace.Notes` existiert bereits).
- Shortcuts zum Wechseln der Workspaces (z. B. Ctrl+1…9) und eine Workspace-Übersicht pro Verbindung.
- Virtuelle FKs (aus WP-06 herausgenommen): manuell definierte Beziehungen für Schemas ohne Constraints, mit Dialog zum Anlegen/Bearbeiten. Empfehlung: pro Verbindung speichern, nicht pro Workspace (sie beschreiben das Schema). Modell: `FkSource.Manual`.
- FK-Vorschläge per Namenskonvention (`KUNDE_ID` → `KUNDE`/`KUNDEN`, deren einspaltiger PK), als „vermutet“ navigierbar oder als virtueller FK übernehmbar. Modell: `FkSource.Convention`.
- FK-Navigation: „Zurück“-Historie über Sprünge; Tabs derselben Tabelle im Tab-Titel unterscheidbar machen (z. B. Kurzform des Filters).
- FK-Navigation über Spalten mit TIMESTAMP WITH TIME ZONE, BINARY_FLOAT/DOUBLE oder NUMBER mit mehr als 28 Stellen (bisher als „nicht möglich“ markiert).
- Export: vollständige LOB-Werte (CLOB/BLOB) für markierte Zeilen nachladen statt sie als NULL zu exportieren; „Alle Zeilen des Filters exportieren“ (nicht nur geladene/markierte).
- Verbindungsabbruch proaktiv erkennen (Keep-alive-Ping im Leerlauf), statt erst beim nächsten Statement.
- Native Dialoge: zuletzt verwendeten Export-Ordner merken.
