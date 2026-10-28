# WP-66 Verbindungsgruppen verwalten → Release offen

Branch `wp/66-connection-groups`, 2026-10-28. Wünsche eines Kollegen, vom Nutzer am selben Tag weitergegeben: eine Gruppe umbenennen, ohne jede Verbindung einzeln zu öffnen, den Aufklappzustand speichern und die Gruppen verschieben. Der Nutzer hat die „⋯“-Menüs nach dem Muster von WP-61 und ein Tastenkürzel je Verbindung dazugenommen.

## Planung (aus `docs/roadmap.md`)
- Ausgangslage: Eine Gruppe war nur der Text `ConnectionProfile.Group`. Die Reihenfolge war fest (`ConnectionSearch.Group`: alphabetisch, „Ohne Gruppe“ zuletzt), der Aufklappzustand flüchtig und doppelt (je ein `HashSet` in `ConnectionSwitcher` und `ConnectionsPage`). Am Gruppennamen hängen die Audit-Vorlage der Gruppe (`AppSettings.AuditTemplateGroups`, WP-51) und der aktive Arbeits-Klon (`work-clones.json`, WP-40); nach einem Umbenennen von Hand blieben sie verwaist stehen.
- **Umbenennen** im Menü der Gruppe, alle Profile in einem Schreibvorgang; Audit-Vorlage, Arbeits-Klon, Aufklappzustand und Position gehen mit. Ein vorhandener Name legt die Gruppen nach einer Rückfrage zusammen; haben beide eine Audit-Vorlage, behält die Zielgruppe ihre. „Ohne Gruppe“ ist nicht umbenennbar.
- **Aufklappzustand und Reihenfolge** in einer neuen Datei `connection-groups.json` (Zustand wie `recent.json`). Gruppen ohne Platz alphabetisch dahinter, „Ohne Gruppe“ zuletzt; gilt überall, wo Gruppen gelistet werden. Import und Export übernehmen sie nicht.
- **Ziehen** auf der Verbindungsseite: Gruppenköpfe für die Reihenfolge, Verbindungen in eine andere Gruppe; „Nach oben/unten“ im Menü für die Tastatur.
- **Menüs** aus den WP-61-Bausteinen, ohne Filterfeld; Rechtsklick öffnet dasselbe Menü; `KeyNavigation` für alle `MoreMenu`.
- **Tastenkürzel je Verbindung** im Verbindungsdialog, gespeichert in den Einstellungen, geprüft wie die Aktionen.

## Entscheidungen des Nutzers (2026-10-28)
1. **Ein Paket** für alle fünf Teile, weil alle an Umschalter, Verbindungsseite und Verbindungsdialog hängen.
2. **Tastenkürzel nur innerhalb von FerretSharp.** Systemweit (`RegisterHotKey`, FerretSharp nach vorne holen) steht im Backlog.
3. **Ziehen nur auf der Verbindungsseite.** Die Seite ist der Ort zum Verwalten; im Popover des Umschalters reichen die Chips im Menü.
4. **Ein gemeinsamer Aufklappzustand** für Umschalter und Seite.
5. Die Empfehlungen des Plans übernommen: Taste in den Einstellungen statt im Profil, keine Standardtaste, kein ADR.

