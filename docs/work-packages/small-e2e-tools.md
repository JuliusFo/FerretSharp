# Klein: E2E-Hilfsskripte in `tools/e2e/`
Anlass (2026-10-30): Seit WP-32 kamen Klicks per `SendInput` in vielen E2E-Läufen nicht an (Bewegungen ja, `mousedown` und Tasten nein), zuletzt in WP-67; geprüft wurde dann nur per CDP. Frage des Nutzers, ob sich das beheben lässt. Branch `feat/e2e-tools`, zugleich der Backlog-Punkt aus dem Gesamt-Review 3.31.0 (Skripte ins Repo).

Ursache:
- Die Shell von Claude ist nicht eingeschränkt: mittlere Integritätsstufe, Konsolensitzung, `WinSta0\Default`, Job-Objekt ohne UI-Beschränkungen – UIPI und Sandbox scheiden aus.
- Auf dem Rechner läuft der Treiber von FACEIT Anti-Cheat (`FACEIT_AC.sys`, `SYSTEM_START`). Laut Nutzer lief FACEIT am Vorabend von WP-67; am Nachmittag danach (Dienst gestoppt, ohne Neustart) kamen eingeschleuster Klick und Taste wieder überall an: im eigenen Probefenster (Raw Input und Fensternachricht) und in einer Testinstanz (`pointerdown` bis `click`, Rechtsklick mit `auxclick`/`contextmenu`, Mittelklick, Doppelklick mit dem bekannten doppelten Mouse-down des Hosts). Die App reicht eingeschleuste Klicks also richtig weiter.

Entscheidungen des Nutzers:
- Probe und Klick-Skripte ins Repo übernehmen.

Gebaut (`tools/e2e/`, Aufrufe im `README.md` dort):
- `Test-InjectedInput.ps1`: Probe vor Stufe 3 – oberstes Fenster, das nie den Fokus nimmt (`WS_EX_NOACTIVATE`, `MA_NOACTIVATE`), bekommt einen Linksklick und F24; Raw Input (`RIDEV_INPUTSINK`, nach allen Low-Level-Hooks) und Fensternachricht; Mauszeiger danach zurück. Exit-Code 0/1.
- `Start-TestInstance.ps1`: Build nach `<Root>\app`, Start mit `--data-dir`, eigenem WebView2-Ordner und Debug-Port; lehnt einen belegten Port ab, prüft danach, dass der Port zur neuen Instanz gehört, stellt das Fenster auf eine feste Position ohne Aktivieren.
- `Invoke-RealClick.ps1`: Links/Rechts/Mitte, 1–3 Klicks, Ziehen, Hover; bricht vor jeder Eingabe ab, wenn das Fenster nicht nach vorn kommt oder ein anderes unter dem Punkt liegt; Bewegung absolut über den virtuellen Desktop.
- `Stop-TestInstance.ps1`: schließt nur einen Prozess unter `<Root>\app`, zählt `[ERR]`/`[WRN]` im Log.
- `cdp.mjs`: `eval` (Datei, wegen PowerShell 5.1), `calls` (mehrere CDP-Aufrufe in einer Verbindung, z. B. Emulation + Screenshot), `screenshot` (`fromSurface`, optional `clip`).

Fallen:
- Ein eigener Low-Level-Hook in der Probe sagt nichts: Er wird vor allen fremden Hooks aufgerufen und war direkt nach dem Setzen manchmal noch nicht aktiv. Raw Input zeigt, was hinter allen Hooks ankommt.
- Die Fenstergröße nicht im Klick-Skript ändern: Die Position kommt vorher per CDP, ein Resize danach verschiebt das Layout.
