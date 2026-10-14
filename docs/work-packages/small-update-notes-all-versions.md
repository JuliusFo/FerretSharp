# Klein: Update-Dialog mit den Notizen aller Versionen dazwischen → Release offen
Auftrag (2026-10-14, Beobachtung des Nutzers): Beim Update von 3.29 auf 3.33 zeigte der Dialog nur die Notizen von 3.33.0. Stand im Backlog (WP-32, 2026-10-13); Machbarkeit vorher in einer eigenen Session geprüft. Branch `feat/update-notes-all-versions`.

## Grundlage in Velopack (1.2.161, im Quelltext geprüft)
- `GithubSource` fasst die `releases.win.json` der letzten 10 GitHub-Releases zusammen; jedes Full- und Delta-Asset trägt die `NotesMarkdown` seiner eigenen Version (`vpk pack --releaseNotes changes.md`, `release.yml`).
- `UpdateManager.CheckForUpdatesAsync` füllt `UpdateInfo.DeltasToTarget` mit allen Delta-Assets in (lokales Full-Paket, Ziel] – unabhängig davon, ob der Download später auf das Full-Paket ausweicht. Leer, wenn es kein lokales Full-Paket gibt (nur die Setup-Installation legt eines ab, Portable vermutlich nicht) oder das Ziel kein Delta hat.
- Bewusst nicht: den Feed ein zweites Mal selbst lesen (`GithubSource.GetReleaseFeed`), um auch Portable alle Versionen zu zeigen – das kostete je Prüfung eine zweite Anfrage gegen das Limit von 60 je Stunde ohne Token.

## Gebaut
- `AppUpdate(Version, Notes, NotesComplete)` (`UI/State/IAppUpdater.cs`): `Notes` ist eine Liste `VersionNotes(Version, Markdown?)`, neueste zuerst. `AppUpdate.From(target, between)` sortiert nach Versionsnummer (`ReleaseVersion.Compare`: 3.10.0 nach 3.9.0, Prerelease vor dem Release, Build-Metadaten egal), nimmt jede Version einmal (Notizen statt leer), lässt Versionen neuer als das Ziel weg und macht leere Notizen zu null. Ohne `between` (oder leer) ist `NotesComplete` false.
- `VelopackUpdater` bleibt dünn: Ziel plus `DeltasToTarget` in `AppUpdate.From`. `Downloaded` (Download wartet auf den Neustart, z. B. nach einem Neustart der App) kennt nur das Ziel.
- `UpdateDialog`: bei mehr als einer Version je Version eine Überschrift mit der Nummer (`h2.release-version`, Trennlinie dazwischen), eine Version ohne Notizen zeigt „Dieses Release hat keine Notizen.“ unter ihrer Nummer. Bei `NotesComplete == false` der Hinweis `Update_NotesOnlyTarget` („Hier sind nur die Notizen von {0} bekannt; dazwischen erschienene Versionen, falls es welche gibt, stehen auf GitHub.“) über dem Link „Alle Releases auf GitHub“. Der Hinweis steht auch, wenn es nichts dazwischen gab – ob es etwas gab, weiß die App ohne Deltas nicht.
- Tests: `AppUpdateTests` (Reihenfolge nach Nummer, nur Ziel, doppelte und leere Einträge, neuere Version als das Ziel, Versionsvergleich).

## Offen / nicht geprüft
- Kein E2E: Ein lokal gepacktes Update (`--update-source`) braucht eine Setup-Installation, und die landet im echten `%LOCALAPPDATA%\FerretSharp` des Nutzers. Beim nächsten echten Update (Setup-Installation, mindestens zwei Versionen übersprungen) prüfen, dass alle Versionen erscheinen; mit Portable erscheint vermutlich der Hinweis.
- Fehlt mitten in der Kette ein Delta (z. B. ein Release ohne Vorgänger-Download in der CI), fehlen dessen Notizen, ohne dass der Hinweis kommt.
