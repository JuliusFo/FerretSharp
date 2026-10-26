# Klein: Tabellen-Designer in Reitern, Constraints umbenennen und ändern → Release 3.51.0

Branch `feat/designer-tabs` (von `main`), 2026-10-26.

## Anlass
Rückmeldung des Nutzers und eines Kollegen zur Bearbeitungsmaske der Spalten-Ansicht (WP-23), in der Spalten, Constraints, Audit, Skript und Aktionen untereinander standen:
- Constraints ließen sich nur löschen und anlegen, nie umbenennen.
- Dass der Workspace gesperrt ist, stand nur klein und grau über den Knöpfen – leicht übersehen (Kollege).
- Wer nichts ändern wollte, musste zum Schließen bis ganz nach unten scrollen.
- Änderungen an der Tabelle selbst (Kommentar) gingen nicht.

Dazu eine Skizze des Nutzers: Reiter über dem Inhalt, darüber die Zusammenfassung der Änderungen und die Aktionen.

## Entscheidungen des Nutzers
- Von drei Varianten (Reiter; Formular links mit Skript rechts; aufklappbare Abschnitte mit fester Fußleiste) gewählt: **Reiter** – Spalten, Constraints, Audit, C# (nur mit neuen Spalten einer gemappten Tabelle), Skript. Kopf mit Zusammenfassung (Statements, Warnungen, Probleme; Klick öffnet das Skript) und Ausführen / Im SQL-Editor / Speichern / Schließen; Sperre als Leiste in Warnfarbe.
- Den Reiter **„Tabelle“** (Kommentar, Umbenennen) vorerst weglassen, die Maske startet auf den Spalten. Tabelle umbenennen bleibt ohnehin Backlog: Audit-Namen, manuelle FKs, Anzeigespalten, Tabs und Filter in den Workspaces, C#-Mapping hängen am Namen.
- Constraints: **Umbenennen** für alle Arten, ein Schlüssel nimmt seinen Index gleichen Namens mit (abwählbar). **Ändern** nur für die Bedingung eines Checks und die Löschregel eines FK; PK/UK bleiben bei Löschen und neu Anlegen (eingehende FKs hängen daran).
- Indizes bleiben in ihrer Ansicht; „Bearbeiten“ in der Constraints-Ansicht öffnet die Maske auf dem Reiter Constraints.

## Gebaut
- Core: `TableDraft.ChangedConstraints` mit `ConstraintEdit(Original, Name, Condition, DeleteRule, RenameIndex)`, geplant in `TableDesign.EditConstraints`:
  - Nur umbenannt: `RENAME CONSTRAINT`, bei PK/UK mit einem Index desselben Namens `ALTER INDEX … RENAME TO` (neue Schrittarten `RenameConstraint`, `RenameIndex`, Stufe direkt nach dem Löschen der Constraints). Der neue Name darf keinem anderen Constraint gehören – auch keinem, der selbst umbenannt wird (ein Tausch bräuchte einen Zwischennamen) –, und ein neuer Constraint darf ihn nicht nehmen. Warnung: Code, der Constraint-Namen auswertet (ORA-00001 …), erwartet den alten.
  - Neue Check-Bedingung: der neue Check zuerst, der alte erst danach (`ReplaceConstraint`, Stufe nach den Fremdschlüsseln) – scheitert der neue an den Zeilen, bleibt der alte. Behält er seinen Namen, kommt er unter `FS_CHECK_n` und wird am Ende umbenannt; ein von Oracle vergebener Name wird neu vergeben.
  - Neue Löschregel: FK löschen und neu anlegen (Oracle lehnt einen zweiten FK auf dieselben Spalten ab, ORA-02275), mit den Spaltennamen nach den Umbenennungen; Warnung, dass er dazwischen nicht gilt. `NO ACTION` aus dem Dictionary gilt als keine Regel.
  - Geänderte Constraints prüfen die Zeilen wie neue (Zählfrage `Rows`, gemeinsame Warnung).
