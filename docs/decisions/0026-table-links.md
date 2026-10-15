# ADR 0026: Links auf eine Tabellenansicht – `ferretsharp://` als neuer Eingang von außen

- Status: akzeptiert
- Datum: 2026-10-15
- Paket: WP-37 (Teil 2)

## Kontext

Der Nutzer will eine Ansicht weitergeben können – „schau dir diese Aufträge an“ im Chat oder in einem Ticket –, ohne dass der Empfänger Tabelle, Filter und Sortierung von Hand nachbaut. Ein Link ist der erste Eingang, über den **fremde Eingabe** FerretSharp steuert: Jede Webseite, Mail oder jedes Programm kann `ferretsharp://` aufrufen. Bisher kam alles, was FerretSharp tut, aus der eigenen Oberfläche.

Entscheidungen des Nutzers (2026-10-14, `docs/roadmap.md`): Ein Link enthält nie SQL und öffnet nur einen Tab; er öffnet im zuletzt aktiven Workspace der Verbindung, immer als neuer Tab, nach einer Bestätigung; Links dürfen auf Prod Werte enthalten – der Schlüssel markierter Zeilen immer, weitere Filterwerte mit einem Hinweis beim Kopieren; Link-Inhalte nie ins Log. Die Verbindung findet der Empfänger über den Namen, sonst über Adresse und Schema.

## Entscheidung

**Format** (`Core/Links/TableLink`): `ferretsharp://open?v=1&c=<Verbindung>&a=<Adresse>&s=<Owner>&t=<Tabelle>&f=<Spalte>,<Operator>,<Wert>…&o=<Spalte>,asc|desc&m=<Ansicht>`. Jeder Teil wird einzeln prozentkodiert, getrennt wird vor dem Dekodieren – so überleben Kommas, `&`, `=` und `#` in Werten. Operatoren und Ansichten als camelCase-Namen wie in den JSON-Dateien (stabil gegen Umsortieren der Enums). Die Adresse als Text, den `OracleAddress.Parse` zurückliest (EZConnect mit Service, Descriptor mit SID, sonst TNS-Alias). Nur aktive Filterzeilen; der Link auf markierte Zeilen ist ein Filter auf den durchgesetzten Primärschlüssel nach den Regeln des FK-Sprungs mehrerer Zeilen (`FkNavigation.KeyFilters`) – ROWIDs nicht, sie ändern sich mit Verschieben oder Export/Import und sind in einer anderen Datenbank andere. Das Filtermodell bleibt unverändert, also weiter als LINQ übersetzbar.

**Lesen als fremde Eingabe** (`TableLink.Parse`, wirft nie): Schema `ferretsharp`, Host `open`, Version (eine höhere heißt „aus einer neueren Version“), Owner und Tabelle Pflicht, Namen über `OracleIdentifier.Problem`, Operatoren nur als Namen (keine Zahlen), Anzahl der Werte je Operator wie in der Filterleiste, kein `;` in einer IN-Liste. Grenzen: 32.768 Zeichen, 50 Filter, 1.000 Werte, 200 Zeichen Verbindungsname. Danach geht der Link den normalen Weg: Spaltennamen prüft `QueryBuilder.Validate` gegen das Schema, Werte werden Bind-Variablen, gelesen wird auf dem Leseweg. Der Link kann nichts, was die Filterleiste nicht kann. Wie eine Vorlage wird er an die Tabelle des Empfängers angepasst, sobald ihre Struktur da ist (`FilterPresets.Fit`): Zeilen auf fehlenden Spalten deaktiviert, nicht mögliche Sortierungen weg.

**Immer bestätigen** (`LinkOpenDialog`): Tabelle, Filter, Sortierung, Ansicht und Verbindung, mit „wird zuerst verbunden“, wenn sie nicht offen ist; Fokus auf „Abbrechen“. Ohne diesen Schritt könnte ein Klick auf einer Webseite still eine Prod-Verbindung aufbauen. Mehrdeutig → Auswahl, nichts Passendes → Hinweis mit Name und Adresse aus dem Link, kein Öffnen. Ein abgebrochenes Passwort, ein neuer Fehler der Verbindung (Verbinden gescheitert, Verbindung verloren) oder der Wechsel zu einer anderen Verbindung verwirft den wartenden Link, damit er nicht später überraschend aufgeht. Passt nur der Name, obwohl der Link eine Adresse hat, sagt der Dialog das.

