// The arithmetic of the grid's row selection by keyboard (3.44): which key moves how, which rows a range covers, which rows
// of a block that arrives belong to it. And of the marked cells (WP-58): a rectangle of cells, or whole columns, and which
// cells a value typed with Ctrl+Enter goes to (WP-59). Pure – no
// DOM, no AG Grid –, so it has tests (tests/js, ADR 0031; review 3.44.0, C5). grid.js applies the results to the grid.

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

// ---------- marked cells (WP-58) ----------
// A cell is { row, col }: the row's index and the column's id. Columns count in display order (`order`: the ids of the
// displayed columns, pinned ones first).

/**
 * The rectangle between two cells: { top, bottom, left, right } as row indexes and positions in `order`, and the ids of its
 * columns in display order. Null if a column is no longer displayed.
 */
export function rectOf(anchor, end, order) {
  const a = order.indexOf(anchor.col);
  const b = order.indexOf(end.col);
  if (a < 0 || b < 0) return null;
  const left = Math.min(a, b);
  const right = Math.max(a, b);
  return {
    top: Math.min(anchor.row, end.row),
    bottom: Math.max(anchor.row, end.row),
    left,
    right,
    cols: order.slice(left, right + 1),
  };
}

/**
 * How a cell is marked: null if not, else which of its sides lie on the outline. `rect` is a rectangle of cells (rectOf),
 * `marked` a set of whole columns (no top or bottom side; a side at a marked neighbour is no outline).
 */
export function markOf(rect, marked, order, row, col) {
  const position = order.indexOf(col);
  if (rect) {
    if (row < rect.top || row > rect.bottom || position < rect.left || position > rect.right) return null;
    return { top: row === rect.top, bottom: row === rect.bottom, left: position === rect.left, right: position === rect.right };
  }
  if (!marked?.has(col)) return null;
  return { top: false, bottom: false, left: !marked.has(order[position - 1]), right: !marked.has(order[position + 1]) };
}

/**
 * Where the end of a rectangle moves with Shift and a key: ← → by a column, ↑ ↓ by a row, Page↑ Page↓ by `pageRows`,
 * within the grid's rows (`rowCount`) and displayed columns. Null for any other key.
 */
export function movedEnd(end, key, order, rowCount, pageRows) {
  const position = order.indexOf(end.col);
  const rows = { ArrowUp: -1, ArrowDown: 1, PageUp: -pageRows, PageDown: pageRows }[key];
  const columns = { ArrowLeft: -1, ArrowRight: 1 }[key];
  if (rows !== undefined) return { row: Math.max(0, Math.min(rowCount - 1, end.row + rows)), col: end.col };
  if (columns !== undefined && position >= 0) return { row: end.row, col: order[Math.max(0, Math.min(order.length - 1, position + columns))] };
  return null;
}

/**
 * Whole columns after a Ctrl+click on a header: the column comes in or, if it was marked, goes; with Shift (`extend`) the
 * columns from the anchor to it in display order come in. Returns { marked, anchor } – the anchor is where the next Shift
 * range starts.
 */
export function markedColumns(marked, anchor, col, extend, order) {
  const next = new Set(marked);
  const from = order.indexOf(anchor);
  const to = order.indexOf(col);
  if (extend && from >= 0 && to >= 0) {
    for (const id of order.slice(Math.min(from, to), Math.max(from, to) + 1)) next.add(id);
    return { marked: next, anchor };
  }
  if (next.has(col)) next.delete(col);
  else next.add(col);
  return { marked: next, anchor: col };
}

/** The ids of marked columns in display order (what is copied, and how). */
export function inDisplayOrder(marked, order) {
  return order.filter(id => marked.has(id));
}

// ---------- many cells at once (WP-59) ----------

/**
 * Where a value typed into a cell goes with Ctrl+Enter: 'marks' – the marked cells or columns, if the edited cell is one
 * of them –, 'rows' – the edited column of the selected rows, if several are and the edited row is one of them –, else
 * null: only into the edited cell, like Enter. `selected` are the indexes of the selected rows.
 */
export function fillTargetOf(rect, marked, order, selected, row, col) {
  if (rect || marked.size > 0) return markOf(rect, marked, order, row, col) ? 'marks' : null;
  return selected.length > 1 && selected.includes(row) ? 'rows' : null;
}
