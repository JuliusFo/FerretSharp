// Typing in the grid and the form: AltGr characters, the member a typed text picks (ADR 0031). Run by `dotnet test` (UI.Tests,
// JsTests) through `node --test`; on its own: node --test tests/js
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { TYPE_PAUSE, TypedText, isAltGrChar, isDigits, optionsOf, pickLabel, pickTyped, typedCharOf } from '../../src/FerretSharp.UI/wwwroot/js/cellTyping.js';

const key = (k, mods = {}) => ({ key: k, shiftKey: false, ctrlKey: false, altKey: false, metaKey: false, ...mods });
const altGr = k => key(k, { ctrlKey: true, altKey: true });

test('printable keys type their character, other keys and shortcuts do not', () => {
  assert.equal(typedCharOf(key('x')), 'x');
  assert.equal(typedCharOf(key('X', { shiftKey: true })), 'X');
  assert.equal(typedCharOf(key('0')), '0');
  assert.equal(typedCharOf(key('ä')), 'ä');
  assert.equal(typedCharOf(key('Enter')), null);
  assert.equal(typedCharOf(key('Dead')), null);
  assert.equal(typedCharOf(key('c', { ctrlKey: true })), null);
  assert.equal(typedCharOf(key('x', { altKey: true })), null);
  assert.equal(typedCharOf(key('v', { metaKey: true })), null);
});

test('AltGr characters type, Ctrl+Alt with a letter or digit stays a shortcut', () => {
  for (const c of ['@', '€', '\\', '{', '}', '[', ']', '~', '|', 'µ', '²']) {
    assert.equal(typedCharOf(altGr(c)), c);
    assert.equal(isAltGrChar(altGr(c)), true);
  }
  assert.equal(typedCharOf(altGr('q')), null);
  assert.equal(typedCharOf(altGr('7')), null);
  assert.equal(isAltGrChar(key('@')), false); // typed without AltGr: AG Grid starts editing itself
  assert.equal(isAltGrChar(key('@', { ctrlKey: true })), false);
});

const states = [
  { value: '', label: 'NULL' },
  { value: '0', label: 'Neu (0)' },
  { value: '1', label: 'Aktiv (1)' },
  { value: '2', label: 'Gesperrt (2)' },
  { value: '10', label: 'Nachlass (10)' },
];

test('the value comes first: a number picks its member, more digits the longer value', () => {
  assert.equal(pickTyped(states, '0', '2'), '0');
  assert.equal(pickTyped(states, '1', '2'), '1');
  assert.equal(pickTyped(states, '10', '2'), '10');
  assert.equal(pickTyped(states, '3', '2'), null);
  assert.equal(pickTyped(states, '', '2'), null);
});

test('a value starts a longer one when no member has the text itself', () => {
  const options = [{ value: '12', label: 'Zwölf (12)' }, { value: '20', label: 'Zwanzig (20)' }];
  assert.equal(pickTyped(options, '1', null), '12');
});

test('a code picks its member before a label with that letter, case does not matter', () => {
  const flags = [{ value: 'N', label: 'Nein' }, { value: 'J', label: 'Ja' }, { value: 'A', label: 'Nur Admins' }];
  assert.equal(pickTyped(flags, 'J', 'N'), 'J');
  assert.equal(pickTyped(flags, 'a', 'N'), 'A');
  const cased = [{ value: 'a', label: 'klein' }, { value: 'A', label: 'groß' }];
  assert.equal(pickTyped(cased, 'A', null), 'A');
});

test('text picks the first label that starts with it, NULL comes last', () => {
  assert.equal(pickTyped(states, 'ak', '2'), '1');
  assert.equal(pickTyped(states, 'Ge', '0'), '2');
  assert.equal(pickTyped(states, 'n', null), '0');
  assert.equal(pickTyped(states, 'nu', '2'), '');
  assert.equal(pickTyped(states, 'na', '2'), '10');
  assert.equal(pickTyped(states, 'x', '2'), null);
  assert.equal(pickTyped(states, 'sx', '2'), null);
});

test('a single letter or the same letter again goes on to the next label with it', () => {
  assert.equal(pickTyped(states, 'n', '0'), '10');
  assert.equal(pickTyped(states, 'nn', '10'), '');
  assert.equal(pickTyped(states, 'nnn', ''), '0');
  assert.equal(pickTyped(states, 'n', '1'), '0'); // the current member has another letter: the first
});

test('typed text grows while typing goes on and starts anew after a pause', () => {
  const typed = new TypedText();
  assert.equal(typed.active(0), false);
  assert.equal(typed.add('1', 1000), '1');
  assert.equal(typed.add('0', 1000 + TYPE_PAUSE), '10');
  assert.equal(typed.active(1000 + 2 * TYPE_PAUSE), true);
  assert.equal(typed.add('n', 1001 + 2 * TYPE_PAUSE), 'n');
  typed.reset();
  assert.equal(typed.active(1001 + 2 * TYPE_PAUSE), false);
  assert.equal(typed.add('a', 1001 + 2 * TYPE_PAUSE), 'a');
});

test('labels only: a number in a label is no value, NULL comes last', () => {
  const flags = [{ value: '', label: 'NULL' }, { value: '0', label: 'Lesen (1)' }, { value: '1', label: 'Schreiben (2)' }, { value: '2', label: 'Nachweis (4)' }];
  assert.equal(pickLabel(flags, '1', null), null); // pickTyped would take the value '1'
  assert.equal(pickLabel(flags, 'sch', null), '1');
  assert.equal(pickLabel(flags, 'n', null), '2');
  assert.equal(pickLabel(flags, 'n', '2'), '');
  assert.equal(pickLabel(flags, '', null), null);
});

test('numbers match by value, as .NET parses them', () => {
  assert.equal(pickTyped(states, '01', '2'), '1');
  assert.equal(pickTyped(states, '1,0', '2'), '1');
  assert.equal(pickTyped(states, '1.0', '2'), '1');
  assert.equal(pickTyped(states, '010', '2'), '10');
  assert.equal(pickTyped([{ value: '-1', label: 'Unbekannt (-1)' }, { value: '1', label: 'Eins (1)' }], '-1', null), '-1');
  assert.equal(pickTyped([{ value: '2.5', label: 'Halb (2.5)' }], '2,5', null), '2.5');
  assert.equal(pickTyped(states, '1,', '2'), null); // not a number yet: nothing changes
});

test('only digits are a typed flags value', () => {
  assert.equal(isDigits('5'), true);
  assert.equal(isDigits('012'), true);
  assert.equal(isDigits('99999999999999999999'), true); // too large for a long: .NET says so
  assert.equal(isDigits('5a'), false);
  assert.equal(isDigits('-1'), false);
  assert.equal(isDigits(''), false);
});

test('list entries are taken with their value and the text shown', () => {
  const list = [{ value: '', textContent: 'NULL' }, { value: '0', textContent: 'Neu (0)' }];
  assert.deepEqual(optionsOf(list), [{ value: '', label: 'NULL' }, { value: '0', label: 'Neu (0)' }]);
});
