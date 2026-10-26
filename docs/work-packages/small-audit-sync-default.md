# Klein: Audit-Haken im Tabellen-Designer per Einstellung → Release offen

Branch `feat/audit-sync-default` (von `main`), 2026-10-26.

## Anlass
Frage des Nutzers: Lässt sich einstellen, ob der Haken „Audit-Tabelle und Trigger abgleichen“ im Tabellen-Designer bei Spaltenänderungen standardmäßig gesetzt ist? Bisher nicht: Der Haken war an, sobald die Tabelle eine Audit-Tabelle hat (WP-26), sonst aus. Auftrag des Nutzers: die Einstellung bauen.

Entscheidung des Nutzers: ein Schalter für alle Verbindungen, nicht je Gruppe oder Verbindung wie die Vorlage. Er wirkt nur auf Tabellen **mit** Audit-Tabelle; bei einer Tabelle ohne Audit-Tabelle bleibt der Haken aus wie bisher.

## Gebaut
- Core `AppSettings.SyncAuditInDesigner` (Standard an, also wie bisher; ältere `settings.json` ohne den Wert bleiben an).
- UI State `ColumnDesigner.AuditAtStart(openedForAudit, hasAuditTable, syncByDefault)`: an, wenn der Designer aus der Audit-Zeile über den Spalten geöffnet wurde („Audit anlegen…“/„Abgleichen“), sonst an bei vorhandener Audit-Tabelle und eingeschalteter Einstellung. `TableDesigner` ruft es beim Laden des Audit-Zustands; eine Wahl des Nutzers im offenen Designer bleibt (`??=`, R5-Review).
- Einstellungen › Audit-Tabellen: Schalter „Audit im Designer abgleichen“ als erste Zeile (`SettingsChoice`, `SettingKey.AuditSyncInDesigner`, Eintrag in `SettingsCatalog`), Texte Englisch und Deutsch.
- Tests: `SettingsStoreTests` (Standard, Rundreise), UI `ColumnDesignerTests` (Startregel).

## E2E
Nicht geprüft: Die Startregel ist getestet, die Anbindung ist eine Zeile im Designer und ein Schalter nach dem Muster der übrigen.
