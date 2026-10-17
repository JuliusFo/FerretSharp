---
paths:
  - "src/FerretSharp.UI/**"
  - "src/FerretSharp.App/**"
---

# UI – Muster und Fallen

UI-Konzept und Tastenkürzel: `docs/architecture.md` 2–3. Grid, Monaco und CSS haben eigene Regeln (`ui-grid.md`, `ui-monaco.md`, `ui-css.md`), Tests `tests.md`.

## Ordner (R6b)
- Komponenten liegen nach Bereich in `Components/` (Common, Shell, Connections, Schema, PlSql, Grid, Filter, Sql, Ddl, Pages); der Ordner ist der Namespace, ein neuer Ordner kommt in die Wurzel-`_Imports.razor`. Die `.cs`-Helfer dort (`FerretComponent`, `Fmt`, `Icons`, `DetailViewBase`) behalten `FerretSharp.UI.Components`.
- `State/` hat dieselbe Gliederung (dazu Tabs, Links, Settings, Host = Schnittstellen zur App, Model), der Namespace bleibt `FerretSharp.UI.State`. Neue Dateien in den passenden Ordner, sonst greifen die `paths` der Regeln nicht.

## Zustand und Dienste
- Dialoge und Bestätigungen fordern Komponenten über den kaskadierten `ShellState` an. Was mit Workspaces und ihren Änderungen passiert (Commit, Rollback, Sperren/Freischalten, Verbinden, Trennen, Löschen, Tab schließen, „vorher fragen“ über `GuardAsync`), liegt im kaskadierten `WorkspaceLifecycle`. `TabBar`, `StatusBar`, `WorkspaceBadge`, `ConnectionLostBanner`, `DialogHost` bekommen ihn als Parameter, damit sie mit der Shell neu rendern.
- Dienste einer Verbindung (`ActiveConnection`, `WorkspaceManager`, `ClrModelManager`, `PresentationService`, `LinqConsoleService`) sind **Scoped** und kommen als `[CascadingParameter]` über `ConnectionScopeView`, **nie per `@inject`/`[Inject]`** – sonst bekommt die Komponente die Instanz des WebView-Scopes, eine Geisterverbindung (`ConnectionServiceInjectionTests` schlägt an). Explorer und Tab-Seiten jeder offenen Verbindung stehen unter deren eigenem `ConnectionScopeView` und bleiben gemountet; Leisten, Dialoge und Seiten hängen an `Hub.Shown` und sind darauf gekeyt (Geschwister brauchen verschiedene Keys). Aktionen auf einem Workspace über `ConnectionHub.OwnerOf(workspaceId)`. `ShortcutService` ist nicht verbindungsgebunden (`@inject` erlaubt).
- Filterzeilen eines Tabs nur über die Methoden von `TableTab` ändern (`SetFilterRows`, `AddFilterRow`, `MoveFilterRow`, `DissolveFilterGroup` …, WP-50), nie `FilterRows` direkt: Sie halten die Reihenfolge „ohne Gruppe, dann Gruppe für Gruppe“, und der Index einer Zeile ist der ihrer Bedingung (Fehler je Zeile).
- Testbare Logik gehört in eine reine Klasse unter `State/` mit Tests in `tests/FerretSharp.UI.Tests` (z. B. `GridRows`, `FormCursor`, `ConnectionHub`, `ShellState`, `GridWriteHistory`, `StatementRunner`, `ColumnDesigner`, `IndexDraftEditor`, `GridDisplayNames`). Braucht sie die Komponente, bekommt sie ein schmales Interface, das die Komponente implementiert (`IDisplayNamesGrid`, `IFormGrid`).

## DB-Aufrufe und Statements
- DB-Aufrufe aus Komponenten über `Shell.RunDbAsync(Logger, Active, …)` (bzw. `CallDbAsync` auf dem UI-Thread) mit der kaskadierten `ActiveConnection`: einheitlich `DbResult` mit Fehler (geloggt, Verbindungsverlust an genau diese Verbindung: `ActiveConnection.Lost`, Banner je Verbindung), Ablehnung (`RefusedException` → Meldung) oder Abbruch (still). Fachliche Ablehnungen im Core sind `RefusedException`, Programmierfehler-Sperren bleiben `InvalidOperationException`.
- Statements von SQL-Editor, Designer und LINQ-Konsole laufen über **einen** Weg, den `StatementRunner` (`Lifecycle.Statements`): `ReadAsync` (erste Seite), `WriteAsync` (Transaktion des Workspaces), `DdlAsync` (Schemaweg, Sperrhalter in `StatementOutcome.Lock`), `DiscardTransactionAsync` vor DDL nach der Bestätigung, `FinishAsync` danach, `RunDdlAsync` für mehrere DDL-Statements (Designer). Fehler im `StatementOutcome`, nie als Ausnahme; in den Verlauf nur mit `StatementLog` (auf Prod ohne Werte, Passwörter geschwärzt über `SqlRedaction`). Kein eigener `RunDbAsync`-Weg für Statements.
- Laufende Statements zeigt `RunProgress` dort, wo das Ergebnis erscheint (Zeit, Abbrechen, nach 5 s ohne Reaktion „Session trennen“ über `WorkspaceLifecycle.ResetSessionAsync`). Die LINQ-Konsole liest die erste Seite selbst und gibt sie dem Grid (wie der SQL-Editor), damit Abbrechen greift.

