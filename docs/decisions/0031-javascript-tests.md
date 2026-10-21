# ADR 0031: Tests für reine JavaScript-Module mit `node --test`

- Status: akzeptiert
- Datum: 2026-10-21
- Paket: R7 (Gesamt-Review 3.44.0, C5)

## Kontext

JavaScript bleibt in FerretSharp dünn: keine Geschäftslogik, nur die Brücke zu AG Grid, Monaco und dem DOM (`CLAUDE.md` §3). Mit der Tastatur-Zeilenauswahl (3.44.0) kam aber Bedienlogik dazu, die nur im Browser laufen kann und trotzdem rechnet: welcher Taste welche Bewegung folgt, wo ein Bereich beginnt, welche Zeilen eines nachgeladenen Blocks dazugehören. Diese Auswahl bestimmt, welche Zeilen Entf zum Löschen markiert und welche kopiert werden. Der Review fand keinen einzigen Test dafür (C5), und es gibt keine JavaScript-Testumgebung.

Ein C#-Spiegel der Rechnung hätte nur den Spiegel getestet, nicht das, was im Grid läuft. Eine Engine wie Jint hätte ein neues Paket gebraucht, ein Browser-Test (Playwright) eine ganze Laufzeit.

## Entscheidung

- **Was rechnet, steht in einem reinen Modul** ohne DOM und ohne AG Grid (`wwwroot/js/gridSelection.js`). `grid.js` wendet nur noch die Ergebnisse auf das Grid an.
- **Getestet mit dem eingebauten Testläufer von Node** (`node:test`, `node:assert`): Dateien `tests/js/*.test.mjs`, keine npm-Pakete, kein `package.json`.
- **Ein Weg für alle Tests:** `dotnet test` startet sie (`UI.Tests`, `JsTests`). Ohne Node auf dem PATH wird der Test übersprungen, in der CI (`CI=true`) scheitert er dann – Node ist auf den GitHub-Runnern vorinstalliert.

## Konsequenzen

- Neue Rechnung in JavaScript (wenn sie unvermeidlich ist) gehört in ein reines Modul mit Test in `tests/js`; Geschäftslogik bleibt trotzdem in .NET.
- Die Module werden im Browser als ES-Module geladen und in Node genauso importiert: nur Sprachmittel, die beide können, keine Browser-Objekte.
- Die Regel steht in `.claude/rules/tests.md`.
