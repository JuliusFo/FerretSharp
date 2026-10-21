// The arithmetic of the grid's keyboard selection (review 3.44.0, C5; ADR 0031). Run by `dotnet test` (UI.Tests, JsTests)
// through `node --test`; on its own: node --test tests/js
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { anchorFor, arrivedIn, keyMoveOf, spanOf } from '../../src/FerretSharp.UI/wwwroot/js/gridSelection.js';

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
