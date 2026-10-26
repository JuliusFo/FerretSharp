# Fixes: Update installiert die zuletzt gemeldete statt der neuesten Version → Release offen

Frage des Nutzers (2026-10-26): Meldet die App 3.31, der Nutzer lädt erst später herunter, und inzwischen ist 3.32 erschienen – welche wird installiert? Antwort: 3.31, erst nach dem Neustart bietet die Prüfung beim Start 3.32 an. Branch `fix/update-latest`.

## Ursache
- Die Prüfung während die App läuft ruhte, sobald eine neuere Version bekannt war (Nachtrag WP-32 vom 2026-10-13: „keine Prüfung, solange eine neuere Version bekannt ist“).
- „Herunterladen“ nahm das `UpdateInfo` der letzten Prüfung (`VelopackUpdater._found`); auch die Notizen im Dialog stammen aus ihr, ein Öffnen des Dialogs holt nichts nach.
- Nebenbei: „Jetzt prüfen“ bei verfügbarem Update, die Prüfung scheitert (VPN) → `Update = null`, `CheckFailed`; das gemeldete Update verschwand aus der Statusleiste.

## Fix (Entscheidung des Nutzers: beides, mit Rückfall auf die bekannte Version)
- **Prüfung vor dem Download** (`UpdateState.NewestAsync`): Der Klick auf „Herunterladen“ prüft erneut (Phase schon `Downloading`, abbrechbar, 30 s Zeitlimit). Eine andere Version ersetzt die gemeldete samt Notizen im offenen Dialog und wird geladen; scheitert die Prüfung oder antwortet nicht, die bekannte. Meldet sie keine Version mehr (Release zurückgezogen): nichts laden, „aktuell“, Dialog zu.
- **Stille Prüfung bei verfügbarem Update** (`CheckSilentlyAsync`): Die Prüfung im Hintergrund läuft jetzt auch in `Available`, ohne Phase `Checking` (die Statusleiste zeigt das Update weiter). Eine Antwort ist die Wahrheit (andere Version oder keine); eine gescheiterte ändert nichts außer `LastChecked`. Nicht bei offenem Dialog (die gelesenen Notizen bleiben), nicht beim Laden oder wenn das Update auf den Neustart wartet; ein Ergebnis, das erst nach dem Klick auf „Herunterladen“ kommt, verfällt (der Download prüft selbst).
- **„Jetzt prüfen“ scheitert bei bekanntem Update:** Phase und Fehler wie vorher (`Available` bzw. `DownloadFailed` mit dem Fehler des Downloads), das Update bleibt.
- Eine Prüfung zur Zeit: `SemaphoreSlim` statt `Interlocked` – „Jetzt prüfen“ und Hintergrund überspringen, der Download wartet auf eine laufende Prüfung (endet nach höchstens 30 s).
- `VelopackUpdater` hält jedes gefundene `UpdateInfo` je Version: Der Download fragt nach der Version, die die UI zeigt, und eine nach dem Zeitlimit aufgegebene Prüfung, die später doch endet, vertauscht nichts.

## Nicht geändert
- Ist das Update schon geladen und wartet auf den Neustart (`Ready`), wird nicht mehr geprüft: Velopack hält einen wartenden Download; nach dem Neustart bietet die Prüfung beim Start die neuere an.

## Tests
- `UpdateStateTests`: stille Prüfung ersetzt (ohne `Checking`), gescheiterte stille Prüfung ändert nichts, keine Prüfung bei offenem Dialog, „Jetzt prüfen“ scheitert bei `Available` und bei `DownloadFailed`, Download nimmt die neuere Version, die bekannte bei Fehler und bei Zeitüberschreitung, Abbrechen während der Prüfung lädt nichts, zurückgezogenes Release schließt den Dialog. Der bisherige Test „keine Prüfung mehr nach der Meldung“ prüft jetzt die stille Prüfung.
- Nicht automatisch geprüft: der echte Ablauf mit Velopack (wie bisher nur von Hand, `--update-source=<Ordner>` mit zwei Paketen).