## Entscheidungen in der Umsetzung
- **Core:** `ConnectionGroupState` hält Reihenfolge und eingeklappte Gruppen (Namen wie die Listen sie zeigen, „“ für ohne Gruppe) und rechnet `Move` (vor eine Gruppe oder ans Ende). `ConnectionGroupRename.Plan` ist eine reine Rechnung: geänderte Profile, Audit-Vorlagen danach, ob zusammengelegt wird und welche Vorlage dabei entfällt. Beim Zusammenlegen nimmt das Ziel seine eigene Schreibweise (`kasse` in `Kasse`), sonst zeigten die Listen je nach erstem Profil eine andere. Eine Vorlage, die für den neuen Namen schon eingetragen ist (verwaist nach einem früheren Umbenennen), zählt wie die einer vorhandenen Gruppe. `WorkClones.RenameGroupAsync` nimmt den aktiven Klon mit, bei einer Zusammenlegung bleibt der des Ziels. Kein eigenes `ConnectionManager.RenameGroupAsync`: `SaveManyAsync` speichert die Profile schon in einem Schreibvorgang.
- **Tasten:** `AppSettings.ConnectionShortcuts` (Profil-Id → Taste). `ShortcutMap` liest sie mit den Regeln einer globalen Aktion; eine Taste, die eine Aktion hat, gilt nicht für die Verbindung (die Aktion gewinnt). `CheckConnection` meldet Konflikte mit Aktionen und anderen Verbindungen, `Check` einer Aktion auch Konflikte mit Verbindungen (`ShortcutCheck.ConflictConnection`). Wer eine Taste nimmt, nimmt sie dem anderen weg – auch „Zurücksetzen“ einer Aktion und „Alle zurücksetzen“ (sonst täte die Taste der Verbindung still nichts, wie R4a für zwei Aktionen). Gelöschte Verbindungen räumt `ConnectionGroups` aus den Einstellungen, sobald die Liste sie nicht mehr hat – nie bei einer `connections.json`, die sich nicht lesen ließ.
- **Wirkung der Taste** in `Shell.OnShortcut` nach den Aktionen: `ShellState.Connect` wie ein Klick im Umschalter (zeigt eine offene Verbindung, verbindet sonst; Passwort, Prod und Schreibschutz wie dort). In einem Dialog nichts.
- **UI-State:** `ConnectionGroups` (Singleton) bündelt Liste in Reihenfolge, Aufklappen, Verschieben, Umbenennen und das Aufräumen der Tasten. Ließ sich etwas nicht speichern, gilt es bis zum Beenden, und ein Hinweis sagt es (Umbenennen: Profile zuerst, danach Vorlage, Klon und Zustand; was davon scheitert, steht im Hinweis).
- **Menüs:** `ConnectionMenuBody` und `ConnectionGroupMenuBody` stecken hinter „⋯“ (`MenuButton` mit `KeyNavigation`) und hinter dem Rechtsklick (`ContextMenuHost`, neu in `Common`: ein `PopoverMenu` an der Maus für ein Element einer Liste). `QuickBar`, `QuickAction` und `MenuEntry` liegen jetzt in `Components/Common`.
- **Ereignisse bleiben im Menü:** `PopoverMenu` stoppt Tasten, Klicks, Rechtsklicks und Pointer-Down an Menü und Hintergrund. Ein Menü in einer Zeile (das „⋯“ einer Verbindung) gab sonst ↑ ↓ und Esc an den Umschalter weiter (Markierung wanderte, Esc schloss beides), Klicks an die Zeile (verbindet), einen Rechtsklick an das Menü der Zeile und ein Drücken an das Ziehen.
- **Ziehen:** neu `dom.js dragAfter` – wartet, bis der Zeiger 5 px mit gedrückter Taste gewandert ist, und gibt dann an `dragToDrop` weiter. Vorher wird nichts eingefangen: Klick und Doppelklick (verbinden, auf- und zuklappen) bleiben bei der Zeile. Ziele sind die Gruppen-Abschnitte (`data-drop` = Index). Eine Verbindung leuchtet die Zielgruppe an (`drop-into`); ein Gruppenkopf zeigt eine Linie über bzw. unter dem Ziel, je nachdem ob er nach oben oder unten wandert (`drop-before`/`drop-after`, vor dem Ziehen gerendert). Gruppen lassen sich nicht ziehen, solange gesucht wird (die Liste ist dann gefiltert), und „Ohne Gruppe“ nie.
- **Dialoge:** `GroupNameDialog` für Umbenennen und „Neue Gruppe…“ einer Verbindung (ein vorhandener Name nimmt sie auf, mit Hinweis). „Neue Verbindung in dieser Gruppe“ belegt die Gruppe im Verbindungsdialog vor (`ConnectionDialogRequest.Group`). „Alle trennen“ fragt einmal für alle Verbindungen der Gruppe (`WorkspaceLifecycle.DisconnectAllAsync`): Nacheinander hätte jede Frage die vorige ersetzt.
- **Audit-Vorlage der Gruppe** im Menü öffnet die Einstellungen beim Bereich Audit (`ShellState.ShowSettings(part)`). Der **Arbeits-Klon** steht nur als Fakt im Kopf: sein Dialog gehört zur angezeigten Verbindung und ihrem Projekt.
- **Export einer Verbindung** aus ihrem Menü: `ConnectionExportRequest.Connection` wählt nur sie vor.
- **Einstellungen › Tastenkürzel** hat einen Bereich „Verbindungen“ (Name mit Gruppe, Taste, Bearbeiten, Entfernen), auch in der Suche (`SettingsCatalog.ConnectionKeys`, Name, Gruppe und Taste).
- Neue Icons aus Lucide 1.55: `plug` (Verbinden), `unplug` (Trennen), `square-pen` (Bearbeiten), `copy-plus` (Duplizieren).

