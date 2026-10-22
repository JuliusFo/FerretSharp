// The arithmetic of the grid's keyboard selection (review 3.44.0, C5; ADR 0031) and of its marked cells (WP-58). Run by `dotnet test` (UI.Tests, JsTests)
// through `node --test`; on its own: node --test tests/js
import { test } from 'node:test';
import assert from 'node:assert/strict';
import {
  anchorFor, arrivedIn, inDisplayOrder, keyMoveOf, markOf, markedColumns, movedEnd, rectOf, spanOf,
} from '../../src/FerretSharp.UI/wwwroot/js/gridSelection.js';

const key = (k, mods = {}) => ({ key: k, shiftKey: false, ctrlKey: false, altKey: false, ...mods });

test('arrows and page keys move a single row, Shift extends, Shift+End and Ctrl+Shift+Down go to the end', () => {
  assert.equal(keyMoveOf(key('ArrowDown')), 'single');
  assert.equal(keyMoveOf(key('PageDown')), 'single');
  assert.equal(keyMoveOf(key('ArrowDown', { shiftKey: true })), 'range');
  assert.equal(keyMoveOf(key('PageUp', { shiftKey: true })), 'range');
  assert.equal(keyMoveOf(key('Home', { shiftKey: true })), 'range');
  assert.equal(keyMoveOf(key('End', { shiftKey: true })), 'end');
  assert.equal(keyMoveOf(key('ArrowDown', { shiftKey: true, ctrlKey: true })), 'end');
  assert.equal(keyMoveOf(key('ArrowUp', { shiftKey: true, ctrlKey: true })), 'range'); // to the first row: loaded
});

test('sideways keys, other keys and Alt do not select', () => {
  assert.equal(keyMoveOf(key('ArrowRight', { shiftKey: true })), 'single'); // another cell of the row
  assert.equal(keyMoveOf(key('Tab')), 'single');
  assert.equal(keyMoveOf(key('a')), null);
  assert.equal(keyMoveOf(key('Enter')), null);
  assert.equal(keyMoveOf(key('ArrowDown', { altKey: true })), null); // the form's key
});

test('a single move makes the row the anchor, a range keeps it while it is selected', () => {
  assert.equal(anchorFor('single', 3, true, 3, 7), 7);
  assert.equal(anchorFor('range', 3, true, 6, 7), 3);
  assert.equal(anchorFor('range', 3, false, 6, 7), 6); // reloaded or sorted: from where the focus was
  assert.equal(anchorFor('range', null, false, null, 7), 7);
});

test('a span covers both ends in either direction, or runs to the end', () => {
  assert.deepEqual(spanOf(7, 3, false), { from: 3, to: 7 });
  assert.deepEqual(spanOf(3, 7, false), { from: 3, to: 7 });
  assert.deepEqual(spanOf(3, 7, true), { from: 3, to: Infinity });
});

test('rows of an arriving block are taken in only within the span', () => {
  assert.deepEqual(arrivedIn({ from: 450, to: Infinity }, 500, 1000), [500, 1000]);
  assert.deepEqual(arrivedIn({ from: 10, to: 520 }, 500, 1000), [500, 521]);
  assert.deepEqual(arrivedIn({ from: 600, to: 700 }, 500, 1000), [600, 701]);
  assert.equal(arrivedIn({ from: 10, to: 20 }, 500, 1000), null);
  assert.equal(arrivedIn(null, 0, 500), null);
});

// ---------- marked cells (WP-58) ----------

const order = ['c0', 'c3', 'c1', 'c2']; // c3 pinned or moved in front of c1

test('a rectangle runs between its corners in either direction, its columns in display order', () => {
  assert.deepEqual(rectOf({ row: 7, col: 'c1' }, { row: 3, col: 'c0' }, order), { top: 3, bottom: 7, left: 0, right: 2, cols: ['c0', 'c3', 'c1'] });
  assert.deepEqual(rectOf({ row: 2, col: 'c2' }, { row: 2, col: 'c2' }, order), { top: 2, bottom: 2, left: 3, right: 3, cols: ['c2'] });
  assert.equal(rectOf({ row: 2, col: 'c9' }, { row: 2, col: 'c2' }, order), null); // a column no longer displayed
});

test('a cell of a rectangle knows which of its sides are the outline', () => {
  const rect = rectOf({ row: 3, col: 'c0' }, { row: 5, col: 'c1' }, order);
  assert.deepEqual(markOf(rect, null, order, 3, 'c0'), { top: true, bottom: false, left: true, right: false });
  assert.deepEqual(markOf(rect, null, order, 4, 'c3'), { top: false, bottom: false, left: false, right: false });
  assert.deepEqual(markOf(rect, null, order, 5, 'c1'), { top: false, bottom: true, left: false, right: true });
  assert.equal(markOf(rect, null, order, 6, 'c0'), null);
  assert.equal(markOf(rect, null, order, 4, 'c2'), null);
});

test('marked columns have an outline only where an unmarked column or the edge follows', () => {
  const marked = new Set(['c3', 'c1', 'c2']);
  assert.deepEqual(markOf(null, marked, order, 0, 'c3'), { top: false, bottom: false, left: true, right: false });
  assert.deepEqual(markOf(null, marked, order, 9999, 'c2'), { top: false, bottom: false, left: false, right: true });
  assert.equal(markOf(null, marked, order, 0, 'c0'), null);
  assert.equal(markOf(null, new Set(), order, 0, 'c0'), null);
});

test('Shift and a key move the end of a rectangle within the grid', () => {
  const end = { row: 10, col: 'c3' };
  assert.deepEqual(movedEnd(end, 'ArrowRight', order, 100, 20), { row: 10, col: 'c1' });
  assert.deepEqual(movedEnd(end, 'ArrowLeft', order, 100, 20), { row: 10, col: 'c0' });
  assert.deepEqual(movedEnd({ row: 10, col: 'c0' }, 'ArrowLeft', order, 100, 20), { row: 10, col: 'c0' });
  assert.deepEqual(movedEnd(end, 'ArrowDown', order, 100, 20), { row: 11, col: 'c3' });
  assert.deepEqual(movedEnd(end, 'PageUp', order, 100, 20), { row: 0, col: 'c3' });
  assert.deepEqual(movedEnd(end, 'PageDown', order, 25, 20), { row: 24, col: 'c3' });
  assert.equal(movedEnd(end, 'Home', order, 100, 20), null);
});

test('Ctrl+click marks or unmarks a column, Ctrl+Shift+click marks up to it from the anchor', () => {
  let state = markedColumns(new Set(), null, 'c3', false, order);
  assert.deepEqual([...state.marked], ['c3']);
  assert.equal(state.anchor, 'c3');
  state = markedColumns(state.marked, state.anchor, 'c2', true, order);
  assert.deepEqual(inDisplayOrder(state.marked, order), ['c3', 'c1', 'c2']);
  assert.equal(state.anchor, 'c3');
  state = markedColumns(state.marked, state.anchor, 'c1', false, order);
  assert.deepEqual(inDisplayOrder(state.marked, order), ['c3', 'c2']);
  assert.equal(state.anchor, 'c1');
  assert.deepEqual([...markedColumns(new Set(), null, 'c2', true, order).marked], ['c2']); // no anchor yet: just the column
});
