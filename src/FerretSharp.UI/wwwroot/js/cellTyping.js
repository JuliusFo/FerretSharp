// Typing in the grid: which key types a character, and which member of a list typed text picks. Pure – no DOM, no
// AG Grid –, so it has tests (tests/js, ADR 0031). grid.js applies the results.

/** A pause this long (ms) ends what was typed into a member list; the next key starts anew (as Windows' own lists). */
export const TYPE_PAUSE = 1000;

/** What was typed into a list so far; a pause of TYPE_PAUSE starts anew. now: milliseconds (Date.now()). */
export class TypedText {
  text = '';
  at = -Infinity;
  /** Adds a character and returns the text typed so far. */
  add(char, now) {
    this.text = (this.active(now) ? this.text : '') + char;
    this.at = now;
    return this.text;
  }
  /** True while typing goes on: a space then belongs to the text. */
  active(now) {
    return this.text !== '' && now - this.at <= TYPE_PAUSE;
  }
  reset() {
    this.text = '';
  }
}

/**
 * The character a key types, or null: a printable key without Ctrl, Alt or Meta, or a character typed with AltGr.
 * Windows reports AltGr as Ctrl+Alt, so AG Grid takes `@ € \ { }` (German keyboard) for a shortcut and does not start
 * editing. A real Ctrl+Alt shortcut types a letter or digit and stays a shortcut.
 */
export function typedCharOf(e) {
  if (!e.key || e.key.length !== 1 || e.metaKey) return null;
  if (!e.ctrlKey && !e.altKey) return e.key;
  return e.ctrlKey && e.altKey && !/^[a-z0-9]$/i.test(e.key) ? e.key : null;
}

/** True for a character typed with AltGr (see typedCharOf): AG Grid would not start editing with it. */
export function isAltGrChar(e) {
  return typedCharOf(e) !== null && e.ctrlKey && e.altKey;
}

/**
 * The value of the member typed text picks; null if none fits. options: [{ value, label }] with the database value
 * (as the cell editor writes it, '' for NULL) and the label shown (`Neu (0)`); current: the value chosen now.
 * In this order: the member with this value (`0`, `J`), the first whose value starts with the text (`1` before `10`),
 * the first whose label starts with it (`Ak`), ignoring case. NULL counts last among the labels: `n` means `Neu`,
 * `nu` NULL. A single letter, or the same letter again (`nnn`), goes to the next label with that letter after current,
 * as in Windows' lists; current null starts at the first.
 */
export function pickTyped(options, text, current) {
  if (!text) return null;
  const lower = text.toLowerCase();
  const byValue = options.find(o => o.value !== '' && o.value === text)
    ?? options.find(o => o.value !== '' && o.value.toLowerCase() === lower)
    ?? options.find(o => o.value !== '' && o.value.toLowerCase().startsWith(lower));
  return byValue ? byValue.value : pickLabel(options, text, current);
}

/** The value of the entry whose label typed text means, as pickTyped without looking at the values; null if none. */
export function pickLabel(options, text, current) {
  if (!text) return null;
  const lower = text.toLowerCase();
  const byLabel = [...options.filter(o => o.value !== ''), ...options.filter(o => o.value === '')];
  const startsWith = prefix => byLabel.filter(o => o.label.toLowerCase().startsWith(prefix));
  if (lower.length > 1) {
    const label = startsWith(lower)[0];
    if (label) return label.value;
  }

  if ([...lower].some(c => c !== lower[0])) return null;
  const same = startsWith(lower[0]);
  if (same.length === 0) return null;
  const at = same.findIndex(o => o.value === current);
  return same[(at + 1) % same.length].value;
}

/** Digits typed into the flags editor as the value they mean (the sum of the flags), or null for other text. */
export function typedSum(text) {
  return /^\d+$/.test(text) ? BigInt(text) : null;
}

/**
 * The boxes of the flags editor a sum ticks: those whose bits are all set in it (bits: BigInt per box, 0n for NULL,
 * which is never ticked). rest: the bits of the sum no box stands for (kept in the value, shown as a number).
 */
export function flagsOfSum(bits, sum) {
  const ticked = bits.map(b => b !== 0n && (sum & b) === b);
  const covered = bits.reduce((all, b, i) => ticked[i] ? all | b : all, 0n);
  return { ticked, rest: sum & ~covered };
}
