# UI end-to-end prüfen

Aus der `CLAUDE.md` ausgelagert (3.14.0), nachgezogen bis 3.31.1. Vor jedem E2E-Lauf lesen. Die Sicherheitsregeln (keine echten Nutzerdaten, `ferret-sample` nicht anfassen) stehen zusätzlich in der `CLAUDE.md`.

## Testinstanz und Test-DB
- App mit `--data-dir=<scratch>`, `--theme=dark|light` und `--lang=en|de` (Sprache der Oberfläche, Standard Englisch; Texte in Selektoren daran ausrichten) starten, Umgebungsvariable `WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS=--remote-debugging-port=9333` setzen und die Seite per Chrome DevTools Protocol (`Runtime.evaluate`) bedienen; Screenshots per `PrintWindow` vom App-Fenster.
- Für eine Test-DB einen eigenen Container starten (`gvenzl/oracle-free:23-slim-faststart`, `APP_USER`/`APP_USER_PASSWORD`) und danach gezielt per Name entfernen – am einfachsten mit `tools/sample-db/New-SampleDb.ps1 -Name <eigener Name> -Port <freier Port>` (Beispielschema inkl. VERTRAG mit 71 Spalten). Der Container `ferret-sample` auf Port 1522 ist die Beispiel-DB des Nutzers (in seinen echten Verbindungen eingetragen) – **nicht anfassen**. Genau das sind die Vorgaben des Skripts: **nie ohne `-Name` und `-Port` aufrufen**, und `-Force` (löscht einen vorhandenen Container samt Daten) nur zusammen mit dem eigenen Namen.
- Im Credential Manager angelegte Test-Einträge über die App wieder löschen.
- Läuft schon eine FerretSharp-Instanz des Nutzers (z. B. aus Visual Studio), teilt sich eine zweite Instanz deren WebView2-Datenordner und stürzt mit Debug-Port mit `0x8007139F` ab → zusätzlich `WEBVIEW2_USER_DATA_FOLDER=<scratch>` setzen und in einen eigenen Ordner bauen (`dotnet build src/FerretSharp.App -o <scratch>`), weil der Debug-Output dann gesperrt ist. Immer nur **eine** App-Instanz mit Debug-Port starten: Zwei Instanzen mit unterschiedlichen Browser-Argumenten (z. B. zwei Debug-Ports) lassen die zweite beim Start abstürzen.
- Nach E2E-Läufen das Log der Testinstanz auf `[ERR]` prüfen.

## Echte Eingabe statt synthetischer Events
Drei Stufen, von schwach nach stark:
1. **DOM-Events** per `Runtime.evaluate` (`element.click()`, `dispatchEvent`): erreichen Blazor, sonst nichts – kein Fokuswechsel, keine Nutzergeste.
2. **CDP-Eingabe** (`Input.dispatchMouseEvent`/`dispatchKeyEvent`): läuft durch Chromiums Eingabeweg (Fokus, `detail`, Rechts- und Mittelklick, Tasten mit Modifiern), aber **am WPF-Host vorbei**.
3. **Windows-Eingabe** (`SendInput`): läuft wie beim Nutzer durch den WPF-Host (`WebView2CompositionControl`). Nur so fallen Fehler wie das fehlende `auxclick` oder das doppelte Mouse-down beim Doppelklick auf (`.claude/rules/ui.md`).

- Mausinteraktionen deshalb mit Stufe 3 prüfen (`realclick.ps1`: `ClientToScreen` des Fensters + CSS-Position × `devicePixelRatio`), Stufe 2 reicht für Tastatur und als schneller Durchlauf. Die Hilfsskripte (`realclick.ps1`, CDP-Aufrufe per `node`) liegen nicht im Repo, sondern entstehen je Session im Scratchpad (Backlog: nach `tools/e2e/`).
- `realclick.ps1`: Die `INPUT`-Struktur muss auf x64 genau 40 Byte haben (`type` + `MOUSEINPUT`, **kein** zusätzliches Füllfeld) – sonst lehnt `SendInput` mit Fehler 87 ab und es kommt still kein Klick an (in WP-28 gefunden). Rückgabewert von `SendInput` prüfen und vor dem Klick abbrechen, wenn `GetForegroundWindow()` nicht das App-Fenster ist.
- Vorher prüfen, dass der Klick ankommt (z. B. `mousedown`-Listener per CDP): Holt Windows das App-Fenster nicht in den Vordergrund (`SetForegroundWindow` wird verweigert, wenn der Nutzer gerade woanders arbeitet), landet der Klick im Fenster, das dort oben liegt – dann abbrechen statt weiterklicken (in WP-12 passiert).
- Screenshots (`PrintWindow`) enthalten die 31 px hohe Titelleiste: Bildkoordinaten ≠ CSS-Koordinaten, Klickziele immer per `getBoundingClientRect` bestimmen.
- Rechts- und Mittelklick per CDP mit `Input.dispatchMouseEvent` (`button: right|middle`), danach mit `SendInput` gegenprüfen. `Input.dispatchKeyEvent`-Modifier: Alt 1, Ctrl 2, Meta 4, Shift 8.