## Ausnahmen und Lebensdauer
- JSInvokable-Methoden werfen keine Ausnahmen an JS zurück (Blazor lässt die Task unbeobachtet → `[ERR]` im Log), sondern melden Fehler im Ergebnis (`GridPage.Failed`) oder laufen über `GuardAsync`.
- Arbeit, die nicht aus einem Blazor-Event-Handler startet (JS-Callback, Shortcut, Timer, Dienst-Ereignis), nie als `_ = …`: Komponenten erben von `FerretComponent` und rufen `Fire(...)` bzw. `GuardAsync(...)` – die Ausnahme landet in der ErrorBoundary des Tabs (`TabFrame`) statt unbeobachtet im Log.
- Komponenten mit JS-Interop prüfen nach jedem `await` im Start und in Handlern, ob sie abgebaut sind (`_disposed`), und rufen danach kein JS mehr auf (Muster: `MonacoEditor.CallAsync`, `GridBridge.CallAsync`).
- JS-Module nur über `JsModule(JS, path)` (R5b; Import beim ersten Aufruf, nach dem Abbau weder Import noch Aufruf, getrennte Seite ist kein Fehler, `DisposeAsync("destroy", …)` mit letztem Aufruf) – nie `JS.InvokeAsync<IJSObjectReference>("import", …)` von Hand. dom.js über `DomModule` (`Dom => _dom ??= new(JS)`, im Dispose `await Dom.DisposeAsync()`); `CopyTextAsync(shell, text)` meldet eine fehlende Zwischenablage selbst.
- **Wer DDL auslöst, kann während des Laufs abgebaut werden:** `RefreshSchemaAsync` baut Tabs mit geänderter Struktur neu auf (`StructureVersion` als `@key`), auch die auslösende Komponente. Was danach gebraucht wird (Entwurf, Fehler), vorher sichern – `RunDdlAsync(…, ended, …)` meldet jedes Ende – und im Tab-Zustand ablegen (`TableTab.Designer`), nach Erfolg dort entfernen; nach dem `await` nichts mehr an der alten Instanz (kein entsorgtes `CancellationTokenSource.Token`).

## Rendern
- Komponenten ohne Parameter rendern bei Parent-Updates nicht neu → sie abonnieren `ShellState.Changed` bzw. `ConnectionManager.Changed`/`WorkspaceManager.Changed` selbst. Tab-Zustand ohne Neurendern (Tippen im Filter, Scrollen, Sortieren) meldet `ShellState.MarkDirty()`.
- `ShellState.Changed` feuert bei jeder Zellbearbeitung. Eine Komponente, die viel rendert und nur einen kleinen Teil der Shell zeigt, rendert nur, wenn sich dieser Teil geändert hat (Explorer: `ShellKey`).
- Modell-Ladeschritte und ein neues Modell rendern nie die ganze Shell (sie rendert jeden gemounteten Tab jeder Verbindung); Statusleiste und Modellseite folgen dem `ClrModelManager` selbst. `PresentationService` meldet tabellengenau (`ChangedSince(version, table)`, `NamesChangedSince`).

