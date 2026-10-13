# UI end-to-end prüfen

Aus der `CLAUDE.md` ausgelagert (Stand 3.14.0). Vor jedem E2E-Lauf lesen. Die Sicherheitsregeln (keine echten Nutzerdaten, `ferret-sample` nicht anfassen) stehen zusätzlich in der `CLAUDE.md`.

## Testinstanz und Test-DB
- App mit `--data-dir=<scratch>`, `--theme=dark|light` und `--lang=en|de` (Sprache der Oberfläche, Standard Englisch; Texte in Selektoren daran ausrichten) starten, Umgebungsvariable `WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS=--remote-debugging-port=9333` setzen und die Seite per Chrome DevTools Protocol (`Runtime.evaluate`) bedienen; Screenshots per `PrintWindow` vom App-Fenster.
- Für eine Test-DB einen eigenen Container starten (`gvenzl/oracle-free:23-slim-faststart`, `APP_USER`/`APP_USER_PASSWORD`) und danach gezielt per Name entfernen – am einfachsten mit `tools/sample-db/New-SampleDb.ps1 -Name <eigener Name> -Port <freier Port>` (Beispielschema inkl. VERTRAG mit 71 Spalten). Der Container `ferret-sample` auf Port 1522 ist die Beispiel-DB des Nutzers (in seinen echten Verbindungen eingetragen) – **nicht anfassen**.
- Im Credential Manager angelegte Test-Einträge über die App wieder löschen.
- Läuft schon eine FerretSharp-Instanz des Nutzers (z. B. aus Visual Studio), teilt sich eine zweite Instanz deren WebView2-Datenordner und stürzt mit Debug-Port mit `0x8007139F` ab → zusätzlich `WEBVIEW2_USER_DATA_FOLDER=<scratch>` setzen und in einen eigenen Ordner bauen (`dotnet build src/FerretSharp.App -o <scratch>`), weil der Debug-Output dann gesperrt ist. Immer nur **eine** App-Instanz mit Debug-Port starten: Zwei Instanzen mit unterschiedlichen Browser-Argumenten (z. B. zwei Debug-Ports) lassen die zweite beim Start abstürzen.
- Nach E2E-Läufen das Log der Testinstanz auf `[ERR]` prüfen.

## Echte Eingabe statt synthetischer Events
- Mausinteraktionen mit echter Windows-Eingabe prüfen (`SendInput` über `realclick.ps1` im Scratchpad: `ClientToScreen` des Fensters + CSS-Position × `devicePixelRatio`), nicht nur per CDP. Nur so läuft die Eingabe durch den WPF-Host wie beim Nutzer (siehe `@ondblclick`/`auxclick` in `src/FerretSharp.UI/CLAUDE.md`).
- `realclick.ps1`: Die `INPUT`-Struktur muss auf x64 genau 40 Byte haben (`type` + `MOUSEINPUT`, **kein** zusätzliches Füllfeld) – sonst lehnt `SendInput` mit Fehler 87 ab und es kommt still kein Klick an (in WP-28 gefunden). Rückgabewert von `SendInput` prüfen und vor dem Klick abbrechen, wenn `GetForegroundWindow()` nicht das App-Fenster ist.
- Vorher prüfen, dass der Klick ankommt (z. B. `mousedown`-Listener per CDP): Holt Windows das App-Fenster nicht in den Vordergrund (`SetForegroundWindow` wird verweigert, wenn der Nutzer gerade woanders arbeitet), landet der Klick im Fenster, das dort oben liegt – dann abbrechen statt weiterklicken (in WP-12 passiert).
- Screenshots (`PrintWindow`) enthalten die 31 px hohe Titelleiste: Bildkoordinaten ≠ CSS-Koordinaten, Klickziele immer per `getBoundingClientRect` bestimmen.
- Rechtsklick und Mittelklick mit `Input.dispatchMouseEvent` (echte Maus). `Input.dispatchKeyEvent`-Modifier: Alt 1, Ctrl 2, Meta 4, Shift 8.
- Synthetische Events erreichen Blazor, ersetzen aber keinen Test mit echter Eingabe (`Input.dispatchMouseEvent`/`dispatchKeyEvent`), sonst bleiben Fehler wie das fehlende `auxclick` unentdeckt.

## Updates (WP-32)
- Nie `Setup.exe` im E2E starten: Es installiert nach `%LocalAppData%\<Pack-Id>`, legt Verknüpfungen an und startet die App **ohne** `--data-dir`, also auf den echten Daten des Nutzers. Stattdessen das Portable-Zip im Scratchpad entpacken und `current\FerretSharp.exe` mit `--data-dir=<scratch>` und `--update-source=<scratch>\releases` starten.
- Testversionen: `dotnet publish … -p:Version=3.x.y -o <scratch>\pub-…`, dann `dotnet vpk pack -u FerretSharpE2E -v 3.x.y -p <pub> -e FerretSharp.exe --releaseNotes <md> -o <scratch>\releases` (eigene Pack-Id, nie `FerretSharp`); das Portable-Zip der älteren Version vor dem Packen der neueren beiseitelegen (gleicher Dateiname).
- Der Updater startet die neue Version mit denselben Argumenten und Umgebungsvariablen (Debug-Port, WebView2-Ordner) – CDP geht danach am selben Port weiter. Ein fehlender Ordner als Quelle gilt bei Velopack als leerer Feed („aktuell“); für eine fehlgeschlagene Prüfung ein kaputtes `releases.win.json` nehmen.

## Zwischenablage und native Dialoge
- Die Zwischenablage über `Get-Clipboard` prüfen. **Kein `navigator.clipboard.readText()`** im WebView aufrufen: Es öffnet eine Berechtigungsabfrage, die als zusätzliches CDP-Target (`edge://permission-request-dialog/`) vor der App-Seite in `/json` steht; das CDP-Skript wählt deshalb das Target mit der URL `https://0.0.0.1/`.
- CDP-Klicks sind keine Nutzergeste (`navigator.clipboard.writeText` schlägt fehl) → Zwischenablage per CDP durch eine Mitschrift ersetzen.
- **Native Dialoge (Speichern unter) nicht per UI Automation bedienen**: Sie öffnen sich in den echten Ordnern des Nutzers (Dokumente, OneDrive). Ein Fehlgriff traf dort einen Ordner-Eintrag statt des Dateinamenfelds, und der Dialog speicherte die Testdatei im Dokumente-Ordner (in WP-07 passiert, Datei wurde ins Scratchpad verschoben). Export-Inhalte über die Zwischenablage prüfen; den Speichern-Pfad höchstens bis zum Öffnen des Dialogs testen.

## Kniffe
- Nach einem Klick auf einen Tab ist `.page.active` noch kurz die alte Seite → auf etwas Spezifisches des Ziels warten.
- Im Infinite Row Model kennt das Grid anfangs nur ~501 Zeilen; `scrollTop` weiter unten wird abgeschnitten.
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
