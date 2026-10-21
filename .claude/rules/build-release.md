---
paths:
  - ".github/workflows/**"
  - "pages/**"
  - "Directory.Build.props"
---

# CI, Release, Weiterleitungsseite

Vor jeder Änderung hier `docs/release.md` lesen (Versionierung ADR 0002, Velopack ADR 0024).

- **Version** nur in `Directory.Build.props` (`<Version>`); `release.yml` bricht ab, wenn der Tag nicht passt. Der Push des Tags `vX.Y.Z` veröffentlicht – erst taggen, wenn der CHANGELOG-Abschnitt steht, und nur nach Rückfrage beim Nutzer. `TreatWarningsAsErrors` und `NeutralLanguage=en` gelten für alle Projekte.
- **Release- und Pages-Workflow:** Actions per Commit-SHA gepinnt (Tag als Kommentar), `concurrency` ohne Abbruch – zwei Tags kurz hintereinander laufen nacheinander, sonst bricht die Delta-Kette (R6a, A35). Beim Aktualisieren einer Action SHA und Kommentar zusammen ändern.
- **Bilder im Changelog** (WP-49): nur als eigene Zeile unter `docs/images/changelog/`; der Workflow schreibt sie auf `raw.githubusercontent.com/<Repo>/<Tag>/` um, der Update-Dialog zeigt nur diese (`ReleaseNotes.OwnImageUri`). Die Regel steht dreimal (Workflow, `ReleaseNotes`, `ChangelogTests`) – zusammen ändern.
- **`pages/`** ist die Weiterleitungsseite für Links (`.claude/rules/links.md`): ein Vertrauensanker wie der Update-Kanal. Bytes LF (`.gitattributes`), Skript und Style per Hash in der CSP – nach jeder Änderung die Hashes neu rechnen, `LinkPageTests` prüft sie. `pages.yml` veröffentlicht nur diesen Ordner.
- **E2E der Seite:** über einen lokalen Server im eingebauten Browser (`python -I -m http.server <port> --bind 127.0.0.1 --directory pages`); als Datei geöffnet fehlt der `#`-Teil. Die Registrierung `ferretsharp://` nie klicken – sie schreibt in die Registry des Nutzers (`docs/e2e-testing.md`).