## Bausteine
- Jeder Dialog nutzt `ModalFrame` (Hintergrund, Rolle, Esc, Klick daneben nur bei reinen Anzeige-Dialogen, Fokus), jedes Kontext- und Aktionsmenü `PopoverMenu` (Position, Fokus, Esc, im Fenster gehalten) – keine handgebauten Menüs. Ein Knopf mit Menü ist ein `MenuButton` (misst Fenster und Position selbst), eine Liste einfacher Aktionen ein `MoreMenu` mit `MoreMenuItem` (`Strong`, `Danger`, `SeparatorBefore`; R6b). Kopieren und Speichern aus Menüs über `ExportActions`, die Export-Gruppe der Zellmenüs ist `ExportMenuItems`, ihre FK-Sprünge `FkJumpLists` + `FkJumpItem`; Schreib-Ergebnisse als `WriteOutcome`.
- Suchfelder sind `SearchBox` (`@bind-Value`, Zusätze als `ChildContent`): Esc mit Text leert und wird in `dom.js` (`clearOnEscape`) gestoppt – Blazor kann `stopPropagation` nicht je Taste setzen –, erst ein Esc im leeren Feld erreicht Popover/Dialog.
- SQL-Codeblöcke sind immer `SqlCode` (Kopier-Symbol eingebaut). Die Knöpfe Schreiben/↶/↷/Commit/Rollback sind `WorkspaceActionsBar`.
- Ein Name, der einem Vorschlag folgt, bis er getippt wird (Constraints, Indizes), ist ein `SuggestedName`.
- Neue Einstellung: eigener `SettingKey`, Eintrag in `SettingsCatalog.Entries` (Hinweis wie auf der Seite) und `@Filter.HideClass(SettingKey.X)` an Zeile und Hinweisen – sonst findet die Suche sie nicht. Eine Auswahl weniger Werte ist eine `SettingsChoice` (Werte in `SettingsOptions`), jeder größere Bereich eine eigene Abschnittskomponente neben `SettingsPage` (R6b). Speichern nur über `Shell.TrySaveSettingsAsync(…)` (die Einstellung gilt schon, scheitert die Datei, kommt „nicht gespeichert“).
- Tooltips immer als `title`-Attribut (auch an deaktivierten Knöpfen), keine eigenen Tooltip-Komponenten: `tooltip.js` zeigt jeden `title` einheitlich und nach oben, wenn unten kein Platz ist (der native öffnete hinter der Taskleiste). Ausgenommen Monaco und Grid-Zellen (`tooltip` als Callback in der Column-Def; `tooltipValueGetter` ist seit AG Grid 36.2 veraltet).
- Tastenkürzel nie als festen Text: `<Kbd Action="…" />`, `Shortcuts.Hint(…)`/`Label(…)` (in Sätzen als Platzhalter). Neue Aktion: Eintrag in `ShortcutAction` und `ShortcutMap.Definitions` (der Name ist der Schlüssel in `settings.json`) **ohne Standardtaste** (Entscheidung des Nutzers: eine Vorgabe könnte ihm eine selbst vergebene Taste wegnehmen); global in `Shell.OnShortcut`, lokal über `ShortcutService.Is` bzw. `shortcuts.js setLocal`.
- Knöpfe außerhalb des Grids, die sofort etwas tun (✕ eines Tabs), nehmen den Fokus nicht (`@onmousedown:preventDefault`) und fragen `cellEditing()` aus `shortcuts.js`: AG Grid beendet eine Zelleingabe erst **nach** dem Klick, der den Fokus wegnahm – sonst geht der getippte Wert verloren.

## Blazor im WPF-Host (WebView2)
- Kein `auxclick`-Event in Blazor: `@onauxclick` wird still als HTML-Attribut ausgegeben. Mittelklick über `@onmouseup` mit `e.Button == 1`.
- **Kein `@ondblclick`:** Der WPF-Host (`WebView2CompositionControl`) reicht beim zweiten Klick das Mouse-down doppelt weiter, Chromium zählt `detail` 1 → 2 → 3, `dblclick` feuert nie. Doppelklick über `@onclick` mit `e.Detail >= 2`.
- Native `<select>`: die Auswahl über `value` am `<select>` setzen, nicht `selected=` an der Option – sobald der Nutzer einmal gewählt hat, ignoriert der Browser das Attribut. Ändern sich die Optionen, `@key` auf die Optionsmenge: Bleibt der Wert gleich, setzt Blazor ihn nicht neu, und der Browser zeigt die erste Option.
- **UI-Kultur nur synchron setzen:** `CultureInfo.CurrentUICulture` gehört zum `ExecutionContext`, eine Änderung in einer async-Methode wird beim Verlassen zurückgenommen. `App.OnStartup` liest die Einstellungen synchron und setzt die Kultur vor dem async-Teil. Unit-Tests merken das nicht.
- **DPI:** Der Host ist per `app.manifest` PerMonitorV2-aware; ohne wird das Fenster auf Monitoren mit anderer Skalierung unscharf und `<select>`-Popups falsch groß.
- Theme: `WindowTheme` (App) setzt Titelleiste, Fensterhintergrund und `CoreWebView2.Profile.PreferredColorScheme`; die Einstellung wird vor dem Erzeugen des Fensters gelesen (kein weißes Aufblitzen), `--theme=dark|light` übersteuert. In WPF-Dateien `ThemeMode` per Alias auf `FerretSharp.Core.Settings` (kollidiert mit `System.Windows.ThemeMode`).
- Mausinteraktionen mit echter Windows-Eingabe prüfen, nicht nur per CDP (`docs/e2e-testing.md`).