## Updates (WP-32)
- Nie `Setup.exe` im E2E starten: Es installiert nach `%LocalAppData%\<Pack-Id>`, legt Verknüpfungen an und startet die App **ohne** `--data-dir`, also auf den echten Daten des Nutzers. Stattdessen das Portable-Zip im Scratchpad entpacken und `current\FerretSharp.exe` mit `--data-dir=<scratch>` und `--update-source=<scratch>\releases` starten.
- Testversionen: `dotnet publish … -p:Version=3.x.y -o <scratch>\pub-…`, dann `dotnet vpk pack -u FerretSharpE2E -v 3.x.y -p <pub> -e FerretSharp.exe --releaseNotes <md> -o <scratch>\releases` (eigene Pack-Id, nie `FerretSharp`); das Portable-Zip der älteren Version vor dem Packen der neueren beiseitelegen (gleicher Dateiname).
- Notizen mit Bildern (WP-49): eine Notizen-Datei mit eigenen Bildern einer schon getaggten Version (`…/v3.33.0/docs/images/grid-dark.png`), einem fehlenden eigenen Bild und einem fremden Host packen; der Dialog zeigt das erste, für die anderen den Alt-Text. Das Portable-Zip hat kein lokales Full-Paket: Der Dialog zeigt nur die Notizen des Ziels mit Hinweis.
- Der Updater startet die neue Version mit denselben Argumenten und Umgebungsvariablen (Debug-Port, WebView2-Ordner) – CDP geht danach am selben Port weiter. Ein fehlender Ordner als Quelle gilt bei Velopack als leerer Feed („aktuell“); für eine fehlgeschlagene Prüfung ein kaputtes `releases.win.json` nehmen.

## Verbindungen importieren (WP-35)
- Die Suche nach DBeaver und SQL Developer schaut sonst in den echten `%APPDATA%` des Nutzers → immer `--import-appdata=<scratch>` mit erfundenen Dateien (`DBeaverData\workspace6\<Projekt>\.dbeaver\data-sources.json`, `SQL Developer\system<Version>\o.jdeveloper.db.connection\connections.json`). „Datei wählen…“ öffnet einen nativen Dialog – nicht bedienen.
- Nie eine echte `credentials-config.json` des Nutzers entschlüsseln oder in den Scratchpad kopieren; eine DBeaver-Datei ohne Credentials testet den Weg „User von Hand“.
- Passwortabfrage mit „Im Credential Manager speichern“ **abgewählt** testen – dann entsteht kein Eintrag, den man wieder löschen müsste.

## Zwischenablage und native Dialoge
- Die Zwischenablage über `Get-Clipboard` prüfen. **Kein `navigator.clipboard.readText()`** im WebView aufrufen: Es öffnet eine Berechtigungsabfrage, die als zusätzliches CDP-Target (`edge://permission-request-dialog/`) vor der App-Seite in `/json` steht; das CDP-Skript wählt deshalb das Target mit der URL `https://0.0.0.1/`.
- CDP-Klicks sind keine Nutzergeste (`navigator.clipboard.writeText` schlägt fehl) → Zwischenablage per CDP durch eine Mitschrift ersetzen.
- **Native Dialoge (Speichern unter) nicht per UI Automation bedienen**: Sie öffnen sich in den echten Ordnern des Nutzers (Dokumente, OneDrive). Ein Fehlgriff traf dort einen Ordner-Eintrag statt des Dateinamenfelds, und der Dialog speicherte die Testdatei im Dokumente-Ordner (in WP-07 passiert, Datei wurde ins Scratchpad verschoben). Export-Inhalte über die Zwischenablage prüfen; den Speichern-Pfad höchstens bis zum Öffnen des Dialogs testen.

