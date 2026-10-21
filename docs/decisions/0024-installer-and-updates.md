# ADR 0024: Installer und Update-Hinweis – Velopack aus den GitHub-Releases, nur melden, eine Instanz je Datenordner

- Status: akzeptiert
- Datum: 2026-10-13
- Paket: WP-32

## Kontext

Bis 3.28 gab es FerretSharp nur als Zip: Bei jedem Release herunterladen, entpacken, den alten Ordner ersetzen – beim Nutzer (12+ Verbindungen im Alltag) und bei Kollegen. Gewünscht (Planung 2026-10-12, `docs/roadmap.md`): einmal installieren, danach meldet die App neue Versionen und aktualisiert sich auf Klick.

Entscheidungen des Nutzers: Downloads je Release sind `Setup.exe` und ein Portable-Zip, das sich ebenfalls aktualisiert; die App **meldet nur** (Prüfung beim Start, abschaltbar, und „Jetzt prüfen“), Herunterladen und Installieren erst nach Klick; Quelle sind die GitHub-Releases des öffentlichen Repos ohne Token; unsigniert (Signieren steht mit Kosten und SignPath im Backlog). Beim Start des Pakets dazu (2026-10-13): **eine Instanz je Datenordner**.

## Entscheidung

**Velopack 1.2.161** (MIT; Nachfolger von Squirrel.Windows): `vpk pack` macht aus dem `dotnet publish`-Ordner `Setup.exe` (Installation pro Benutzer nach `%LocalAppData%\FerretSharp`, keine Admin-Rechte, Verknüpfungen auf Desktop und Startmenü), Portable-Zip, volles und Delta-Paket und den Feed `releases.win.json`. Die App fragt den Feed über `GithubSource` (öffentliche Releases, keine Pre-Releases). Alternativen: MSIX (verlangt eine Signatur, auch beim Installieren von Hand), Inno Setup plus eigene Update-Prüfung (Installer und Updater selbst bauen, keine Delta-Pakete). Velopack bringt Installer, Delta-Updates und den Neustart in einem Werkzeug, mit einem lokalen dotnet-Tool im Release-Workflow.

**Eigener Einstiegspunkt** (`Program.Main`, `App.xaml` ist eine `Page` statt `ApplicationDefinition`): Zuerst `VelopackApp.Build().SetAutoApplyOnStartup(false).Run()` – Velopacks Hooks (Installieren, Update, Deinstallieren) starten die App mit eigenen Argumenten und enden dort; ohne `SetAutoApplyOnStartup(false)` würde Velopack ein heruntergeladenes Update beim nächsten Start ungefragt installieren. Danach die Instanzsperre, dann WPF.

**Eine Instanz je Datenordner** (`Core/IO/InstanceLock`): eine Datei `instance.lock` im Datenordner, exklusiv offen (`FileShare.None`), solange der Prozess läuft – das System gibt sie auch nach einem Absturz frei; die Prozess-ID steht in `instance.pid` daneben (die gesperrte Datei ist nicht lesbar). Ein zweiter Start holt das Fenster der laufenden Instanz nach vorn (`SetForegroundWindow` – der gerade vom Nutzer gestartete Prozess darf das) und endet; geht das nicht, sagt eine Meldung, dass FerretSharp schon läuft. Gründe: Zwei Instanzen auf denselben Dateien überschrieben sich Workspaces und Einstellungen (zuletzt Speichernder gewinnt, `AtomicJsonFile` schützt nur vor kaputten Dateien), und beim Anwenden eines Updates sperrt keine zweite Instanz Dateien in `current`. Eine eigene Instanz mit `--data-dir` (E2E, Screenshots) ist ein anderer Ordner, also eine andere Sperre. Datei statt benanntem Mutex: läuft und testet sich unter Linux (CI) wie unter Windows.

**Nur melden** (`UpdateState`, UI; Schnittstelle `IAppUpdater`, Implementierung `VelopackUpdater` in `FerretSharp.App`, Muster `ISecretStore`): Prüfung beim Start (Einstellung `CheckForUpdates`, Standard an), während die App läuft (Nachtrag 2026-10-13: `UpdateCheckHours` – nie, stündlich, alle 4 Stunden (Standard), täglich – seit der letzten Prüfung; nur mit `CheckForUpdates` und solange keine neuere Version bekannt ist) und über „Jetzt prüfen“, mit 30 s Zeitlimit (`CheckForUpdatesAsync` nimmt kein Token, ein hängendes VPN soll nichts festhalten). Eine fehlgeschlagene Prüfung (offline, VPN, Rate-Limit) ist ein Hinweis in Einstellungen › Updates, kein Dialog und nichts in der Statusleiste. Eine neuere Version zeigt die Statusleiste („Update 3.x.y verfügbar“); der Dialog zeigt die Release-Notes und „Herunterladen“, danach „Jetzt neu starten“ (auch in der Statusleiste: „Neu starten für 3.x.y“). Ohne Installation (Build, `dotnet run`, das einfache Zip) ist `IsInstalled` false: keine Prüfung, die Einstellungen sagen warum.

