# ADR 0008: Freischalten pro Workspace, LOBs als ganzer Wert

- Status: akzeptiert
- Datum: 2026-10-05

## Kontext

Profile mit „Schreibgeschützt“ (Prod voreingestellt) laufen seit WP-08 in einer `SET TRANSACTION READ ONLY`-Transaktion (ADR 0006): Oracle selbst lehnt jede Änderung ab. WP-10 soll das gezielt aufheben können, ohne den Schutz unbemerkt zu verlieren. Außerdem fehlte das Bearbeiten von CLOB/BLOB (WP-09 hat LOBs ausgenommen; das Grid kennt nur Vorschau und Länge).

## Entscheidung

**Freischalten** (Entscheidungen des Nutzers):

- **Pro Workspace**, nicht pro Verbindung: Jeder Workspace hat seine eigene Session, nur dessen Session verlässt die Read-only-Transaktion (`OracleSession.StopReadOnlySnapshotsAsync`). Andere Workspaces derselben Verbindung bleiben von Oracle geschützt.
- **Gilt bis zum manuellen Sperren**, wird aber nie gespeichert: Schließen des Workspaces, Trennen und Neu verbinden sperren wieder (`WorkspaceManager` hält die Menge nur im Speicher).
- **Bestätigung:** auf Prod durch Eintippen des Verbindungsnamens (wie GitHub beim Löschen), bei anderen schreibgeschützten Profilen ein einfacher Dialog.
- **Sperren** setzt voraus, dass keine schreibende Transaktion offen ist: vorher dieselbe Abfrage wie beim Trennen (Committen/Verwerfen/Abbrechen). Kann eine Session nicht wieder gesperrt werden (Verbindung weg), wird sie verworfen; die nächste öffnet gesperrt.
- Nach dem Freischalten laden die Tabs des Workspaces neu: Sie zeigten den Read-only-Snapshot.
- Sichtbarkeit: gestreiftes Badge „PROD · FREIGESCHALTET“, offenes Schloss am Workspace-Chip, „freigeschaltet“ in der Statusleiste. Commit auf Prod fragt weiterhin nach (seit 1.9).

**LOBs:**

- Bearbeitet wird immer der **ganze Wert** in einem Dialog, nie in der Zelle. `IDataAccess.ReadLobAsync` liest ihn über den Row-Key in der Workspace-Session (sieht eigene geschriebene Änderungen). Die Session holt LOBs vollständig mit der Zeile (`InitialLOBFetchSize = -1`); Grid-Abfragen selektieren LOB-Spalten nie direkt, nur Vorschau und Länge.
- Grenzen (`LobLimits`): bis 10 MB direkt im Textfeld bearbeitbar (Entscheidung des Nutzers), bis 100 MB überhaupt geladen (Datei speichern/laden), darüber nur Länge.
- Der beim Öffnen gelesene Wert ist die Referenz des Concurrency-Checks (`RowChange` merkt ihn als „loaded“), nicht die Vorschau des Grids. Beim Schreiben liest `SELECT … FOR UPDATE WAIT n` den ganzen LOB und vergleicht in .NET.
- Gebunden wird als `OracleDbType.Clob`/`NClob`/`Blob` (auch über 32 KB).
- Hex-Ansicht, Dateityp-Erkennung und Text-Dekodierung (UTF-8/UTF-16 mit BOM/Windows-1252) liegen in `LobContent` (Core, getestet); Datei-Dialoge im Host (`IFileOpenService`, `IFileSaveService.SaveBytesAsync`).
- Lange Bind-Werte werden in SQL-Vorschau, Fehlerdialog und Log gekürzt (`BindValues`).

## Konsequenzen

- Ein freigeschalteter Workspace ist für Oracle ein normaler schreibender Workspace; der Schutz hängt dann allein an FerretSharps Bestätigungen.
- LOBs über 100 MB lassen sich in FerretSharp nicht bearbeiten (Streaming = Backlog).
- Der Dialog hält den ganzen Wert im Speicher, bei großen Texten auch im WebView.
