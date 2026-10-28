# Klein: Audit-Tabellen nie anlegen, nur abgleichen

Branch `feat/audit-create-setting` (von `main`), 2026-10-28.

## Anlass
Wunsch des Nutzers: eine Option, mit der FerretSharp vorhandene Audit-Tabellen weiter abgleicht, aber nie neue anlegt. Angeboten wurde das Anlegen bisher an zwei Stellen: „Audit anlegen…“ in der Audit-Zeile über den Spalten und der Haken im Reiter „Audit“ des Tabellen-Designers (bei Tabellen ohne Audit-Tabelle und bei neuen Tabellen).

Entscheidung des Nutzers: ein Schalter für alle Verbindungen (global wie „Audit im Designer abgleichen“, `small-audit-sync-default.md`), nicht je Gruppe oder Verbindung wie die Vorlage.

## Gebaut
- Core `AppSettings.CreateAuditTables` (Standard an, also wie bisher; ältere `settings.json` ohne den Wert bleiben an).
- UI State `ColumnDesigner.CanAudit(hasAuditTable, createAllowed)`: Der Haken nimmt bei vorhandener Audit-Tabelle immer teil, sonst nur, wenn Anlegen erlaubt ist. `AuditAtStart` bekommt die Einstellung dazu und startet nie mit einem Haken, der anlegen würde.
- `TableDesigner`: Haken gesperrt (Tooltip nennt die Einstellung), und der Plan nimmt Audit nur auf, wenn `CanAudit` – ein Haken aus einem offenen Entwurf von vor dem Umschalten legt also nichts an.
- `AuditStatusLine`: Ohne Audit-Tabelle und mit ausgeschalteter Einstellung bleibt die Zeile leer (statt „Audit anlegen…“). Steht nur der Trigger da und fehlt die Audit-Tabelle, sagt die veraltete Zeile „Anlegen ausgeschaltet“ statt „Abgleichen“ (Code-Review, unten).
- Einstellungen › Audit-Tabellen: Schalter „Audit-Tabellen anlegen“ (`SettingsChoice`, `SettingKey.AuditCreateTables`, Eintrag in `SettingsCatalog`), Texte Englisch und Deutsch.
- Tests: `SettingsStoreTests` (Standard, Rundreise), UI `ColumnDesignerTests` (Startregel mit Einstellung, `CanAudit`).

## E2E
Nicht geprüft: Die Regeln sind getestet, die Anbindung sind eine Bedingung in der Audit-Zeile und Sperre plus Planbedingung im Designer nach dem Muster von `small-audit-sync-default.md`.

## Nachtrag: Code-Review (2026-10-28)
Reviewer-Agent, vier kleine Befunde, kein schwerer. Entscheidung des Nutzers: „Alle, Variante a“.
- Behoben: Trigger da, Audit-Tabelle fehlt, Anlegen aus – die Zeile bot „Abgleichen“ an, der Designer sperrte dann den Haken. Jetzt `AuditStatus.TableMissing` und `AuditStatusSummary.CanSync` (auf `ColumnDesigner.CanAudit`); statt des Links steht grau „Anlegen ausgeschaltet“ mit dem Tooltip des Designers (Variante a; b wäre nur Ausblenden gewesen).
- Behoben: Ob der Haken in den Plan kommt, stand nur in der Komponente. Jetzt `ColumnDesigner.AuditTakesPart`, getestet mit einem Haken, der über `Rebase` erhalten bleibt; Designer nutzt es für Plan und Haken.
- Behoben: Backlog-Punkt „Kontextmenü im Explorer“ nennt die Einstellung.
- Behoben: Der Tooltip `Audit_DesignerCreateOff` nannte den Pfad „Einstellungen › Audit-Tabellen“ als festen Text; jetzt aus `Settings_Title` und `Settings_Audit` zusammengesetzt (`AuditStatusSummary.CreateOffHint`).
- Vorher schon da, nicht behoben: Scheitert das Laden des Audit-Zustands im Designer (Verbindung weg), gilt das wie „keine Audit-Tabelle“ – mit Anlegen an plant er dann CREATE TABLE für eine vorhandene Audit-Tabelle (ORA-00955). Die Änderung macht es nicht schlimmer.
- E2E der neuen Zeile: nicht geprüft (wie oben; die Regel ist getestet, die Anbindung eine Bedingung).
