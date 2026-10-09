// Keyboard shortcuts (WP-25: changeable in the settings). Global combos (e.g. "ctrl+shift+o") are caught before the grid
// and the editors and forwarded to .NET; local ones (the grid's form key) are only looked up here by grid.js.
// The text form is KeyChord's in FerretSharp.Core – keep keyOf in step with KeyChord.NormalizeKey.
let handler = null;
let recording = null;
const local = new Map();

const modifierKeys = new Set(['control', 'shift', 'alt', 'meta', 'altgraph', 'os']);

// Every dialog renders a .modal-backdrop; those of tabs in the background are mounted but not displayed.
const modalOpen = () => [...document.querySelectorAll('.modal-backdrop')].some(el => el.getClientRects().length > 0);

// A grid cell being edited in the visible tab (the grids of tabs in the background are mounted but not displayed).
const cellEditing = () => [...document.querySelectorAll('.ag-cell-inline-editing')].some(el => el.getClientRects().length > 0);

function keyOf(e) {
  if (e.key === ' ') return 'space';
  if (e.key === '+') return 'plus';
  return e.key.toLowerCase();
}

// "ctrl+shift+alt+key" of a key event; null for a modifier pressed alone.
export function comboOf(e) {
  if (!e.key || modifierKeys.has(e.key.toLowerCase())) return null;
  const parts = [];
  if (e.ctrlKey) parts.push('ctrl');
  if (e.shiftKey) parts.push('shift');
  if (e.altKey) parts.push('alt');
  parts.push(keyOf(e));
  return parts.join('+');
}

export function register(dotnet, combos) {
  unregister();
  const wanted = new Set(combos);

  handler = e => {
    if (recording) return;
    const combo = comboOf(e);
    if (combo && wanted.has(combo)) {
      // Taken also while a dialog is open (.NET then ignores it): F5 would reload the whole WebView otherwise.
      e.preventDefault();
      e.stopPropagation();
      dotnet.invokeMethodAsync('OnShortcut', combo, modalOpen(), cellEditing()).catch(err => console.error('Shortcut failed', err));
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

// Combos handled where they happen (name → combo or null), e.g. { form: 'alt+enter' } for grid.js.
export function setLocal(combos) {
  local.clear();
  for (const [name, combo] of Object.entries(combos)) local.set(name, combo);
}

export function localCombo(name) {
  return local.get(name) ?? null;
}

// The next key combination pressed (settings page): resolves with its text form, or null on Esc or cancelRecord.
// Global shortcuts rest meanwhile, and the key does nothing else.
export function record() {
  cancelRecord();
  return new Promise(resolve => {
    const listener = e => {
      e.preventDefault();
      e.stopPropagation();
      if (e.key === 'Escape' && !e.ctrlKey && !e.shiftKey && !e.altKey) {
        finish(null);
        return;
      }

      const combo = comboOf(e);
      if (combo) finish(combo);
    };
    const finish = combo => {
      window.removeEventListener('keydown', listener, true);
      recording = null;
      resolve(combo);
    };
    recording = { finish };
    window.addEventListener('keydown', listener, true);
  });
}

export function cancelRecord() {
  recording?.finish(null);
}
