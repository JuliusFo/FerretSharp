# Klein: Tab-Leiste scrollt mit Mausrad und schmaler Scrollbar → Release 3.44.0
Anlass (2026-10-21): Bugreport eines Kollegen – bei vielen offenen Tabs liefen sie über den rechten Rand, ohne dass man hinkam. Die Leiste konnte seit dem ersten Tab-Commit (`d54f948`) scrollen (`overflow-x: auto`), die Scrollbar war aber auf Höhe 0 versteckt (ohne festgehaltenen Grund, vermutlich Optik), und das normale Mausrad scrollt nur senkrecht – erreichbar war der Rest nur mit Shift+Rad. Branch `fix/tab-bar-scroll`.

Entscheidungen des Nutzers:
- Zusätzlich zum Mausrad eine schmale Scrollbar: Platz immer reserviert (kein Springen der Tabs, sobald die Leiste überläuft), Daumen nur sichtbar, solange die Maus über der Leiste ist.

Gebaut (`dom.js scrollableTabs`, aufgerufen von `TabBar` neben `watchOverflow`; `ferretsharp.css` `.tabs`):
- Mausrad über der Leiste scrollt seitwärts, solange sie überläuft (mit Ctrl nicht: Zoom bleibt). Die Seite scrollt dabei nicht mit.
- Wird ein anderer Tab aktiv (geöffnet, FK-Sprung, Tastenkürzel, geschlossener Nachbar), scrollt die Leiste ihn ins Bild – links rechts neben die angeheftete Gruppe (sticky, WP-34), die ihn sonst verdecken würde; angeheftete Tabs sind immer sichtbar. Nur der Wechsel des aktiven Tabs scrollt: Hat der Nutzer die Leiste weggescrollt, bleibt sie beim Neurendern (Filter, Umbenennen) stehen.
- Scrollbar 4px unter den Tabs, `overflow-x: scroll` statt `auto` (Platz immer da), Leiste 42px statt 38px, damit die Tabs ihre Höhe behalten. Daumen `--border-strong`, unter der Maus `--faint`.

Fallen:
- Chromium malt `::-webkit-scrollbar-thumb` nicht neu, wenn sich nur `:hover` am Scroll-Container ändert: `.tabs:hover::-webkit-scrollbar-thumb` blieb unsichtbar. Ein Klassenwechsel per JS (`mouseenter`/`mouseleave` → Klasse `hover`) malt neu. Das Standard-`scrollbar-color` schaltet zwar auch per `:hover` um, aber `scrollbar-width: thin` ist unter Windows 10px hoch, und mit gesetztem `scrollbar-color`/`scrollbar-width` gelten die `::-webkit-scrollbar`-Regeln nicht mehr.
- Die globale Daumenregel hat `border: 3px solid transparent` mit `background-clip: content-box` – bei 4px Höhe bliebe nichts übrig, deshalb `border: 0` an `.tabs`.

E2E: echte `ferretsharp.css` und `dom.js` mit dem Markup der Leiste auf einer Testseite im eingebauten Browser (gleiche Chromium-Engine): Mausrad (Eingabe des Browserfensters) scrollt bis ans Ende, Seite bleibt stehen; Daumen erscheint beim Hovern und verschwindet danach (zum Sehen vergrößert und rot gefärbt); aktivierter Tab links und rechts außerhalb wird sichtbar, auch hinter zwei angehefteten; Neurendern nach Scrollen von Hand lässt die Leiste stehen. Nicht geprüft: in der App selbst (Docker lief nicht, keine Test-DB) – also Mausrad durch den WPF-Host (`SendInput`) und Hell/Dunkel.
