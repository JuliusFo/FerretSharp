---
paths:
  - "src/FerretSharp.Core/Connections/Transfer/**"
  - "src/FerretSharp.UI/Components/Connections/ConnectionImportDialog*"
  - "src/FerretSharp.UI/Components/Connections/ConnectionExportDialog*"
  - "src/FerretSharp.UI/State/Connections/ConnectionTransfer*"
---

# Verbindungen importieren und exportieren (WP-35, ADR 0025)

- Fremde Dateien nur über `ImportJson` lesen (fehlende oder anders getypte Werte sind null) und über `ImportLimits` (16 MB, 10.000 Zip-Einträge) – nie `JsonSerializer.Deserialize` auf fremde Formate.
- DBeavers `credentials-config.json`: nur `DbeaverCredentials.ReadUsers` (liest per `Utf8JsonReader` nur `user`, überschreibt den Puffer). Bei der Entwicklung **nie eine echte Credential-Datei des Nutzers entschlüsseln** (auch nicht, um Feldnamen zu sehen); Testdaten verschlüsselt der Test selbst (`EncryptForTests`).
- SQL Developer: `password` in `info` nie lesen; `NoPasswordConnection` ist auch mit gespeichertem Passwort `"TRUE"`.
- Lokale Pfade (C#-Projekt, Skriptordner, `TnsAdminPath`, Audit-Vorlage `AuditTemplatePath` seit WP-51 – alle aus `ConnectionProfile.LocalPaths`, ein neuer Pfad gehört dorthin und in `WithoutLocalPaths`) übernimmt der Import nur mit der Checkbox „Lokale Pfade übernehmen“ (Standard aus), der Export nur auf Wunsch; eine überschriebene Prod-Verbindung bleibt Prod, außer in der Vorschau anders gewählt (Entscheidungen des Nutzers, R5a, A4/A5).
- Duplikate (gleiche Adresse und User) über `OracleAddress.SameAs` – dieselbe Regel wie die Zuordnung einer App zur Verbindung (WP-33): Host/Service/SID/Alias ohne Groß-/Kleinschreibung, leer = keiner, Port genau, `TnsAdminPath` zählt nicht. Ein JDBC-URL hat seinen eigenen Parser (`JdbcUrl`, `host:port:SID`), Descriptoren liest er nicht (Backlog).
- Testen ohne echte Daten: `--import-appdata=<scratch>` (`docs/e2e-testing.md`).