## Abweichungen vom Plan
- Die Taste steht **nicht in der Befehlspalette**: Die Palette listet keine Verbindungen (ein „@“-Modus steht im Backlog). Sichtbar ist sie in der Zeile im Umschalter und auf der Seite, im Kopf des Verbindungsmenüs und in den Einstellungen.
- Der Aufnahme-Teil der Einstellungsseite ist nicht herausgezogen; der Dialog hat eine eigene kleine Komponente (`ConnectionShortcutField`) mit derselben Aufnahme (`shortcuts.js record`) und Prüfung.
- Der Arbeits-Klon im Gruppenmenü springt nicht in seinen Dialog (siehe oben).

## Gebaut
- **Core:** `ConnectionGroupState`, `ConnectionGroupRename`, `ConnectionSearch.Group`/`InListOrder` mit Reihenfolge, `WorkClones.RenameGroupAsync`, `AppSettings.ConnectionShortcuts`, `ShortcutMap` (`ConnectionChord`, `ConnectionFor`, `CheckConnection`, `WithConnection`, `ConnectionsWithout`, `Defaults`, `ShortcutCheck.ConflictConnection`), `AppPaths.ConnectionGroupsFile`.
- **UI-State:** `ConnectionGroups`, `ConnectionLastUsed`, `ShortcutService` (`ConnectionKey`, `SetConnectionAsync`, `ForgetConnectionsExceptAsync`), `ShellState` (`GroupNameRequest`, `RenameGroup`, `NewGroupFor`, `NewConnection(group)`, `ShowSettings(part)`), `WorkspaceLifecycle.DisconnectAllAsync`, `SettingsCatalog` (Tasten der Verbindungen), `ConnectionExportSelection`/`LinkTargetChoice` mit Reihenfolge.
- **UI:** `ConnectionMenuBody`, `ConnectionGroupMenuBody`, `ConnectionActionsMenu`, `ContextMenuHost`, `GroupNameDialog`, `ConnectionShortcutField`; umgebaut `ConnectionsPage`, `ConnectionSwitcher`, `ConnectionDialog`, `ShortcutSettings`, `SettingsPage`, `PopoverMenu`, `MenuButton`/`MoreMenu` (`KeyNavigation`), `Shell` (Taste), `dom.js` (`dragAfter`), CSS (`.drop-into`, `.drop-line`, `.conn-key`, `.ctx-conn-name`).
- **Tests:** Core `ConnectionGroupStateTests`, `ConnectionGroupRenameTests`, `ConnectionSearchTests`, `WorkCloneTests`, `ShortcutMapTests`; UI `ConnectionGroupsTests`, `ShortcutServiceTests`, `SettingsCatalogTests`, `ConnectionTransferTests`.

