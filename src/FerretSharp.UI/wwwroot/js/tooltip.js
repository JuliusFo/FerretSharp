// Tooltips for every title in the app: one look that follows the app theme. The native tooltip of the WebView always
// opens below the pointer – near the bottom of a maximized window behind the Windows taskbar – and follows the Windows
// theme instead of the app's. These open below the pointer where there is room, else above it, always inside the
// window. The title is taken away while the pointer rests on the element (that keeps the native tooltip away) and given
// back when it leaves. Monaco keeps its own hovers and the titles of its widgets; AG Grid draws its cell tooltips itself
// (styled alike in ferretsharp.css).

const EXCLUDED = '.monaco-editor, .monaco-aria-container';
const DELAY = 500;
const MARGIN = 8;
// Below the pointer: clear of the cursor itself.
const BELOW = 20;
const ABOVE = 8;

let tip = null;
let timer = 0;
let source = null;
let pointer = { x: 0, y: 0 };

// Blazor sets the title again when its text changes while the pointer rests on the element (the status bar updates
// itself): take the new text over instead of showing the old one, and keep the native tooltip away.
const watcher = new MutationObserver(() => {
  if (!source?.hasAttribute('title')) return;
  source.dataset.tip = source.getAttribute('title');
  source.removeAttribute('title');
  if (tip) {
    tip.textContent = source.dataset.tip;
    place();
  }
});

function hide() {
  clearTimeout(timer);
  watcher.disconnect();
  tip?.remove();
  tip = null;
  if (source?.isConnected && source.dataset.tip !== undefined) {
    // Give the title back unless Blazor has set a new one meanwhile.
    if (!source.hasAttribute('title')) source.setAttribute('title', source.dataset.tip);
    delete source.dataset.tip;
  }
  source = null;
}

function show() {
  if (!source?.isConnected || !source.dataset.tip) return;
  tip = document.createElement('div');
  tip.className = 'app-tip';
  tip.setAttribute('role', 'tooltip');
  tip.textContent = source.dataset.tip;
  document.body.appendChild(tip);
  place();
}

function place() {
  const size = tip.getBoundingClientRect();
  const left = Math.min(pointer.x, window.innerWidth - size.width - MARGIN);
  const below = pointer.y + BELOW;
  const top = below + size.height <= window.innerHeight - MARGIN ? below : pointer.y - ABOVE - size.height;
  tip.style.left = `${Math.max(MARGIN, left)}px`;
  tip.style.top = `${Math.max(MARGIN, top)}px`;
}

function onOver(e) {
  // Blazor removed the element under the pointer: no mouseout comes for it.
  if (source && !source.isConnected) hide();
  // The source itself has no title meanwhile (data-tip): moving onto a child of it must not jump to an outer title.
  const element = e.target instanceof Element ? e.target.closest('[title], [data-tip]') : null;
  if (!element || element === source || !element.getAttribute('title') || element.closest(EXCLUDED)) return;
  hide();
  source = element;
  pointer = { x: e.clientX, y: e.clientY };
  // Taking the title away suppresses the native tooltip.
  element.dataset.tip = element.getAttribute('title');
  element.removeAttribute('title');
  watcher.observe(element, { attributes: true, attributeFilter: ['title'] });
  timer = setTimeout(show, DELAY);
}

// Until it shows, the tooltip goes where the pointer rests.
function onMove(e) {
  if (source && !tip) pointer = { x: e.clientX, y: e.clientY };
}

function onOut(e) {
  if (source && !(e.relatedTarget instanceof Node && source.contains(e.relatedTarget))) hide();
}

const events = [
  ['mouseover', onOver],
  ['mousemove', onMove],
  ['mouseout', onOut],
  ['mousedown', hide],
  ['keydown', hide],
  ['wheel', hide],
  ['scroll', hide],
];

export function install() {
  for (const [type, handler] of events) document.addEventListener(type, handler, { capture: true, passive: true });
  window.addEventListener('blur', hide);
}

export function uninstall() {
  for (const [type, handler] of events) document.removeEventListener(type, handler, { capture: true });
  window.removeEventListener('blur', hide);
  hide();
}