## Kniffe
- Links (WP-37): einen zweiten Start **immer mit `--data-dir`** und dem Link als Argument – ohne geht der Link über die Pipe an die echte Instanz des Nutzers (bzw. startet auf seinen Daten). Die Registrierung `ferretsharp://` nicht im E2E klicken: Sie schreibt in die Registry des Nutzers. Die Weiterleitungsseite (`pages/`) im eingebauten Browser über einen lokalen Server prüfen (`python -I -m http.server <port> --bind 127.0.0.1 --directory pages`): als Datei geöffnet fehlt der `#`-Teil; nur das Fragment zu ändern lädt die Seite nicht neu.
- Abfragen zählen (eine statt zwei nach einer Aktion): Das Log der Testinstanz hat je Grid-Abfrage eine Zeile `[DBG] Rows 0+ of OWNER.TABELLE (n filters)` (WP-37).
- Nach einem Klick auf einen Tab ist `.page.active` noch kurz die alte Seite → auf etwas Spezifisches des Ziels warten.
- Im Infinite Row Model kennt das Grid anfangs nur ~501 Zeilen; `scrollTop` weiter unten wird abgeschnitten.
- AG-Grid-Optionen prüfen: `agGrid.enableDevValidations()` per CDP, dann einen Tab öffnen – das neue Grid meldet veraltete oder falsche Optionen in der Konsole und als Panel im Grid (seit 36 sonst still, WP-36).
- Hell/dunkel bei `--theme=dark`: Die Einstellung greift erst beim nächsten Start; zum Umschalten in derselben Sitzung `Emulation.setEmulatedMedia` (`prefers-color-scheme`) und den Screenshot in **derselben** CDP-Verbindung (die Emulation endet mit ihr).
- Text im LOB-Dialog geht erst beim `change` nach .NET → mit echter Maus übernehmen.
- Monaco über `getModels()…setValue`/`setPosition` füttern, `triggerSuggest` erst nach > 250 ms.
- Ausdrücke mit Anführungszeichen über eine Datei an `node` geben (PowerShell 5.1 verliert sie).
- Abfragen über `Kunde` im Beispielmodell scheitern absichtlich mit ORA-00904 (Drift).

## SQL*Plus im Container
- Statements per PowerShell-Pipe an `docker exec … sqlplus` bekommen ein BOM vorangestellt (SP2-0734) → `docker exec <name> bash -c "echo '…' | sqlplus …"` oder Skriptdatei per `docker cp`.
- SQL*Plus-Testskripte: `SET DEFINE OFF` (sonst wird `&` als Variable abgefragt) und `NLS_LANG=…AL32UTF8` für Umlaute.

## README-Screenshots
- `docs/images/`, je `-light`/`-dark`: eigene Instanz mit Beispiel-DB und verknüpftem `samples`-Projekt, Fenster per `SetWindowPos` auf 1440×900 (ohne Aktivieren), `PrintWindow`, dann auf den Client-Bereich zuschneiden (x 8–1431, y 31–891 bei 100 % Skalierung: unsichtbare Ränder und Titelleiste weg). Theme über `--theme`, Sprache über `--lang=en` (Screenshots für GitHub auf Englisch), die App stellt Workspaces und Tabs nach dem Neustart wieder her (Ergebnisse neu ausführen).
- `docs/images/social-preview.png` (1280×640) lädt der Nutzer von Hand unter Settings → Social preview hoch (keine API).
- `PrintWindow` liefert ein veraltetes Bild, wenn das Fenster verdeckt ist (WebView2 zeichnet dann nicht neu): Das DOM hatte die Änderungen, das Bild zeigte den Stand von Minuten vorher (README-Screenshots 3.26, nach dem Wechsel des Nutzers in ein anderes Fenster). Zuverlässiger ist CDP `Page.captureScreenshot` (`fromSurface: true`, optional `clip` in CSS-Pixeln): Es rendert auch verdeckt und liefert bei 100 % Skalierung genau den Client-Bereich 1424×861, ohne das Fenster zu aktivieren.
- Ansichten, die höher sind als das Fenster (Designer mit DDL und C#): Fenster per `SetWindowPos` vorübergehend höher stellen (z. B. 1440×1400, ohne Aktivieren), den Inhaltsbereich per `clip` aufnehmen, danach zurück auf 1440×900.
