# WP-51 Audit-Vorlage je Systemgruppe → Release offen
Branch `wp/51-audit-template-per-group`. Anlass: Fund des Nutzers (2026-10-15). Jede Gruppe von Systemen hat ihre eigene Audit-Vorlage, FerretSharp kannte nur eine für alle (`AppSettings.AuditTemplatePath`, WP-26). Eine Audit-Tabelle mit `AUDIT_TIME TIMESTAMP(6) WITH TIME ZONE` gegen eine Vorlage mit `DATE` sperrte im Designer den Abgleich („… cannot take the values of the table (DATE)“), obwohl `DATE` gar nicht aus der Tabelle kam.

## Entscheidungen des Nutzers (2026-10-17)
- Ein Eintrag einer Gruppe, die keine Verbindung mehr hat (umbenannt), **bleibt sichtbar** und ist als verwaist markiert, bis er entfernt wird. Mitziehen wäre bei mehreren Verbindungen einer Gruppe nicht eindeutig.
- **Vorlage je Verbindung gleich mit:** im Verbindungsdialog, sie geht der Gruppe vor.
- Die Audit-Zeile nennt die gültige Vorlage **im Tooltip**.
- Eine Audit-Spalte, die schmaler ist als die Vorlage (DATE gegen TIMESTAMP WITH TIME ZONE), ergibt eine **Warnung**, die Spalte bleibt. Verbreitern per MODIFY bräuchte eine leere Spalte und wird nicht vorgeschlagen.
- Aus der Roadmap übernommen: Verbindungen ohne Gruppe nehmen die globale Vorlage.

## Gebaut
- **Auflösung (`AuditTemplateSource.For(profile)`):** Vorlage der Verbindung → der Gruppe → der Einstellungen → eingebaute. `AuditTemplateLoad` sagt, woher die Vorlage kommt (`Origin`, `Group`). Eine gesetzte, aber kaputte oder fehlende Datei wird gemeldet und nicht übersprungen: Die nächste Vorlage gehört womöglich zu einem anderen System. `Current()` ist weg; Designer und Audit-Zeile fragen mit `Active.Profile`.
- **Speicher:** `AppSettings.AuditTemplateGroups` als `SettingsMap`, Gruppenname → Datei, nach Inhalt verglichen (damit `AppSettings` ein Wert bleibt), in `settings.json` ein schlichtes Objekt; `null` von Hand gilt als leer. Gruppen werden wie in der Verbindungsliste ohne Leerraum und Groß-/Kleinschreibung verglichen (`ConnectionSearch.SameGroup`), ein Eintrag je Gruppe (`AuditGroupTemplates.With`). `ConnectionProfile.AuditTemplatePath` ist ein lokaler Pfad: Export und Import nehmen ihn nur auf Wunsch mit, ein Import behält den Pfad der vorhandenen Verbindung.
- **Datei-Cache:** `CachedFileRead` hält jetzt bis zu 32 Pfade, je Pfad eigene Frische und eigenes Datum. Bisher hielt er einen; bei mehreren Gruppen im Wechsel hätte er jede Datei bei jedem Render neu gelesen.
- **Typprüfung (`OracleTypes.FitOf`, `ValueFit`):** Der Trigger schreibt per INSERT, nicht per MODIFY. Datums- und Zeittypen nehmen einander immer an, vollständig, wenn die Spalte genug Sekundenbruchteile und genug Zeitzone hat (keine < lokal < mit Zeitzone), sonst mit Verlust. Andere Typen nur dort, wo MODIFY mit Daten geht. `AuditPlanner.Kept` nutzt das: vollständig → bleibt (eigene Spalte der Vorlage breiter als die Vorlage → Warnung „stammt vermutlich aus einer anderen Vorlage“), verbreiterbar → MODIFY wie bisher, mit Verlust → Warnung, gar nicht → Problem. Jede Meldung sagt, ob der Typ aus der Vorlage oder der Tabelle kommt (`PlanTypeIncompatibleTemplate`, `PlanTypeLossy`, `PlanTypeLossyTemplate`, `PlanWiderThanTemplate`). Das alte `Holds` (MODIFY-Regeln, TIMESTAMP → DATE ausgenommen) ist in `FitOf` aufgegangen.
- **Einstellungen:** im Abschnitt „Audit-Tabellen“ unter der globalen Vorlage „Vorlagen der Verbindungsgruppen“: jede Gruppe der Verbindungen mit Pfad, „Auswählen…“, „Obere verwenden“ und Prüfung (Fehler mit Zeile oder „gültig“); verwaiste Einträge mit Hinweis und „Entfernen“. Darunter „Vorlagen einzelner Verbindungen“, nur lesend, mit „Verbindung bearbeiten…“. Zwei neue `SettingKey`s für die Suche.
- **Verbindungsdialog:** eigener Abschnitt „Audit-Tabellen“ mit Pfad und Auswahl; der Hinweis nennt, welche Vorlage ohne Eintrag gälte. Fehler der Datei zeigt er für den gespeicherten Pfad sofort, für einen getippten erst, wenn die Datei existiert oder nach dem Speichern. Speichern blockiert eine kaputte Vorlage nicht.
- **Tooltip:** Audit-Zeile und Audit-Namen im Designer nennen die Vorlage und woher sie kommt (`AuditTemplateLabel`).
- **Tests:** Core `AuditTemplateSourceTests` (Reihenfolge, Gruppe ohne Leerraum/Groß-/Kleinschreibung, kaputte Vorlage nicht übersprungen), `AuditPlannerTests` (Fall des Nutzers, Verlust bei Vorlage und Tabelle, Problem nennt die Vorlage), `OracleTypesTests.FitOf`, `CachedFileReadTests` (mehrere Pfade, Verdrängung), `SettingsStoreTests`, `ConnectionProfileTests`; UI `AuditGroupTemplatesTests` (Liste mit verwaisten Einträgen, ein Eintrag je Gruppe, Beschriftung, Formular).

## E2E (2026-10-17)
Eigene Instanz (`--data-dir`, eigener WebView2-Ordner, deutsch, hell), Verbindungen und `settings.json` von Hand angelegt, ohne Datenbank, CDP.
- **Gefunden und behoben:** Der Verbindungsdialog zeigte den Fehler einer fehlenden Vorlage erst nach dem Speichern, auch für den schon gespeicherten Pfad. Außerdem stand das Feld im Abschnitt „C#-Modell“; es hat jetzt einen eigenen.
- Log ohne `[ERR]`.
- Nicht geprüft: Designer und Audit-Zeile gegen eine echte Datenbank (keine Test-DB in diesem Lauf). Der Fall des Nutzers ist durch `AuditPlannerTests` abgedeckt, der Tooltip nur durch den Build.

## Offen
- Die Audit-Zeile liest eine geänderte Vorlage weiterhin erst beim nächsten Laden des Tabs (Backlog seit WP-26). Das gilt jetzt auch, wenn die Gruppe einer Verbindung geändert wird.
