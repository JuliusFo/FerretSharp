// Tooltips for the footers (status bar, tab and result footers). The native title tooltip always opens below the
// pointer, so in a maximized window it ends up behind the Windows taskbar; these open above the element instead.

const SELECTOR = '.statusbar [title], .tab-footer [title]';
const DELAY = 500;

let tip = null;
let timer = 0;
let source = null;

// Blazor sets the title again when its text changes while the pointer rests on the element (the status bar updates
// itself): take the new text over instead of showing the old one, and keep the native tooltip away.
const watcher = new MutationObserver(() => {
  if (!source?.hasAttribute('title')) return;
  source.dataset.tip = source.getAttribute('title');
  source.removeAttribute('title');
  if (tip) {
    tip.textContent = source.dataset.tip;
    place(source);
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

function show(element) {
  const text = element.dataset.tip;
  if (!text) return;
  tip = document.createElement('div');
  tip.className = 'footer-tip';
  tip.textContent = text;
  document.body.appendChild(tip);
  place(element);
}

function place(element) {
  const margin = 8;
  const target = element.getBoundingClientRect();
  const size = tip.getBoundingClientRect();
  const left = Math.min(Math.max(margin, target.left), window.innerWidth - size.width - margin);
  tip.style.left = left + 'px';
  tip.style.top = Math.max(margin, target.top - size.height - 6) + 'px';
}

function onOver(e) {
  // Blazor removed the element under the pointer: no mouseout comes for it.
  if (source && !source.isConnected) hide();
  const element = e.target instanceof Element ? e.target.closest(SELECTOR) : null;
  if (!element || element === source) return;
  hide();
  source = element;
  // Taking the title away suppresses the native tooltip.
  element.dataset.tip = element.getAttribute('title');
  element.removeAttribute('title');
  watcher.observe(element, { attributes: true, attributeFilter: ['title'] });
  timer = setTimeout(() => show(element), DELAY);
}

function onOut(e) {
  if (source && !(e.relatedTarget instanceof Node && source.contains(e.relatedTarget))) hide();
}

export function install() {
  document.addEventListener('mouseover', onOver, true);
  document.addEventListener('mouseout', onOut, true);
  document.addEventListener('mousedown', hide, true);
}

export function uninstall() {
  document.removeEventListener('mouseover', onOver, true);
  document.removeEventListener('mouseout', onOut, true);
  document.removeEventListener('mousedown', hide, true);
  hide();
}
