// Global keyboard shortcuts: forwards registered combos (e.g. "ctrl+shift+o") to .NET.
let handler = null;

// Every dialog renders a .modal-backdrop; those of tabs in the background are mounted but not displayed.
const modalOpen = () => [...document.querySelectorAll('.modal-backdrop')].some(el => el.getClientRects().length > 0);

export function register(dotnet, combos) {
  unregister();
  const wanted = new Set(combos.map(c => c.toLowerCase()));

  handler = e => {
    const parts = [];
    if (e.ctrlKey) parts.push('ctrl');
    if (e.shiftKey) parts.push('shift');
    if (e.altKey) parts.push('alt');
    parts.push(e.key.toLowerCase());
    const combo = parts.join('+');

    if (wanted.has(combo)) {
      // Taken also while a dialog is open (.NET then ignores it): F5 would reload the whole WebView otherwise.
      e.preventDefault();
      e.stopPropagation();
      dotnet.invokeMethodAsync('OnShortcut', combo, modalOpen()).catch(err => console.error('Shortcut failed', err));
    }
  };

  document.addEventListener('keydown', handler, true);
}

export function unregister() {
  if (handler) {
    document.removeEventListener('keydown', handler, true);
    handler = null;
  }
}
