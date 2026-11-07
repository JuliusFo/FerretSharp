# Klein: Tab-Leiste – breitere Scrollbars, mehrzeilig, Tabs ziehen → Release 3.62.0

Branch `feat/tab-bar` (von `main`), 2026-11-07. Ohne WP-Nummer.

## Anlass
Aufgabe des Nutzers „Überarbeitung der Tab-Leiste“ mit drei Punkten: Machbarkeit einer mehrzeiligen Tab-Leiste per Option, dickere Scrollbar per Option, Tabs per Drag&Drop verschieben.

Entscheidungen des Nutzers (2026-11-07):
- Keine eigene Option für die Scrollbar: Die Scrollbars werden überall breiter – Tab-Leiste, Explorer und alle anderen.
- Einstellung „einzeilig/mehrzeilig“ statt zweier Optionen (mehrzeilig hat keine waagerechte Scrollbar, die beiden schließen sich aus).
- Vorschläge, so bestätigt: Daumen sichtbar 8px statt 4px; der Daumen der Tab-Leiste weiter nur unter der Maus; höchstens drei Zeilen, danach scrollt die Leiste senkrecht; ein gezogener Tab bleibt in seiner Gruppe (angeheftet bzw. nicht), Ziehen heftet nie an und löst nie.

## Gebaut
- **Scrollbars** (`ferretsharp.css`): global 14px mit 3px durchsichtigem Rand → Daumen 8px (vorher 10px/4px). Monaco behält seine eigenen. Tab-Leiste: Scrollbar 10px, Daumen 8px (1px Rand), Leiste 48px statt 42px, die Tabs bleiben 37px.
- **Daumen der Tab-Leiste erschien in der App nie** (seit `small-tab-bar-scroll`, dort nur auf einer Testseite im Browser geprüft): Chromium malt den Daumen nicht neu, wenn sich nur seine Regel ändert – auch nicht über die Klasse `hover`. Erst eine Änderung an der Scrollbar selbst malt neu: `.tabs.hover::-webkit-scrollbar` bekommt eine kaum sichtbare Tönung (`color-mix(… var(--surface) 99%, var(--text))`). Regel in `.claude/rules/ui-css.md` ergänzt.
- **Einstellung „Tab-Leiste“** unter Einstellungen › Darstellung (`AppSettings.TabBar`, `TabBarLayout.SingleRow`/`MultiRow`, `SettingKey.TabBar`, `SettingsOptions.TabBars`); Standard eine Zeile. `TabBar` folgt `AppSettingsService.Changed` und rendert nur bei dieser Einstellung neu. Mehrzeilig (`.tabs.multi`): `flex-wrap`, Tabs 37px mit eigener Linie unten, die Linie der Leiste als `inset`-Schatten (liegt unter den Tabs, zeigt sich nur rechts der letzten Zeile), höchstens 111px = drei Zeilen, dann `overflow-y: auto`; die angeheftete Gruppe klebt nicht und bricht in sich um.
- **Mitlaufen** (`dom.js scrollableTabs`): mehrzeilig kommt die Zeile des aktiven Tabs ins Bild; nach dem Umschalten der Einstellung auch, und zwar erst, wenn die Leiste wieder sichtbar ist (die Einstellungsseite verdeckt sie: Maße 0 → später über einen `ResizeObserver`).
- **Tabs ziehen** (`dom.js dragTab`, `TabBar.DragAsync`, `ShellState.MoveTab`): ab 5px Weg (`movedAway`, auch von `dragAfter` genutzt), Ziele nur Tabs der eigenen Gruppe, die Hälfte unter der Maus beim Loslassen entscheidet vor/hinter; Linie in `--accent` an der Seite des Ziels, der gezogene Tab halb durchsichtig. Am Ende der Leiste scrollt sie alle 30 ms weiter (seitwärts neben der angehefteten Gruppe bzw. oben/unten). ✕ und Pin stoppen `pointerdown` (kein Ziehen), ein Tab im Umbenennen zieht nicht. `MoveTab` meldet nur eine echte Änderung (sonst kein Speichern); getestet in `MoveTabTests`.
- `tools/e2e/Invoke-RealClick.ps1`: `-HoldMs` hält die Taste am Ende des Ziehens (Auto-Scroll prüfen).

