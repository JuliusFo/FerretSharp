# Klein: Icons in der Bereichsliste der Einstellungen

Branch `feat/settings-area-icons` (von `main`), 2026-11-06, unveröffentlicht. PR #134.

Idee des Nutzers: in den Einstellungen Icons für die Bereiche. Claude riet dazu (neun Bereiche, die Liste ist zum Springen da) und wies auf ADR 0035 hin: Die naheliegenden Motive waren fast alle schon mit anderer Bedeutung vergeben (`plug` = verbinden, `database` = SQL-Editor, `code` = LINQ-Konsole, `activity` = Statements der App, `download` = speichern, `table` = Tabelle, `sliders-horizontal` = die Einstellungsseite selbst).

Entscheidungen des Nutzers:
- Erst ein Mockup (PNG, hell, dunkel mit Suche, Alternativen für drei Bereiche), dann bauen.
- Datenansicht `table-properties`, Verbindung `server` (im Mockup lagen `rows-3` und `server` zu nah beieinander).
- Im Code-Review „Alle“ mit den vorgeschlagenen Motiven: Anzeigespalten der Stern, Audit `file-clock`, C#-Modell `file-code`.
- Icons nur in der Liste links, nicht in den Überschriften der Abschnitte (Vorschlag von Claude, nicht widersprochen).

Gebaut:
- `Icons.cs`: Darstellung `palette`, Datenansicht `table-properties`, C#-Modell `file-code`, Verbindung `server`, Audit-Tabellen `file-clock`, Updates `circle-arrow-up`, Diagnose `stethoscope`, Tastenkürzel `keyboard` (Lucide 1.55, unverändert). Anzeigespalten nutzen `Star`, das Icon der Anzeigespalte im Spaltenmenü. `Icons.For(SettingsPart)` nennt jeden Bereich, ein unbekannter wirft; `IconsTests` prüft, dass jeder Bereich ein eigenes Icon hat. `SettingsCatalog` bleibt ohne Markup.
- `SettingsPage`: Icon vor dem Namen. CSS: `.settings-nav-item` als Flex-Zeile (8 px Abstand), Icon 16 px in der Textfarbe (aktiv Akzent, gedämpft bei der Suche `--faint`).

E2E (eigene Instanz mit `--data-dir`, Port 9347, ohne DB): hell und dunkel, Suche „audit“ dämpft die anderen Bereiche samt Icon, echter Windows-Klick aufs Icon wählt den Bereich. Log ohne Warnungen.

## Nachtrag: Code-Review (2026-11-06)
Ein unabhängiger Reviewer-Agent (`/branch-code-review`), zwei Durchgänge. Entscheidungen des Nutzers: „Alle“, danach „Ja, ergänzen“.

- **1 (M) Anzeigespalten mit `columns-3`:** behoben. Das ist die Grundform von `SelectColumn` (Spalte markieren), und die Anzeigespalte hat schon den Stern. Jetzt `Star`; die Commit-Message des ersten Commits („none reused“) korrigiert der Fix-Commit.
- **2 (K) Protokoll fehlte:** behoben (diese Datei). Claude hatte angenommen, kleine Änderungen stünden nur im CHANGELOG.
- **3 (K) Audit mit `history`:** behoben, `file-clock`. `history` bleibt frei für den SQL-Verlauf (Knopf „Verlauf“ hat noch kein Icon).
- **4 (K) C#-Modell `square-code` wie `Code` mit Rahmen:** behoben, `file-code` (gleicher Dateiumriss wie der Schema-Vergleich, aber anderer Inhalt und nie nebeneinander).
- **5 (K) Standardzweig `_ => Shortcuts`:** behoben, jeder Bereich ausdrücklich, Test schlägt bei einem Bereich ohne Icon an (nachgestellt).
- **6 (K, zweiter Durchgang) Summary von `Star` nannte nur das Spaltenmenü:** behoben.
- Vorher schon da, in den Backlog: Hover über einem gedämpften Bereich bleibt blass.
