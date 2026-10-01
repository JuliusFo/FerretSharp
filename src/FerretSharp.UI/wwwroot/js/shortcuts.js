// Global keyboard shortcuts: forwards registered combos (e.g. "ctrl+shift+o") to .NET.
let handler = null;

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
      e.preventDefault();
      e.stopPropagation();
      dotnet.invokeMethodAsync('OnShortcut', combo);
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