## E2E (2026-10-28)
Eigene Instanz (`--data-dir`, eigener WebView2-Ordner, Build im Scratchpad, `--lang=en`, dunkel), fünf erfundene Verbindungen in vier Gruppen auf einen leeren Port, keine Datenbank nötig: Die Taste einer Verbindung ohne Passwort öffnet die Passwortabfrage (abgebrochen, kein Eintrag im Credential Manager). Klicks, Rechtsklicks, Ziehen und Tasten per CDP (Stufe 2), Zustand über DOM und Dateien.
- **Gefunden und behoben:** Nach einem Neustart waren alle Gruppen aufgeklappt und alphabetisch, obwohl `connection-groups.json` stimmte – die Listen rendern mit den Verbindungen, bevor der Zustand gelesen ist, und `LoadAsync` meldete nichts. Jetzt meldet es `Changed` (Test).
- **Gefunden und behoben (Layout):** Die Leiste des Gruppenmenüs brach bei fünf Aktionen um („Nach unten“ allein in Zeile 2); jetzt fünf Spalten.
- **Nicht mit echter Eingabe geprüft:** Per `SendInput` kam wie bei WP-61 kein `pointerdown` an – laut Anleitung nicht weitergeklickt. Ziehen (Gruppenkopf, Verbindung), Doppelklick zum Verbinden auf der Seite und Rechtsklick bitte von Hand prüfen.
- **Nicht geprüft:** „Exportieren“ bis zum Dialog (unveränderter Weg), „Alle trennen“ und „Anzeigen“ mit offenen Verbindungen (brauchen eine DB), die Taste über einem offenen SQL-Editor (Monaco).

## Nachtrag: Code-Review (2026-10-28)
Unabhängiger Reviewer über den Branch, zwei Prüfdurchgänge über die Fixes. Entscheidung des Nutzers zur ersten Runde: „Alle beheben“, zur zweiten: „Alle vier beheben“.
1. **M – Offene Verbindungen behielten nach dem Umbenennen die alte Gruppe** (Audit-Vorlage und Arbeits-Klon unter dem alten Namen gesucht): behoben. Nach jedem Speichern übernehmen offene Verbindungen ihr Profil, wenn sich an der Session nichts ändert (`ConnectionHub.UseSaved`, `ActiveConnection.UseSaved`); Art, Adresse, User, Schema und Schreibschutz weiter erst nach dem Neuverbinden. Löst auch das Versprechen „Name, group and paths apply at once“ an `SessionDiffers` ein, das vorher schon für den Dialog nicht galt.
2. **M – „Audit-Vorlage der Gruppe…“ tat auf einer offenen Einstellungsseite nichts:** behoben, die Seite nimmt den Bereich auch bei `Shell.Changed`.
3. **M – „Zurücksetzen“ nahm einer Verbindung die Taste ohne Rückfrage:** behoben; die Frage bei „Alle zurücksetzen“ nennt die Folge.
4. **K – Schnelle Klicks hoben einander auf** (Gruppenzustand vor der Sperre berechnet): behoben, jede Änderung wird in der Sperre aus dem aktuellen Stand berechnet.
5. **K – Reihenfolge fehlte an fünf Stellen:** behoben (Tastenliste, App-Statements, Schema-Vergleich, Link-Vorschläge, Audit-Gruppen; verwaiste Audit-Einträge stehen jetzt hinter den Gruppen).
6. **K – Suche der Einstellungen veraltete:** behoben (neu bei `Connections.Changed`).
7. **K – Eine neue Gruppe erbte Reste einer früheren gleichen Namens:** behoben – beim Umbenennen und für jede Gruppe, die in der Liste neu auftaucht (Dialog, Import, Chips, Ziehen; Nachtrag aus der zweiten Runde).
8. **K – Verschieben speicherte das Profil vom Öffnen des Menüs:** behoben (per Id neu gelesen, gelöschte Verbindungen bleiben gelöscht).
9. **K – Der Arbeits-Klon fiel beim Zusammenlegen still weg:** behoben, der Dialog nennt ihn – seit der zweiten Runde auch beim Umbenennen auf einen Namen, unter dem noch ein Klon steht.
10. **K – Eine fehlende `connections.json` löschte alle Verbindungstasten:** behoben (bei leerer Liste kein Aufräumen).
11. **K (zweite Runde) – Ein Speichern während des Verbindens ging verloren:** erst nachgestellt (Test schlug fehl), dann behoben (`ActiveConnection.Saved`).
12. **K (zweite Runde) – Ein Test wartete fest 50 ms:** behoben, `ConnectionGroups.Cleanup` ist abwartbar. Die Einstellungsseite selbst (Punkte 2 und 6) hat keinen Komponententest (kein bUnit): von Hand prüfen.
13. **K (dritte Runde) – Folge von Punkt 7/12:** Seit `MoveConnectionAsync` auf das Aufräumen wartet, hätte eine schreibgeschützte `settings.json` (`UnauthorizedAccessException`) ein gelungenes Verschieben als gescheitert gemeldet. Behoben im selben Zug (das Aufräumen fängt beide Fehler), ohne eigenen Test: eine schreibgeschützte Datei verhält sich unter Linux in der CI anders.
- Vorher schon da, nicht geändert: Menüs zeigen „verbunden“ auch bei einem laufenden oder fehlgeschlagenen Verbinden, die Verbindungsseite nicht.