**Release-Notes** sind der Abschnitt der Version aus `CHANGELOG.md` (`vpk pack --releaseNotes`). Der Dialog zeigt sie als einfache Blöcke (`ReleaseNotes`: Überschriften, Aufzählungen, Absätze, Inline-Code; Links nur mit Text) – kein HTML aus dem Internet in der WebView, auch nicht Velopacks `NotesHTML`.

**Zwei Klicks, Neustart über den `ExitGuard`:** „Herunterladen“ lädt (Fortschritt, abbrechbar), danach bleibt die App offen – der Nutzer entscheidet mit „Jetzt neu starten“, wann (Entscheidung des Nutzers nach dem ersten Stand, 2026-10-13: vorher startete die App direkt nach dem Download neu). „Jetzt neu starten“ schließt die App wie der Schließen-Knopf des Fensters (`ExitGuard.RequestClose`): Bei nicht committeten Änderungen kommt erst der bekannte Dialog (Commit/Rollback/Abbrechen), dann speichert die App Workspaces, schließt Sessions und beendet Hilfsprozesse (`OnExit`). Erst wenn das Fenster wirklich schließt, startet `UpdateManager.WaitExitThenApplyUpdates` den Updater, der auf das Ende des Prozesses wartet (bis 60 s), installiert und die neue Version mit denselben Argumenten startet (`--data-dir`, `--lang`). `ApplyUpdatesAndRestart` beendet den Prozess sofort und ließe das Aufräumen aus. Bricht der Nutzer die Frage ab und schließt später selbst, wird nichts installiert (`ExitGuard.UserClosing`); der Download bleibt und wird beim nächsten Start als „Neu starten für 3.x.y“ angeboten.

**Release-Workflow:** `vpk` als lokales dotnet-Tool (`dotnet-tools.json`, gleiche Version wie das Paket), `vpk download github` holt das letzte Release für das Delta (ohne Vorgänger nur volle Pakete), `vpk pack` (Pack-Id `FerretSharp`, Icon, Release-Notes, `.pdb` bleiben drin wie im Zip), `vpk upload github` legt das Release als Entwurf mit den Velopack-Dateien an, danach ergänzt `gh` Notizen und das einfache Zip und veröffentlicht. Das einfache Zip gibt es noch ein Release lang, damit der Umstieg ankommt.

**Testen ohne GitHub:** `--update-source=<Ordner>` liest einen lokalen Ordner (`SimpleFileSource`) – nur für Tests, im README nicht beworben.

## Konsequenzen

- Unsigniert: `Setup.exe` zeigt beim ersten Start die SmartScreen-Warnung; Updates aus der App heraus nicht (kein Mark of the Web).
- Daten bleiben in `%APPDATA%\FerretSharp`, eine Deinstallation lässt Verbindungen, Workspaces und Einstellungen stehen. Wer vom Zip umsteigt, installiert einmal und löscht den alten Ordner von Hand.
- GitHub erlaubt ohne Token 60 Anfragen je Stunde und Adresse – eine Prüfung je Start und höchstens eine je Stunde im Lauf reicht weit. Ein Release ohne Velopack-Dateien (bis 3.28) gibt es für die App nicht.
- Pakete sind ~95 MB (self-contained), Deltas bei kleinen Änderungen einige 100 KB.
- Eine zweite Instanz mit **anderem** `--data-dir` aus derselben Installation hält beim Update Dateien in `current` offen; das betrifft nur Tests und ist nicht abgefangen. Wie der Updater dann reagiert, ist nicht geprüft.
- Nicht automatisch geprüft (nur von Hand, E2E): der echte Ablauf mit Velopack (`VelopackUpdater`, `Program.Main`, Fenster nach vorn holen).

## Nachtrag (2026-10-17, Gesamt-Review 3.40.0, D9): Bilder in den Release-Notizen (WP-49, 3.34.0)

Seit WP-49 lädt der Update-Dialog Bilder aus dem Netz – der einzige Inhalt der Notizen, der nicht als Text im Paket steht.
- **Nur aus dem eigenen Repo:** `ReleaseNotes.OwnImageUri` lässt nur https, Host genau `raw.githubusercontent.com`, Standard-Port, ohne UserInfo/Query/Fragment, `..` oder Escapes und nur unter dem Pfad `JuliusFo/FerretSharp/…` durch; alles andere zeigt den Alt-Text. Die CSP der App-Seite (R6a, A28) erlaubt für Bilder neben der App selbst nur diesen Host.
- **Am Tag, nicht an `main`:** Der Release-Workflow schreibt die relativen Pfade auf `…/FerretSharp/vX.Y.Z/<pfad>` um – das Bild eines Releases ändert sich danach nicht mehr. Nur Bildzeilen unter `docs/images/changelog/`; ein fehlendes Bild oder eines mitten im Satz bricht das Release ab (`ChangelogTests` prüft dasselbe in der CI).
- **Folgen:** Der Dialog meldet sich bei GitHub, sobald Notizen Bilder haben (Adresse des Nutzers, wie die Update-Prüfung selbst; README › Install sagt das). Offline oder über ein VPN ohne Zugang steht der Alt-Text da. Wer das GitHub-Konto übernimmt, kann Bilder austauschen, aber keinen Code ausführen – derselbe Vertrauensanker wie Update-Kanal und Weiterleitungsseite (ADR 0026, Nachtrag R6a).
