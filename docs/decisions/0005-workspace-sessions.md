# ADR 0005: Eine Oracle-Session pro Workspace, Schema über eine eigene Explorer-Session

- Status: akzeptiert
- Datum: 2026-10-01

## Kontext

Workspaces (WP-05) sind Arbeitskontexte auf einer Verbindung. Ab v2 hält jeder Workspace eine eigene Transaktion; zwei Workspaces sollen dieselbe Tabelle unabhängig ansehen und später bearbeiten können. Dafür braucht jeder Workspace eine eigene Connection. Offen war, wo das Schema geladen wird, wann die Sessions geöffnet werden und was mit Workspaces passiert, die gerade nicht angezeigt werden.

## Entscheidung

- **Explorer-Session:** `ActiveConnection` behält eine eigene Session (ACTION „Explorer“) nur für den `SchemaCache` (Objektliste, FKs, Spalten-Details). Schema-Abfragen blockieren damit nie die Daten-Session eines Workspace, und der Cache ist für alle Workspaces derselben Verbindung gemeinsam.
- **Workspace-Sessions:** `WorkspaceManager` öffnet pro offenem Workspace eine eigene Session (ACTION = Workspace-Name), und zwar beim ersten Datenzugriff. Ein fehlgeschlagenes Öffnen wird nicht gecacht. Umbenennen setzt ACTION neu.
- **Inaktive Workspaces behalten ihre Session**, solange sie offen sind (in v2 hängt daran die Transaktion). Erst Schließen des Workspace, Trennen oder Beenden der App schließt sie. Eine Verbindung belegt damit „Anzahl offener Workspaces + 1“ Sessions.
- **Tabs aller offenen Workspaces bleiben gemountet** (wie die Tabs in WP-04), damit Grid-Zustand und Scrollposition beim Wechsel erhalten bleiben. Wiederhergestellte Tabs werden erst beim ersten Anzeigen gemountet, sonst würde ein Neustart alle Tabs aller Workspaces gleichzeitig abfragen.
- **Persistenz:** eine JSON-Datei pro Workspace. Tab-Änderungen werden nach 1 s gesammelt gespeichert (nur wenn sich etwas geändert hat); Anlegen, Schließen, Wiederöffnen, Trennen und Beenden speichern sofort. Der letzte offene Workspace lässt sich nicht schließen, so gibt es nie einen Zustand ohne Workspace.

## Konsequenzen

- Bei vielen offenen Workspaces viele Sessions; auf Datenbanken mit knappem `SESSIONS_PER_USER`-Limit kann das stören. Abhilfe ist dann das Schließen von Workspaces.
- Mehrere gemountete AG-Grid-Instanzen kosten Speicher in der WebView; bisher unkritisch.
- ODP.NET (23.26) verliert die Session, wenn ACTION/CLIENT_INFO Nicht-ASCII enthalten (ORA-12537). Workspace-Namen werden deshalb für ACTION nach ASCII transliteriert („Prüfung“ → „Pruefung“); in der Oberfläche bleibt der Name unverändert.