## Nachtrag: E2E mit Datenbank (2026-10-29)
Vor dem Merge, Stand mit `main` (alle anderen offenen Branches gemerged, auch das Öffnen in Versuchen aus `fix/session-open-retry`): eigener Container `ferret-e2e-enum` (Port 1547), Beta über einen TCP-Proxy im Scratchpad (Port 1548), der einzelne Verbindungsaufbauten hängen lassen kann. Drei erfundene Verbindungen (Alpha und Beta in „Team“, Gamma in „Ops“), eigene Instanz mit `--data-dir`, eigenem WebView2-Ordner, Debug-Port, `--lang=en`, Passwörter ohne Speichern. Bedienung per CDP (Stufe 2):
- **„Show“** im Menü einer verbundenen Verbindung, über „⋯“ und über Rechtsklick: zeigt sie, Menü und Seite schließen.
- **„Disconnect all“** der Gruppe Team mit je einem nicht committeten UPDATE in Alpha und Beta: genau eine Frage („Disconnect the group Team“) mit beiden Transaktionen; „Discard“ trennt beide, Gamma bleibt verbunden. In der DB danach nur noch Gammas Session, die Updates verworfen.
- **Taste einer Verbindung:** im Dialog aufgenommen; Ctrl+Alt+B abgelehnt (AltGr), Ctrl+Shift+B angenommen, in `settings.json` unter der Id, in der Zeile sichtbar. Mit dem Fokus in einem SQL-Editor (Monaco) einer anderen Verbindung zeigt die Taste Beta und verbindet sie (Passwort aus der Sitzung), im Editor kommt kein Zeichen an.
- **Mit dem Öffnen in Versuchen:** Beta über die Taste mit hängendem ersten Versuch → „attempt 2 of 5“, verbunden nach 5,1 s. Gruppe Team in „Team Neu“ umbenannt, während Beta im zweiten Versuch hing: Die Statusleiste zeigte sofort „Team Neu › Beta“, nach dem Verbinden (dritter Versuch) bleibt Beta in „Team Neu“ (Seite, Umschalter, `connections.json`) – `UseSaved` und `Saved(profile)` greifen auch über das längere Öffnen.
- **„Export connection…“** im Menü bis zum Dialog: nur Beta vorgewählt („Export 1 connection…“), abgebrochen.
- Log ohne `[ERR]`/`[WRN]`.
- **Aufgefallen (Frage):** Die gemeinsame Frage von „Disconnect all“ listet beide Einträge als „Workspace 1 – open transaction“, ohne die Verbindung zu nennen – bei mehreren Verbindungen ist nicht zu sehen, welche Transaktion zu welcher gehört.
- **Weiter nicht mit echter Eingabe geprüft:** Ziehen (Gruppenkopf, Verbindung), Doppelklick und Rechtsklick mit echter Maus – bitte von Hand.

## Offen
- Tastenkürzel systemweit (Backlog).
- Die Taste einer Verbindung in einem „@“-Modus der Schnellsuche (Backlog, Fortsetzung von WP-38).
