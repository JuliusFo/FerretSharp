---
paths:
  - "src/FerretSharp.UI/wwwroot/css/**"
---

# CSS

- Eigenes CSS mit Design-Tokens (`ferretsharp.css`), hell/dunkel über `prefers-color-scheme`; keine CSS-Frameworks. Farben nur als Tokens mit Dunkel-Variante – auch Zustände (`--warn`, `--pending`, `--yellow`, `--key`, `--green`, `--green-text`, `--on-prod`, `--backdrop`, `--sql-*`, R5b). Helle Flächen nicht als `rgba(…)` festschreiben, sondern aus dem Token mischen: `color-mix(in srgb, var(--warn) 12%, transparent)` – dann folgt der Dunkelmodus von selbst. Kontrast im Dunkelmodus berechnen (Text auf der gemischten Fläche), nicht nur ansehen.
- Farbige Button-Varianten (`.btn.primary`, `.btn.danger-solid`) setzen im `:hover` ihren Hintergrund selbst: `.btn:hover:not(:disabled)` setzt `--hover` und gewinnt sonst (Text unlesbar). Text auf farbigen Flächen über Tokens (`--accent-text`, `--danger-text`), die im Dark Mode dunkel sind. Prüfen mit echtem Hover (`Input.dispatchMouseEvent` mouseMoved) und berechnetem Kontrast, nicht nur per Screenshot.
- Rahmen um einen Container nie als `box-shadow: inset` auf dem Container: Der Schatten liegt unter den Kindern, deren Hintergründe verdecken ihn. Der Prod-Rahmen (`.app.is-prod`) ist deshalb ein Overlay `::after` (`position: fixed`, `pointer-events: none`, `z-index: 90`: über Popovern und Dialogen, unter `#blazor-error-ui`).
- Klassennamen in Komponenten nicht mit globalen Klassen kollidieren lassen (`.empty` ist der Leerzustand mit `position: absolute; inset: 0`). `EmptyState` füllt den nächsten positionierten Vorfahren: auf einer Seite mit Kopfzeile in eine eigene positionierte Box unter dem Kopf setzen, sonst schluckt er Klicks auf den Kopf.
- Kein `display: flex` direkt auf `<td>`: Die Zelle verliert ihr Tabellenverhalten – Flex-Layout in ein inneres Element.
- Eine eigene Tab-Ansicht scrollt nur in ihrem Container: `tabview` → `tab-details` → `details-body` → `structure` (wie `TabView`) bzw. ein Flex-Container mit `min-height: 0`. Sonst schiebt `scrollIntoView` oder langer Inhalt das ganze Fenster samt Explorer und Statusleiste weg.
- Scrollbar-Teile (`::-webkit-scrollbar-thumb`) nicht über `:hover` am Scroll-Container umschalten: Chromium malt sie dann nicht neu, der Daumen bleibt wie er war. Stattdessen eine Klasse per JS (`mouseenter`/`mouseleave`, wie `.tabs.hover` aus `dom.js scrollableTabs`). Das Standard-`scrollbar-color` schaltet zwar um, `scrollbar-width: thin` ist unter Windows aber 10px hoch und schaltet die `::-webkit-scrollbar`-Regeln ab.
- Sticky-Kopfzeilen (`position: sticky`): Kein Vorfahre zwischen Kopf und Scroll-Container darf `overflow: hidden`/`auto` haben – er wird selbst Scroll-Container, der Kopf klebt dann an ihm und bewegt sich mit (für runde Ecken `overflow: clip`). Sticky rechnet das Padding des Scroll-Containers ein: In `.structure` steht der Kopf mit `top: 0` unter dem Padding, darüber laufen Zeilen durch → `top: calc(-1 * var(--structure-top, 0px))` (`table.columns th`).
