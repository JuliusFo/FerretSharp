---
paths:
  - "**/*.resx"
  - "src/*/Resources/**"
---

# Texte in Englisch und Deutsch (WP-29, ADR 0017)

Vollständige Anleitung und Glossar: `docs/localization.md` – vor jedem neuen Begriff dort nachsehen.

- Neuer Text = Schlüssel in **beiden** Dateien (`<Bereich>Text.resx` Englisch, `.de.resx` Deutsch), gleiche Platzhalter; `<comment>` in der englischen Datei bei Platzhaltern und mehrdeutigen kurzen Wörtern.
- Schlüssel nach der Bedeutung, nicht nach dem Wortlaut; in der UI mit Komponente als Präfix (`Settings_LockWaitHint`). Plural über `One`/`Other` und `TextFormat.Plural`.
- Ganze Sätze mit Platzhaltern (`TextFormat.Format`), nie Satzteile zusammenkleben. Tastenkürzel nie als festen Text (`ShortcutService.Label`/`Hint`, `<Kbd>`).
- Beim Umziehen bestehender Texte den deutschen Wortlaut unverändert übernehmen – die Tests prüfen ihn.
- Englisch: US-Schreibweise, Sentence case, „you“, typografische Anführungszeichen. Deutsch: du-Form, „…“, Gedankenstrich –.
