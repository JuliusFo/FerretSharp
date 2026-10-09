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
