# WP-70 Kontextmenüs im Explorer und an Tabs → Release offen

Branch `wp/70-explorer-tab-menus`, 2026-10-31. Wunsch des Nutzers am selben Tag: Rechtsklick im Explorer, dabei das Menü am Tab-Kopf anpassen; als Basis für WP-68 (Synonyme) und WP-69 (Abfrage aus dem Beziehungsdiagramm) vorgezogen.

## Planung (aus `docs/roadmap.md`)
Heute hat der Explorer kein Kontextmenü, der Tab-Kopf eine schlichte Liste (Anheften, Link kopieren bei Tabellen, vier Arten zu schließen).
- **Ein Objektmenü an zwei Orten:** Rechtsklick auf einen Eintrag im Explorer und auf den Kopf eines Tabs mit Tabelle, View oder PL/SQL zeigt dieselben Objektaktionen – je Art ein Menü-Rumpf (Tabelle/View/MView/Synonym, PL/SQL), aus den Bausteinen von WP-61/66 (`ContextMenuHost`, Kopf, `QuickBar`/`QuickAction`, `MenuEntry`, Chips), Tastatur über `KeyNavigation`.
- **Kopf:** Art (Tabelle, View, MView, Synonym mit Ziel und Owner), Entity-Name, Kommentar, ungültig bzw. deaktiviert.
- **Tabelle/View – Leiste:** Öffnen, Link kopieren, „Im SQL-Editor öffnen“ (`SELECT * FROM …`, der Text kommt aus `Core/Query`), „In LINQ-Konsole öffnen“ (`db.Kunden`, wie `LinqFilter.Source`), „Im Designer bearbeiten“ (nur Tabellen des eigenen Schemas).
- **Ansicht direkt öffnen:** Chips für Struktur, Constraints, Indizes, Abhängigkeiten, Beziehungen, DDL und Verwendung (nur mit Code-Index) – `ShellState.OpenTable(table, mode)`.
- **Kopieren:** Chips für Name, `OWNER.NAME` (gequotet, wo nötig), Entity-Name und `db.<DbSet>` (nur mit C#-Modell).
- **Andere Verbindung:** Chips der verbundenen Verbindungen, öffnet die Tabelle dort (Weg aus `small-link-choose-connection`, `ShellState.OpenLinkIn`).
- **PL/SQL:** Öffnen mit Chips für Spezifikation, Body, Unterprogramme und Abhängigkeiten; Name kopieren; bei Triggern aktivieren/deaktivieren über den Schemaweg (`TriggerDdl`, mit Bestätigung wie jedes DDL).
- **Tab-Kopf:** Über dem Objektmenü der Teil des Tabs: Anheften, **„Im Explorer zeigen“** (Abschnitt wechseln, Suche leeren, Eintrag ins Bild scrollen und markieren), **„Tab duplizieren“** (neuer Tab derselben Tabelle mit denselben Filtern, Weg der FK-Sprünge `OpenFiltered`), darunter die Arten zu schließen wie heute. SQL-, LINQ- und andere Tabs bekommen nur den Teil des Tabs.
- **Spätere Pakete tragen ein:** WP-68 „Synonyme“ bzw. „Synonym löschen“, WP-69 „Abfrage bauen“ – je nachdem, welches Paket nach diesem kommt.

## Entscheidungen des Nutzers (2026-10-31)
1. **Kein Filterfeld:** Das Menü bleibt kurz, wie Spalten- und Verbindungsmenü.
2. **Leiste mit Beschriftung** wie im Spaltenmenü – es sind wenige Aktionen.
3. **Tabellen und Views löschen oder umbenennen** nicht in diesem Paket (Backlog: Papierkorb, abhängige FKs, Prod).
4. **„Tab duplizieren“ und „Im Explorer zeigen“ ohne Standardtaste**, aber in der Befehlspalette (`docs/architecture.md` 3).

## Entscheidungen in der Umsetzung
- **Reihenfolge am Tab-Kopf:** oben der Kopf des Objekts (was der Tab zeigt), dann die Zeilen des Tabs (Anheften, Duplizieren, Im Explorer zeigen), dann Leiste und Chips des Objekts, unten die Arten zu schließen. „Öffnen“ fehlt dort – ein Klick auf den Tab tut das. SQL-, LINQ-, Datensatz- und Neue-Tabelle-Tabs behalten die schlichte Liste.
- **Wirkt auf den Tab, nicht auf die Tabelle:** Aus dem Tab-Menü zeigen Ansichten und Designer genau diesen Tab (`ShellState.ShowTab(tab, mode, designer)`), auch wenn ein zweiter Tab derselben Tabelle offen ist (FK-Sprung, Duplikat); der Link und „In Verbindung öffnen“ nehmen seine angewendeten Filter, Sortierung und Ansicht mit. Aus dem Explorer wirkt es auf den ersten Tab der Tabelle bzw. öffnet einen.
- **Duplizieren** als eigene Methode (`ShellState.DuplicateTab`), nicht über `OpenFiltered`: direkt hinter dem Quell-Tab (der nicht aktiv sein muss), mit Sortierung, Ansicht und angehefteten Spalten; nur die angewendeten Filter – noch nicht angewendete Zeilen bleiben beim Quell-Tab. Nie angeheftet.
- **„Im Explorer zeigen“** über einen Auftrag in der Shell (`ExplorerReveal` mit fortlaufender Id und Verbindung): Die Explorer aller offenen Verbindungen bleiben gemountet, nur der der Verbindung des Tabs nimmt ihn, einmal. Er wählt den Abschnitt (Views nur mit eigener Abteilung), leert Suche und Quelltextsuche, markiert den Eintrag (`revealed`, kurzes Aufleuchten mit `fs-flash`) und scrollt ihn nach dem Rendern in die Mitte. Eine Tabelle eines anderen Schemas ohne Synonym (FK-Sprung) steht nicht im Explorer: Eintrag im Tab-Menü ausgegraut mit Grund, über die Taste ein Hinweis (`RevealUnavailable`).
- **Trigger aktivieren/deaktivieren** öffnet `ALTER TRIGGER … ENABLE/DISABLE` in einem neuen SQL-Tab, statt direkt auszuführen: Dort läuft es mit der Bestätigung jeder Schemaänderung (Prod-Hinweis, offene Transaktion), ohne einen zweiten Weg zum Schemaweg. Der Text kommt aus `TriggerDdl.Enable`/`Disable` (`OracleDdl`), immer mit Owner.
- **„In Verbindung öffnen“** zeigt alle anderen Verbindungen (wie die Chips für `ALTER TABLE … ADD`, `AddColumnsTargets`), nicht nur verbundene: Bei 12+ Verbindungen ist die gesuchte selten schon offen; der Weg ist der eines Links (`OpenLinkIn` + `ConnectAsync`, Passwort und Prod wie beim Verbinden).
- **Designer** für jede Tabelle (wie „Bearbeiten“ in der Spaltenansicht), nicht nur für Tabellen des eigenen Schemas; Views und MViews haben keinen Eintrag.
- **Kopf ohne Kommentar:** `TableSummary` kennt den Kommentar nicht, ihn zu laden kostete je Rechtsklick einen Roundtrip; er steht im Objektkopf der Detailansichten.
- **SQL-Text** über `QueryBuilder.SelectStarText` (Synonym, eigener Name oder `OWNER.NAME`, gequotet nur wo nötig – `OracleIdentifier.QualifyIfNeeded`); LINQ über `LinqFilter.Source(EntityExport)` und `PresentationService.LinqSourceOf`, auch ohne geladene Spalten.
- Gemeinsames aus dem Explorer herausgezogen: Art und Tooltip eines Synonyms (`ObjectMenuFacts`), die Ansichten einer PL/SQL-Einheit je Art (`PlSqlTab.ViewsOf`) und ihre Beschriftungen (`PlSqlViewLabels`).
- Kein ADR: Bausteine aus WP-61/66, kein neuer Weg zur Datenbank.

## Gebaut
- **Core:** `OracleIdentifier.QualifyIfNeeded`, `QueryBuilder.SelectStarText`, `LinqFilter.Source(EntityExport)`, `TriggerDdl.Disable` (`OracleDdl.DisableTrigger`); `ShortcutAction.DuplicateTab`/`RevealInExplorer` ohne Standardtaste mit Texten.
- **UI-State:** `ShellState.ShowTab(tab, mode, designer)`, `DuplicateTab`, `RevealInExplorer`/`RevealUnavailable`/`Reveal`, `ExplorerReveal`; `ShellActions` (Duplizieren nur mit Tabellen-Tab, Zeigen mit Tabellen- oder PL/SQL-Tab); `ObjectMenuFacts`; `PlSqlViewLabels`; `PresentationService.LinqSourceOf`.
- **UI:** `ObjectMenuHead`, `TableMenuBody`, `PlSqlMenuBody` (Components/Schema); `Explorer` mit zwei `ContextMenuHost` und der Übernahme von `ExplorerReveal`; `TabBar` mit dem neuen Menü (`KeyNavigation`); `Shell.razor` für die beiden Tasten; CSS `.ctx-object-name`, `.ctx-fact.bad`, `.object-menu-bar` (Leiste in einer Reihe), `.menu-chip.mono`/`.active`, `.table-item.revealed`.
- **Tests:** Core `OracleIdentifierTests`, `QueryBuilderTests`, `LinqFilterTests`, `TriggerDdlTests`; UI `ShellStateTabsTests` (Duplikat, Im Explorer zeigen), `ShellActionsTests`, `ObjectMenuFactsTests`.

## E2E (2026-10-31)
Eigener Container `ferret-wp70` (Port 1540, danach entfernt), eigene Instanz über `tools/e2e` (`--data-dir`, eigener WebView2-Ordner, Port 9341), zwei Verbindungen auf denselben Container (Test mit verknüpftem `samples`-Projekt, Dev ohne), Passwort nicht gespeichert; deutsch/dunkel, englisch/hell. Die Probe für eingeschleuste Eingaben ging durch.
- **Mit echter Windows-Eingabe:** Rechtsklick auf AUFTRAG im Explorer öffnet das Menü (Kopf „Tabelle · C# Auftrag“, Leiste, Ansichten, Kopieren mit `AUFTRAG`, `FERRET.AUFTRAG`, `Auftrag`, `db.Auftraege`, WP70 Dev); Klick auf „Spalten“ öffnet den Tab in der Spaltenansicht; Klick auf `FERRET.ARTIKEL` kopiert (Zwischenablage per CDP mitgeschnitten) mit Hinweis „FERRET.ARTIKEL kopiert.“
- **Per CDP:** SQL (`SELECT *\n  FROM ARTIKEL;` im neuen SQL-Tab), LINQ (`db.Artikel`), Designer (Spaltenansicht im Bearbeiten-Modus); View ohne Designer; Trigger AUFTRAG_AD (deaktiviert) mit „Im SQL-Editor aktivieren“ → `ALTER TRIGGER "FERRET"."AUFTRAG_AD" ENABLE;`; Package mit INVALID und seinen fünf Ansichten; Tab-Menü einer Tabelle (Kopf, Anheften, Duplizieren, Zeigen, Objektteil, Schließen) und eines SQL-Tabs (nur Tab-Teil); Duplizieren eines nicht aktiven Tabs landet direkt dahinter; „Im Explorer zeigen“ aus dem PL/SQL-Abschnitt mit Suche „PKG“ wechselt zu Tabellen, leert die Suche, AUFTRAG im Bild; „WP70 Dev“ fragt das Passwort, verbindet und zeigt ARTIKEL dort.
- Log beider Läufe ohne `[ERR]`/`[WRN]`.
- **Nicht geprüft:** Tastatur im Menü (↑ ↓, Enter – Baustein unverändert), „Im Explorer zeigen“ über eine selbst vergebene Taste und aus der Befehlspalette (Unit-Tests für die Regel), Prod-Verbindung als Ziel von „In Verbindung öffnen“.
