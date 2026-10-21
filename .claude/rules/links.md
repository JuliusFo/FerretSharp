---
paths:
  - "src/FerretSharp.Core/Links/**"
  - "src/FerretSharp.UI/Components/Shell/LinkOpenDialog*"
  - "src/FerretSharp.UI/State/Links/Link*"
  - "src/FerretSharp.App/Services/LinkPipe.cs"
  - "src/FerretSharp.App/Services/ProtocolRegistration.cs"
  - "src/FerretSharp.UI/State/Host/ProtocolCommand*"
  - "pages/**"
---

# Links `ferretsharp://` (WP-37, ADR 0026 mit Nachträgen)

Ein Link ist der erste Eingang, über den **fremde Eingabe** FerretSharp steuert – jede Webseite, Mail oder jedes Programm kann ihn aufrufen. Deshalb:

- **Nie SQL, nur ein lesender Tab.** Ein Link kann nichts, was die Filterleiste nicht kann: Tabelle, Filterzeilen (auch Gruppen, `g`), Sortierung, Ansicht. Spalten prüft `QueryBuilder.Validate` gegen das Schema, Werte werden Bind-Variablen, gelesen wird auf dem Leseweg; angepasst an die Tabelle des Empfängers über `FilterPresets.Fit`. Ein Link, der mehr kann (SQL, Schreiben), braucht ein eigenes ADR.
- **Immer bestätigen** (`LinkOpenDialog`): Fokus auf „Abbrechen“, nichts wird von selbst gewählt (ohne Treffer bleibt „Öffnen“ gesperrt). Ein Link zur Zeit: neue warten in der `LinkInbox`, bis der offene Dialog beantwortet ist (R6a, A25). Ein wartender Link verfällt bei abgebrochenem Passwort, neuem Verbindungsfehler oder Wechsel der Verbindung (`PendingOpen` ab R6b).
- **`TableLink.Parse` wirft nie** und liest alles als fremd: Schema/Host/Version, Namen über `OracleIdentifier.Problem`, Operatoren nur als Namen, Werteanzahl wie in der Filterleiste. Grenzen: `MaxLength` 32.768 Zeichen, 50 Zeilen (`f` + `g`), 1.000 Werte, 200 Zeichen Verbindungsname. Eine neue Grenze oder ein neuer Parameter gehört in `Parse`, in die Prüfung der Weiterleitungsseite und in `LinkPageTests`.
- **Abgeschnittene Links** (R6a, A26): `n` (Zahl der folgenden Parameter) direkt nach `v`, am Ende `e=1`; stimmt das nicht, „unvollständig“. Ohne `n` = ältere Version, öffnet wie bisher. Neue Parameter zählen in `n` mit; `v` steigt nur, wenn ein älteres FerretSharp den Link sonst **falsch** läse (wie bei Gruppen: `v=2`).
- **Nie ins Log:** weder Link noch Werte – Pipe und Dialog loggen nur die Länge bzw. den Namen der eigenen Verbindung. Auf Prod nennt der Hinweis beim Kopieren die Filterwerte (Schlüssel markierter Zeilen ohne Hinweis).
- **Kommandozeile:** Nach dem Link zählt kein Argument mehr (`CommandLine.Effective`), sonst brächte ein Link eigene Optionen mit (`--data-dir=`, `--update-source=`). Ein Neustart startet ohne den Link (`CommandLine.Restart`).
- **Pipe** (`LinkPipe`): je Datenordner, `PipeOptions.CurrentUserOnly`, eine Nachricht je Verbindung mit Zeitgrenze; der Sender schickt höchstens ein Zeichen mehr als `MaxLength` (`TableLink.ForTransfer`), damit ein zu langer Link nicht gekürzt als anderer gültiger ankommt. Fremd belegte Pipe: Backoff, einmal warnen.
- **Registrierung** nur unter `HKCU\Software\Classes\ferretsharp` (Velopack-Hooks, Einstellungen › Verbindung); Befehlszeile und „ist es unsere?“ in `ProtocolCommand` (UI, getestet; R6b). Im E2E nie klicken – sie schreibt in die Registry des Nutzers (`docs/e2e-testing.md`); zweite Starts immer mit `--data-dir`.
- **Weiterleitungsseite** (`pages/open/`): Vertrauensanker wie der Update-Kanal – sie liest den `#`-Teil mit allen Filterwerten. GitHub-Konto nie umbenennen oder aufgeben, `pages/` nur mit derselben Sorgfalt wie `release.yml` ändern. Sie lädt nichts nach (CSP `default-src 'none'`, Skript/Style per Hash, Bytes LF über `.gitattributes`), zeigt nichts vom Link an, sendet keinen Referrer.
