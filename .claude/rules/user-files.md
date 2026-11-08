---
paths:
  - "src/FerretSharp.Core/IO/**"
  - "src/FerretSharp.Core/Scripts/**"
  - "src/FerretSharp.Core/Display/**"
  - "src/FerretSharp.Core/ClrModel/Report/**"
  - "tests/FerretSharp.Core.Tests/Scripts/**"
---

# Dateien in Ordnern des Nutzers (geteilte Repositories)

SQL-Skripte (WP-77), Anzeigespalten, Ausnahmen des Berichts und Audit-Vorlagen liegen oft im Repository der Firma: Inhalt, Namen und Links darin kommen von anderen.

- Schreiben über `AtomicFile.WriteInUserFolderAsync` bzw. `AtomicJsonFile.WriteInUserFolderAsync` (eigener Temp-Name, `CreateNew`), nie `WriteAsync` mit festem `x.tmp`: Ein Link dieses Namens leitete den Inhalt in eine Datei außerhalb, `x` wurde selbst zum Link, eine eigene `x.tmp` des Nutzers wurde ersetzt (Review WP-77). `WriteAsync` nur im eigenen Datenordner.
- „Strikt unter der Wurzel“ nicht mit `TrimEndingDirectorySeparator(root) + Trenner` prüfen: Ein Laufwerk (`S:\`) behält seinen Trenner, jeder Pfad darunter galt als „außerhalb“ (Review WP-77, `ScriptPaths.Below`). Links unter der Wurzel über `FileSystemInfo.LinkTarget` erkennen, nicht über `ReparsePoint` – OneDrive-Platzhalter sind auch Reparse Points.
- Ordner im Hintergrund lesen: je Ordner „läuft schon / noch einmal“ statt einer Sperre für alle, und nach dem `await` mit `ConfigureAwait(false)` weiter – eine Freigabe hinter stillem VPN staute sonst alle 10 s einen Lauf auf, hielt die anderen Ordner auf und ließ `Directory.Exists` im UI-Thread laufen (Review WP-77). Eine fehlende geteilte Wurzel nicht still neu anlegen.
- Werte aus Workspace- und Einstellungsdateien, die von Hand geändert sein können, beim Laden prüfen (null in Listen, Pfad nur aus Anführungszeichen) und nie ungeprüft in eine geteilte Datei schreiben (Review WP-77).
