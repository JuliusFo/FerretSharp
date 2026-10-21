// The arithmetic of the grid's row selection by keyboard (3.44): which key moves how, which rows a range covers, which rows
// of a block that arrives belong to it. Pure – no DOM, no AG Grid –, so it has tests (tests/js, ADR 0031; review 3.44.0,
// C5). grid.js applies the results to the grid.

const navigationKeys = new Set(['ArrowUp', 'ArrowDown', 'ArrowLeft', 'ArrowRight', 'PageUp', 'PageDown', 'Home', 'End', 'Tab']);
const rangeKeys = new Set(['ArrowUp', 'ArrowDown', 'PageUp', 'PageDown', 'Home', 'End']);

/**
 * How a key moves the selection with the focus: null if it is no navigation key (or with Alt), 'single' without Shift (or
 * a key that moves sideways), 'end' for Shift+End and Ctrl+Shift+↓ (to the last row, also rows not loaded yet), else
 * 'range'.
 */
export function keyMoveOf(e) {
  if (!navigationKeys.has(e.key) || e.altKey) return null;
  if (!e.shiftKey || !rangeKeys.has(e.key)) return 'single';
  return e.key === 'End' || (e.ctrlKey && e.key === 'ArrowDown') ? 'end' : 'range';
}

/**
 * Where a range starts after the focus moved to rowIndex: a single move makes the row the anchor; a range keeps the anchor
 * while its row is still selected, else starts where the focus was (reloaded, sorted, Ctrl+click took the selection).
 */
export function anchorFor(how, anchor, anchorSelected, focusedRow, rowIndex) {
  if (how === 'single') return rowIndex;
  return anchor === null || !anchorSelected ? (focusedRow ?? rowIndex) : anchor;
}

/** The rows a range covers: from the smaller index, to the larger one – or to the end (Infinity) for 'end'. */
export function spanOf(from, to, toEnd) {
  return { from: Math.min(from, to), to: toEnd ? Infinity : Math.max(from, to) };
}

/** The indexes of a block [from, to) that lie in the span, as [start, end); null if none (or no span). */
export function arrivedIn(span, from, to) {
  if (!span) return null;
  const start = Math.max(from, span.from);
  const end = Math.min(to, span.to + 1);
  return start < end ? [start, end] : null;
}
