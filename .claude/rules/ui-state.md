---
paths:
  - "src/FerretSharp.UI/State/**"
  - "src/FerretSharp.UI/Components/**"
  - "src/FerretSharp.UI/*.razor*"
  - "tests/FerretSharp.UI.Tests/**"
---

# UI – Zustand, Dienste und Statements

Muster der Komponenten (Lebensdauer, Rendern, Bausteine, WebView2): `ui.md`; Tests der State-Klassen: `tests.md`.

## Ordner (R6b)
- `State/` hat eigene Ordner (Connections, Copy, Export, Grid, Host = Schnittstellen zur App und ihre reinen Regeln, Links, Model, Schema, Settings, Shell, Tabs, Workspaces), der Namespace bleibt `FerretSharp.UI.State`. Neue Dateien in den passenden Ordner, sonst greifen die `paths` der Regeln nicht.

## Zustand und Dienste
- Dialoge und Bestätigungen fordern Komponenten über den kaskadierten `ShellState` an. Was mit Workspaces und ihren Änderungen passiert (Commit, Rollback, Sperren/Freischalten, Verbinden, Trennen, Löschen, Tab schließen, „vorher fragen“ über `GuardAsync`), liegt im kaskadierten `WorkspaceLifecycle`. `TabBar`, `StatusBar`, `WorkspaceBadge`, `ConnectionLostBanner`, `DialogHost` bekommen ihn als Parameter, damit sie mit der Shell neu rendern.
- Dienste einer Verbindung (`ActiveConnection`, `WorkspaceManager`, `ClrModelManager`, `PresentationService`, `LinqConsoleService`) sind **Scoped** und kommen als `[CascadingParameter]` über `ConnectionScopeView`, **nie per `@inject`/`[Inject]`** – sonst bekommt die Komponente die Instanz des WebView-Scopes, eine Geisterverbindung (`ConnectionServiceInjectionTests` schlägt an). Explorer und Tab-Seiten jeder offenen Verbindung stehen unter deren eigenem `ConnectionScopeView` und bleiben gemountet; Leisten, Dialoge und Seiten hängen an `Hub.Shown` und sind darauf gekeyt (Geschwister brauchen verschiedene Keys). Aktionen auf einem Workspace über `ConnectionHub.OwnerOf(workspaceId)`. `ShortcutService` ist nicht verbindungsgebunden (`@inject` erlaubt).
- Filterzeilen eines Tabs nur über die Methoden von `TableTab` ändern (`SetFilterRows`, `AddFilterRow`, `MoveFilterRow`, `DissolveFilterGroup` …, WP-50), nie `FilterRows` direkt: Sie halten die Reihenfolge „ohne Gruppe, dann Gruppe für Gruppe“, und der Index einer Zeile ist der ihrer Bedingung (Fehler je Zeile).
- Testbare Logik gehört in eine reine Klasse unter `State/` mit Tests in `tests/FerretSharp.UI.Tests` (z. B. `GridRows`, `FormCursor`, `ConnectionHub`, `ShellState`, `GridWriteHistory`, `StatementRunner`, `TableDesignerDraft`, `IndexDraftEditor`, `GridDisplayNames`). Braucht sie die Komponente, bekommt sie ein schmales Interface, das die Komponente implementiert (`IDisplayNamesGrid`, `IFormGrid`).

## DB-Aufrufe und Statements
- DB-Aufrufe aus Komponenten über `Shell.RunDbAsync(Logger, Active, …)` (bzw. `CallDbAsync` auf dem UI-Thread) mit der kaskadierten `ActiveConnection`: einheitlich `DbResult` mit Fehler (geloggt, Verbindungsverlust an genau diese Verbindung: `ActiveConnection.Lost`, Banner je Verbindung), Ablehnung (`RefusedException` → Meldung) oder Abbruch (still). Fachliche Ablehnungen im Core sind `RefusedException`, Programmierfehler-Sperren bleiben `InvalidOperationException`.
- Statements von SQL-Editor, Designer und LINQ-Konsole laufen über **einen** Weg, den `StatementRunner` (`Lifecycle.Statements`): `ReadAsync` (erste Seite), `WriteAsync` (Transaktion des Workspaces), `DdlAsync` (Schemaweg, Sperrhalter in `StatementOutcome.Lock`), `DiscardTransactionAsync` vor DDL nach der Bestätigung, `FinishAsync` danach, `RunDdlAsync` für mehrere DDL-Statements (Designer). Fehler im `StatementOutcome`, nie als Ausnahme; in den Verlauf nur mit `StatementLog` (auf Prod ohne Werte, Passwörter geschwärzt über `SqlRedaction`). Kein eigener `RunDbAsync`-Weg für Statements.
- Laufende Statements zeigt `RunProgress` dort, wo das Ergebnis erscheint (Zeit, Abbrechen, nach 5 s ohne Reaktion „Session trennen“ über `WorkspaceLifecycle.ResetSessionAsync`). Die LINQ-Konsole liest die erste Seite selbst und gibt sie dem Grid (wie der SQL-Editor), damit Abbrechen greift.
