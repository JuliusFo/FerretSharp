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
- Spaltensuche auch in Spaltenkommentaren (`ALL_COL_COMMENTS`, bisher nicht geladen) und – mit v3 – in C#-Property-Namen.
- Objekt-Details wie in KeepTool HORA (alles lesend über `ALL_*`, je Bereich eine `ISchemaReader`-Methode mit Integrationstest, lazy beim ersten Öffnen, immer nach Owner + Name gefiltert). Darstellung: als weitere Einträge im Umschalter neben „Daten“ (flach, z. B. Daten | Spalten | Constraints | Indizes | Abhängigkeiten | DDL); Kopfzeile über allen Nicht-Daten-Ansichten mit Kommentar, Status, Zeilen laut Statistik, letzter DDL. Wertvollste Teile zuerst:
  - Übersicht: `ALL_OBJECTS` (angelegt, letzte DDL, Status), `ALL_TABLES` (Zeilen laut Statistik, Tablespace, partitioniert), Tabellenkommentar aus `ALL_TAB_COMMENTS`.
  - Spaltenkommentare (`ALL_COL_COMMENTS`), Virtual/Hidden (`ALL_TAB_COLS`), Default on Null. Kommentare danach auch in der Spaltensuche.
  - Constraints: PK/UK/FK plus Check-Constraints (Bedingung als LONG), Status (enabled/validated), deferrable.
  - Indizes: `ALL_INDEXES`, `ALL_IND_COLUMNS`, `ALL_IND_EXPRESSIONS` (funktionsbasiert, LONG); Hinweis auf FK-Spalten ohne Index.
  - Abhängigkeiten: `ALL_DEPENDENCIES` in beide Richtungen (was nutzt das Objekt, wer nutzt es).
  - DDL: `SELECT DBMS_METADATA.GET_DDL(…) FROM DUAL` (fremde Schemas brauchen `SELECT_CATALOG_ROLE`).
  - Ungültige Objekte im Explorer rot markieren (`ALL_OBJECTS.STATUS = 'INVALID'` in der Tabellenliste).
  - Fehlende Rechte von „gibt es nicht“ unterscheiden (leere Ergebnisse je nach DB-User).
  - Später: Trigger (`ALL_TRIGGERS`, Rumpf als LONG, PL/SQL-Hervorhebung), Synonyme auf das Objekt, Berechtigungen (`ALL_TAB_PRIVS`/`ALL_COL_PRIVS`), Statistik (`ALL_TAB_STATISTICS`/`ALL_TAB_COL_STATISTICS`). Partitionen, Audit und „Access“ vorerst nicht.