**Verbindung finden** (`LinkConnectionMatch`): Name **und** Adresse zuerst; sonst die Adresse (bei mehreren eingeengt auf die mit dem Schema des Links); der Name allein nur, wenn die Adresse nichts findet. Abweichend vom Wortlaut „Name, sonst Adresse“: Eine gleichnamige Verbindung eines Kollegen kann auf eine andere Datenbank zeigen – dann soll die Adresse gewinnen. Die Tabelle sucht der Empfänger nach Owner und Name, sonst nach Name im Schema seiner Verbindung (Test und Prod heißen oft verschieden), sonst als Synonym dieses Namens.

**Übergabe an die laufende Instanz** (`FerretSharp.App`, Windows-spezifisch hinter `LinkInbox`/`IProtocolRegistration`): Windows startet `"FerretSharp.exe" "%1"`. Nach dem Link zählt kein Argument mehr (`CommandLine.Effective`): Kämen Anführungszeichen unkodiert an, könnte ein Link sonst eigene Optionen wie `--update-source=` oder `--data-dir=` mitbringen; Optionen davor (Start von Hand, E2E mit `--data-dir`) bleiben. Hält schon eine Instanz den Datenordner (`InstanceLock`, WP-32), schickt der zweite Start den Link über eine **Named Pipe je Datenordner** (Name aus dem Hash des Pfads) mit `PipeOptions.CurrentUserOnly` – nur Prozesse desselben Windows-Benutzers können schreiben – und holt das Fenster nach vorn. Der Sender schickt höchstens ein Zeichen mehr, als ein Link haben darf (`TableLink.ForTransfer`) – auf die Grenze gekürzt, könnte ein zu langer Link als ein anderer, gültiger gelesen werden. Die laufende Instanz liest höchstens eine Nachricht je Verbindung mit Zeitgrenze und legt sie in die `LinkInbox`; die Shell zeigt den neuesten Link zur Bestätigung. Ein Neustart (Update, Grafik) startet ohne den Link (`CommandLine.Restart`).

**Registrierung** unter `HKCU\Software\Classes\ferretsharp` (nur der eigene Benutzer, keine Adminrechte): in den Velopack-Hooks bei Installation; bei einem Update nur, wenn noch nichts registriert ist (eine in den Einstellungen gewählte Kopie bleibt); bei der Deinstallation entfernt, wenn sie auf diese Kopie zeigt. Portable Kopien und eigene Builds registrieren in Einstellungen › Verbindung („Links (ferretsharp://)“), dort auch Übernehmen und Entfernen.

**Log und Prod:** Weder Pipe noch Dialog loggen den Link oder seine Werte, nur die Länge bzw. den Namen der eigenen Verbindung. Beim Kopieren eines Links mit Filterwerten von Prod nennt der Hinweis die Werte; der Link auf markierte Zeilen (nur Schlüssel) kommt ohne Hinweis.

## Konsequenzen

- Ein Link ist so mächtig wie die Filterleiste und nicht mehr; ein neuer Eingang von außen (z. B. ein Link, der SQL ausführt) bräuchte ein eigenes ADR. Die Lese- und Schreibsperren (ADR 0006) bleiben unberührt.
- Links tragen Werte in Chat-Verläufe und Tickets – auf Prod nur mit Hinweis beim Kopieren, das Weitergeben bleibt Sache des Nutzers.
- Ein zweiter Start mit anderem `--data-dir` ist eine andere Pipe; ein Link öffnet immer in der Instanz des Standard-Datenordners (Windows startet ohne Argumente außer dem Link).
- Die Registrierung ist Windows-spezifisch; Core und UI kennen nur `TableLink`, `LinkInbox` und `IProtocolRegistration`.