Der gezogene Tab wird aktiv (wie in Visual Studio und Browsern), auch wenn er auf seinem Platz landet – zuerst ohne Rückfrage gebaut, im Code-Review vom Nutzer bestätigt („#3 passt“, 2026-11-07).

## Fallen
- `dragTab` meldete anfangs immer „davor“: `end` setzte die Seite für die Markierung zurück, bevor sie gemeldet wurde. Und die letzte Bewegung lag oft wenige Pixel vor dem Loslassen auf der anderen Hälfte – die Position des `pointerup` zählt.
- Der Daumen fehlt in `Page.captureScreenshot` und war im ersten `PrintWindow`-Bild trotz Klasse `hover` nicht da: geprüft über die Pixel der Scrollbar-Zeilen im `PrintWindow`-Bild; eine rote Testregel nur für den Daumen zeigte ebenfalls nichts, eine für die Scrollbar selbst schon – so fiel das Neuzeichnen auf.

## E2E
Testinstanz mit eigener DB (`New-SampleDb.ps1 -Name ferret-e2e-tabbar -Port 1531`), echte Maus (`Invoke-RealClick.ps1`), dunkel, Deutsch:
- Eine Zeile mit 16 und 39 Tabs: Leiste 48px, Tabs 37px; Daumen nur unter der Maus (Pixel der Scrollbar: 0 → 255 → 0); Ziehen nach rechts, nach links, an den Anfang, ans rechte Ende mit Auto-Scroll; Reihenfolge nach Neustart der App wieder da; unangehefteter Tab auf angehefteten: nichts passiert; angehefteter unter angehefteten: verschoben; Klick aktiviert weiter, ✕ schließt weiter (kein Ziehen vom ✕ aus).
- Mehrere Zeilen: Umschalten wirkt sofort und steht als `"tabBar": "multiRow"` in `settings.json`; fünf Zeilen, drei sichtbar, senkrechter Daumen unter der Maus; Ziehen von der dritten in die erste sichtbare Zeile; Ziehen an den oberen Rand scrollt nach oben; Einfügemarke und halb durchsichtiger Tab im Bild während des Ziehens; Umschalten hin und zurück hält den aktiven Tab im Bild.
- Explorer- und Grid-Scrollbars breiter, nichts abgeschnitten; Tab-Leiste hell per Emulation angesehen. Log der Testinstanz ohne `[ERR]`/`[WRN]`; Verbindung über die App gelöscht (Passwort mit), Container entfernt.
- Nicht geprüft: RDP, Hochkontrast, 125/150 % Skalierung.

## Nachtrag: Code-Review (2026-11-07)
Ein unabhängiger Reviewer-Agent (`/branch-code-review`), zwei Durchgänge; kein S/M, sechs K. Entscheidung des Nutzers: „Behebe alles und #3 passt.“
- **K1 Mehrzeilig: Mausrad schob die Leiste seitwärts, wenn ein Tab breiter war als sie** (schmales Fenster, langer Name mit Filter; keine Scrollbar zurück, ✕ abgeschnitten): behoben. Der Wheel-Handler lässt das Rad im Modus `multi` in Ruhe, ein Tab ist dort höchstens so breit wie die Leiste, Name und Filter kürzen sich (Ellipse), Symbol, Punkte und ✕ behalten ihre Größe. E2E mit einem künstlich langen Namen: Tab = Leistenbreite, ✕ sichtbar, Rad scrollt senkrecht, `scrollLeft` 0.
- **K2 Auto-Scroll-Timer lief weiter, wenn die Leiste mitten im Ziehen verschwand** (Trennen, andere Verbindung): per CDP nachgestellt (ziehen, „Trennen“, loslassen → ein 30-ms-Intervall blieb), behoben mit `bar.isConnected` im Timer (danach 0). `lostpointercapture` geht bei einem entfernten Element ans Document, nicht ans Element.
- **K3 „Der gezogene Tab wird aktiv“ stand als Tatsache in `architecture.md`:** vom Nutzer bestätigt, dort und oben als seine Entscheidung eingetragen.
- **K4 Leerzeile in der Added-Liste des CHANGELOG:** entfernt.
- **K5 Zwei volle Shell-Renders je Ablegen** (`MoveTab`, dann `ActivateTab`): behoben, `MoveTab(…, activate)` meldet einmal; Test `A_dropped_tab_is_moved_and_activated_in_one_change` (scheitert ohne den Fix, auch „eigener Platz, schon aktiv“ → keine Meldung).
- **K6 Zeiger wurde beim Ziehen über ✕/Pin zur Hand:** behoben (`.tabs.dragging *` mit `!important` wie `.conn-table`), per CDP geprüft.
- Learnings: eine Zeile in `.claude/rules/ui.md` (Ziehen mit Timer endet auch, wenn sein Element aus dem DOM fällt – kommt bei Auto-Scroll wieder); K1, K3–K6 waren Einzelfälle. In `docs/e2e-testing.md`: Ziehen per CDP braucht nach dem Drücken eine Pause, bis Blazor das Skript aufgerufen hat.