- UI State: `ExistingConstraint` (Name, Bedingung, Löschregel, Index mitnehmen) in `ColumnDesigner.Existing`, `DesignerSection` als `ColumnDesigner.Section` (übersteht `Rebase`), `ColumnsChanged`/`ConstraintsChanged` für die Punkte an den Reitern; `TableTab.OpenDesignerAt` für „Bearbeiten“ aus der Constraints-Ansicht. `Rebase` findet einen bestehenden Constraint über den neuen, sonst den alten Namen; ein halb ersetzter Check (alter weg, `FS_CHECK_n` da) verliert seine Änderung, der neue erscheint als eigener Constraint.
- UI: `TableDesigner` in Reitern; `DdlDraftPanel` mit `Head`/`Body` als Kopf der Maske (Aktionen, Lauf bzw. Fehler darunter), sonst wie bisher (Index-Entwurf). Neue `WorkspaceLockedBanner` (Leiste mit „Workspace freischalten …“) über der Maske und im Index-Entwurf statt des grauen Hinweises; „Ausführen“ trägt bei gesperrtem Workspace ein Schloss. Der Kopf bleibt beim Scrollen stehen (die Kopfzeile der Spaltentabelle klebt in der Maske deshalb nicht mehr).
- Tests: Core `TableDesignTests` (Umbenennen mit/ohne Index, Check ersetzen mit und ohne Zwischennamen, generierter Name, Löschregel mit umbenannter Spalte, Namensprüfungen, gelöschter Constraint), UI `ColumnDesignerTests` (Edits aus den Feldern, Namensvorschlag, `Rebase`).

## E2E
Eigener Container mit dem Beispielschema, Testinstanz mit `--data-dir`, Dev- und Prod-Verbindung, hell und dunkel; Klicks per CDP (keine neue Mausinteraktion außer Knöpfen).
- Gefunden und behoben: Nach dem Freischalten blieb die Sperrleiste stehen, während „Ausführen“ sein Schloss schon verloren hatte – die Leiste hat nur die Workspace-ID als Parameter und rendert beim Elternteil nicht neu. Sie folgt jetzt `WorkspaceManager.Changed` selbst.
- Geprüft gegen Oracle: PK umbenannt samt Index, FK mit neuer Löschregel, Check mit einer Bedingung, die vorhandene Zeilen brechen (ORA-02293 auf `FS_CHECK_1`, der alte Check blieb, nichts lief), danach mit gültiger Bedingung (Name bleibt); ein deaktivierter Check behält `DISABLE`. „Bearbeiten“ in der Constraints-Ansicht öffnet die Maske auf dem Reiter Constraints, der Kopf bleibt beim Scrollen durch 71 Spalten stehen.
- Nicht geprüft: „Bearbeiten“ aus der Constraints-Ansicht, während die Maske schon offen ist (schaltet nur den Reiter um; der Weg ist eine Zeile in `ColumnsView`).

## Nachtrag: Code-Review (PR 72)
- Ein geänderter Check oder FK auf einer Spalte, die derselbe Entwurf löscht, ist jetzt ein Problem (`ConstraintColumnDropped`): Oracle löscht den Constraint mit der Spalte, das neue Anlegen scheiterte nach dem schon committeten `DROP COLUMN` mit ORA-00904. Umbenennen bleibt erlaubt (läuft vorher).
- Halb gelaufener Ersatz: `ColumnDesigner.Rebase` übernimmt ihn (`TakeOverReplacements`). Ein Check, dessen neue Fassung schon da ist (ein Check, den die Tabelle vorher nicht hatte, mit der neuen Bedingung), wird zu „alten löschen, neuen umbenennen“; ein FK, der gelöscht, aber nicht neu angelegt wurde, kommt als neuer Constraint mit der neuen Regel zurück. Vorher blieb `FS_CHECK_n` stehen bzw. der FK fehlte, und der Entwurf zeigte nichts mehr zu tun.
- Gezählt wird nur noch für Änderungen, die die Zeilen erneut prüfen (`TableDesign.Rechecks`, `Questions(…, constraints)`): Ein reines Umbenennen sperrte „Ausführen“ bis zum `COUNT(*)`.
- Rote Punkte nach Bereich: `DesignPlan.ConstraintProblems` (Probleme beim Löschen, Ändern, Anlegen von Constraints), Audit-Probleme sind die, die `WithAudit` hinzufügt, der Rest gehört zu den Spalten.
- `ConstraintInfo.OnDelete`/`RuleOf` statt viermal „NO ACTION ist keine Regel“; `DdlDraftMessages` zeigt Probleme, Warnungen und Skript für Index-Entwurf und Skript-Reiter; Problem-Chip ohne Schließen-Icon; die Sperrleiste folgt auch dem Umbenennen des Workspaces; `Designer_Constraints` und `.designer h2` entfernt.

## Offen / Backlog
- Reiter „Tabelle“ (Kommentar, Umbenennen) – vom Nutzer zurückgestellt.
- Reiter „Indizes“ in der Maske statt der eigenen Ansicht.
