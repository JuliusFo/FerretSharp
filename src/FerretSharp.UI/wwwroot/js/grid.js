// Bridge to AG Grid Community (global `agGrid`, loaded by index.html). Infinite row model: every block of rows,
// including sorting, comes from .NET; cells arrive as display strings (null = SQL NULL). No business logic here.

import { comboOf, localCombo, setEditFinisher } from './shortcuts.js';
import {
  anchorFor, arrivedIn, fillTargetOf, inDisplayOrder, keyMoveOf, markOf, markedColumns, movedEnd, rectOf, spanOf,
} from './gridSelection.js';
import {
  ALTGR_FOLLOWS, TypedText, isAltGrChar, isAltGrKey, isDigits, optionsOf, pickLabel, pickTyped, typedCharOf,
} from './cellTyping.js';

const grids = new Map();
// Per grid: focus changes from .NET – forget the last navigation key (it must not select), select the row (focusRow with
// select); refresh ends the rows the keyboard selected (forgetSpan).
const focusControls = new Map();
// Cell values on their way to .NET (onCellEditRequest), each resolving to whether .NET took it.
const pendingEdits = new Set();

// Ends the open cell editors of the visible grids, taking their values, and waits until .NET has them (R4a: Ctrl+S with
// an editor open reloaded the grid, which cancelled the editor – the value was lost). False if one was refused.
async function finishEditing() {
  for (const [id, api] of grids) {
    if (api.getEditingCells().length > 0 && document.getElementById(id)?.getClientRects().length > 0) {
      api.stopEditing(false);
    }
  }

  await new Promise(resolve => setTimeout(resolve)); // the edit request may follow the stop
  return (await Promise.all([...pendingEdits])).every(taken => taken);
}

setEditFinisher(finishEditing);
// Per grid: whether its workspace may write (switched by setEditable when the workspace is unlocked or locked).
const editStates = new Map();
// Per grid: column metadata by column id; updateColumns changes these objects in place (the column defs refer to them).
const metas = new Map();
// Per grid: header height (with the line for C# names), null for the default.
const headerHeights = new Map();
// Keys a member list (enum editor) handles itself while editing.
const listKeys = new Set(['ArrowUp', 'ArrowDown', 'PageUp', 'PageDown', 'Home', 'End']);
const media = window.matchMedia('(prefers-color-scheme: dark)');
let modulesRegistered = false;

// A call into .NET that failed. Errors of the handlers themselves reach the tab's error boundary on the .NET side (FerretComponent);
// what is left here is the transport – mostly a grid whose component is already gone. Logged, not hidden.
const callFailed = e => console.debug('FerretSharp: grid call to .NET failed', e);

// How .NET names a row: the index of a loaded row, or -1 and the id of a new row pinned at the top.
const rowArgs = (node, data) => [node.rowPinned ? -1 : node.rowIndex, data?.__new ?? null];

// The id of the new row behind a cell pinned at the top (a focused cell, a cell event); null for loaded rows.
const newIdOf = (api, cell) => cell?.rowPinned === 'top' ? api.getPinnedTopRow(cell.rowIndex)?.data?.__new ?? null : null;

// The message below a cell editor; null removes it.
function showEditorError(gui, message) {
  let label = gui.querySelector('.fs-editor-error');
  if (!message) {
    label?.remove();
    return;
  }

  if (!label) {
    label = document.createElement('div');
    label.className = 'fs-editor-error';
    gui.appendChild(label);
  }

  label.textContent = message;
}

function registerModules() {
  if (!modulesRegistered && agGrid.ModuleRegistry && agGrid.AllCommunityModule) {
    agGrid.ModuleRegistry.registerModules([agGrid.AllCommunityModule]);
  }
  modulesRegistered = true;
}

/** A design token of ferretsharp.css, as it applies now (light or dark). */
function token(name) {
  return getComputedStyle(document.documentElement).getPropertyValue(name).trim();
}

/** The grid's look; its colors come from the design tokens (R4b: before, a copy of their values stood here). */
function theme(headerHeight) {
  const dark = media.matches;
  return agGrid.themeQuartz
    .withPart(dark ? agGrid.colorSchemeDark : agGrid.colorSchemeLight)
    .withParams({
      fontFamily: '"Segoe UI Variable Text", "Segoe UI", system-ui, sans-serif',
      fontSize: 13,
      headerFontSize: 12,
      headerFontWeight: 600,
      rowHeight: ROW_HEIGHT,
      // A line more for C# names (WP-12): the header height is a theme parameter, as a grid option it would reset the row height.
      headerHeight: headerHeight || 46,
      spacing: 6,
      wrapperBorder: false,
      wrapperBorderRadius: 0,
      columnBorder: true,
      // Clearly visible edge of the pinned area (primary key and pinned columns).
      pinnedColumnBorder: { style: 'solid', width: 2, color: token('--grid-pinned-border') },
      accentColor: token('--accent'),
      backgroundColor: token('--surface'),
      foregroundColor: token('--text'),
      headerBackgroundColor: token('--grid-header'),
      oddRowBackgroundColor: token('--grid-odd-row'),
      borderColor: token('--grid-border'),
    });
}

media.addEventListener('change', () => grids.forEach((api, id) => api.setGridOption('theme', theme(headerHeights.get(id)))));

/**
 * Header: name, Oracle type, NOT NULL, PK/FK badges, sort arrow. With a C# model (WP-12) a line between them shows the
 * other name and the C# type (`KundeId · int`); `meta.clrLine` says whether the table has that line at all. The type line
 * comes as text from .NET (type and NOT NULL as the settings show them), `meta.typeLine` says whether there is one.
 */
class FerretHeader {
  init(params) {
    this.params = params;
    const meta = params.getMeta();
    const texts = params.texts;
    this.eGui = document.createElement('div');
    this.eGui.className = 'fs-header' + (meta.numeric ? ' num' : '');
    this.eGui.innerHTML =
      '<div class="fs-h-top"><span class="fs-h-name"></span><span class="fs-h-badges"></span><span class="fs-h-sort"></span></div>' +
      (meta.clrLine ? '<div class="fs-h-clr"></div>' : '') +
      (meta.typeLine ? '<div class="fs-h-type"></div>' : '');
    this.eGui.querySelector('.fs-h-name').textContent = meta.label;
    if (meta.clrLine) {
      this.eGui.querySelector('.fs-h-clr').textContent = [meta.alternate, meta.clrType].filter(Boolean).join(' · ');
    }
    if (meta.typeLine) this.eGui.querySelector('.fs-h-type').textContent = meta.type;
    const badges = this.eGui.querySelector('.fs-h-badges');
    if (meta.pk) badges.insertAdjacentHTML('beforeend', '<span class="fs-badge pk">PK</span>');
    if (meta.fk) badges.insertAdjacentHTML('beforeend', '<span class="fs-badge fk">FK</span>');
    else if (meta.fkModel) {
      // The tooltip is a text from .NET: set as a property, not as markup.
      const badge = document.createElement('span');
      badge.className = 'fs-badge fk model';
      badge.title = texts.fkModel;
      badge.textContent = 'FK';
      badges.appendChild(badge);
    }
    if (meta.displayColumn) {
      // The table's display column (WP-31); texts from .NET, set as properties.
      const badge = document.createElement('span');
      badge.className = 'fs-badge display';
      badge.title = texts.displayBadgeTitle;
      badge.textContent = texts.displayBadge;
      badges.appendChild(badge);
    }
    this.sortEl = this.eGui.querySelector('.fs-h-sort');
    // Ctrl+click marks the column instead of sorting it, Ctrl+Shift+click the columns up to it (WP-58) – where the grid
    // marks columns at all (params.markColumn).
    const colId = params.column.getColId();
    if (meta.sortable || params.markColumn) {
      this.eGui.addEventListener('click', e => {
        if (params.markColumn && (e.ctrlKey || e.metaKey)) params.markColumn(colId, e.shiftKey);
        else if (meta.sortable) params.progressSort(e.shiftKey);
      });
    }
    this.eGui.classList.toggle('marked', !!params.isMarked?.(colId));
    if (meta.sortable) {
      this.onSortChanged = () => this.updateSort();
      params.column.addEventListener('sortChanged', this.onSortChanged);
      this.updateSort();
    }
    // Column comment (ALL_COL_COMMENTS) as tooltip.
    this.eGui.title = [meta.alternate ? `${meta.label} · ${meta.alternate}` : null, meta.comment,
      meta.sortable ? null : texts.notSortable].filter(Boolean).join('\n');
  }
  updateSort() {
    const sort = this.params.column.getSort();
    const index = this.params.column.getSortIndex();
    const multi = this.params.api.getColumnState().filter(c => c.sort).length > 1;
    this.sortEl.textContent = (sort === 'asc' ? '↑' : sort === 'desc' ? '↓' : '') + (sort && multi ? String(index + 1) : '');
  }
  getGui() { return this.eGui; }
  refresh() { return false; }
  destroy() {
    if (this.onSortChanged) this.params.column.removeEventListener('sortChanged', this.onSortChanged);
  }
}

/**
 * Cell editor (v2): starts with the full value from .NET (the grid shows shortened texts and grouped numbers), or
 * with the typed character, or – after a rejected value – with that value and the message below it. Long text
 * columns get a multi-line box (Shift+Enter: new line, Enter: done).
 */
/**
 * Enter in an editor: the message if .NET refuses the text, else null. A failed call counts as "no objection" – the edit
 * itself (OnCellEdit) checks again and keeps the editor open with its reason (review 3.31.0, A47: unhandled rejections).
 */
async function validateEdit(params, value) {
  try {
    return await params.state.dotnet.invokeMethodAsync('ValidateEdit', ...rowArgs(params.node, params.data), params.column.getColId(), value);
  } catch (e) {
    callFailed(e);
    return null;
  }
}

/** Ctrl+Enter in a cell editor (WP-59); the global shortcut of the same key rests while a cell is edited (shortcuts.js). */
const isFillKey = e => e.ctrlKey && !e.shiftKey && !e.altKey && !e.metaKey;

/**
 * Ctrl+Enter: the typed value for the marked cells or the selected rows the edited cell lies in (fillTargetOf). .NET sets
 * them all at once; the editor closes without an edit of its own, or stays open with the reason. False if there is nothing
 * to fill – the key is an Enter then.
 */
async function fillAll(params, value, showError) {
  const targets = params.state.fillTargets?.(params.node, params.column.getColId());
  if (!targets) return false;
  try {
    const result = await params.state.dotnet.invokeMethodAsync('OnFillValue', params.column.getColId(), value, targets.marks, targets.rows);
    if (result.error != null) {
      if (result.error) showError(result.error); // empty: a read failed and has said why
      return true;
    }
    // The value is set already: cancel the editor. Its own params.stopEditing(true) would not – the flag there means
    // "do not move on after the edit", the value would still go to .NET as an edit of its own.
    params.api.stopEditing(true);
    params.state.applyUpdates(result.rows);
  } catch (e) {
    callFailed(e);
  }
  return true;
}

/** The current value could not be read from .NET: the editor ends without a value – left open and empty, it wrote NULL (A47). */
function cancelEditor(params, e) {
  if (e) callFailed(e);
  params.stopEditing(true);
}

class FerretCellEditor {
  init(params) {
    this.params = params;
    const meta = params.meta;
    const state = params.state;
    const retry = state.retry && state.retry.key === cellKey(params.node, params.column.getColId()) ? state.retry : null;
    state.retry = null;

    this.eGui = document.createElement('div');
    this.eGui.className = 'fs-editor' + (meta.multiline ? ' multiline' : '');
    this.input = document.createElement(meta.multiline ? 'textarea' : 'input');
    this.input.className = 'fs-editor-input';
    this.input.spellcheck = false;
    this.eGui.appendChild(this.input);
    if (retry) {
      this.input.value = retry.text;
      const error = document.createElement('div');
      error.className = 'fs-editor-error';
      error.textContent = retry.error;
      this.eGui.appendChild(error);
    } else if (params.eventKey && params.eventKey.length === 1) {
      this.input.value = params.eventKey; // typing starts editing: replace the value
    } else if (params.eventKey === 'Backspace' || params.eventKey === 'Delete' || params.eventKey === ALTGR_FOLLOWS) {
      this.input.value = ''; // AltGr: the character follows as text input into the focused box
    } else {
      this.input.disabled = true;
      state.dotnet.invokeMethodAsync('GetEditText', ...rowArgs(params.node, params.data), params.column.getColId())
        .then(text => {
          this.input.disabled = false;
          this.input.value = text ?? '';
          this.input.focus();
          this.input.select();
        })
        .catch(e => cancelEditor(params, e));
    }
  }
  /**
   * Enter (not Shift+Enter in the multi-line box): .NET checks the value first; the editor only closes when it is
   * valid, otherwise it stays open with the message below it. The grid ignores Enter while editing
   * (suppressKeyboardEvent), so this is the only way Enter ends an edit.
   */
  listenForEnter() {
    const params = this.params;
    this.input.addEventListener('keydown', async e => {
      if (e.key !== 'Enter' || (params.meta.multiline && e.shiftKey)) return;
      e.preventDefault();
      e.stopPropagation();
      if (isFillKey(e) && await fillAll(params, this.input.value, message => this.showError(message))) return;
      const error = await validateEdit(params, this.input.value);
      if (error) {
        this.showError(error);
      } else {
        params.stopEditing();
      }
    });
    this.input.addEventListener('input', () => this.showError(null));
  }
  showError(message) { showEditorError(this.eGui, message); }
  getGui() { return this.eGui; }
  afterGuiAttached() {
    this.listenForEnter();
    this.input.focus();
    if (!this.params.eventKey || this.params.eventKey.length !== 1) this.input.select();
    else this.input.setSelectionRange(this.input.value.length, this.input.value.length);
  }
  getValue() { return this.input.value; }
  // Still loading its text from .NET (the box is empty and disabled): ending the edit now – a click elsewhere, Ctrl+S –
  // takes nothing, else the empty box would be written as NULL (R4a, found in the E2E run).
  isCancelAfterEnd() { return this.input.disabled; }
  isPopup() { return !!this.params.meta.multiline; }
}

/**
 * Cell editor for enum and converted bool columns (WP-12): a list of the members; the value is the database value, so
 * .NET checks and writes it like typed text. Picking with the mouse takes the value at once; with the keyboard Enter
 * does (arrow keys on the closed list already change the selection). Typing picks by value first, then by label
 * (`0` or `Ne` for `Neu (0)`, cellTyping.js); the key that started editing counts as typed.
 */
class FerretSelectEditor {
  init(params) {
    this.params = params;
    const meta = params.meta;
    params.state.retry = null;
    this.eGui = document.createElement('div');
    this.eGui.className = 'fs-editor';
    this.select = document.createElement('select');
    this.select.className = 'fs-editor-input fs-editor-select';
    if (meta.nullable || params.node.rowPinned) this.addOption('', 'NULL');
    for (const option of meta.options) this.addOption(option.value, option.label);
    this.eGui.appendChild(this.select);
    this.select.disabled = true;
    this.typed = new TypedText();
    const clear = params.eventKey === 'Backspace' || params.eventKey === 'Delete';
    const started = params.eventKey?.length === 1 ? params.eventKey : null;
    params.state.dotnet.invokeMethodAsync('GetEditText', ...rowArgs(params.node, params.data), params.column.getColId())
      .then(text => {
        const value = clear ? '' : text ?? '';
        // A value without a member stays selectable, so leaving the editor does not change it.
        if (![...this.select.options].some(o => o.value === value)) this.addOption(value, params.state.texts.noMember.replace('{0}', value));
        this.select.value = value;
        // Typed onto the cell: that key picks, from the first member on (not the next after the current one).
        if (started) this.type(started, null);
        this.select.disabled = false;
        this.loaded = true;
        this.select.focus();
      })
      .catch(e => cancelEditor(params, e));
  }
  addOption(value, label) {
    const option = document.createElement('option');
    option.value = value;
    option.textContent = label;
    this.select.appendChild(option);
  }
  /** Adds a typed character to what was typed (a pause starts anew) and picks the member it means, if one does. */
  type(char, current) {
    const text = this.typed.add(char, Date.now());
    const value = pickTyped(optionsOf(this.select.options), text, current);
    if (value !== null) this.select.value = value;
  }
  async commit(fill) {
    const params = this.params;
    if (fill && await fillAll(params, this.select.value, message => showEditorError(this.eGui, message))) return;
    const error = await validateEdit(params, this.select.value);
    if (error) {
      showEditorError(this.eGui, error);
    } else {
      params.stopEditing();
    }
  }
  afterGuiAttached() {
    let pointer = false;
    this.select.addEventListener('pointerdown', () => { pointer = true; });
    this.select.addEventListener('change', () => { if (pointer) this.commit(); });
    this.select.addEventListener('keydown', e => {
      pointer = false;
      // Typed characters pick here, not in the list's own search (it knows only the labels). A space while typing
      // belongs to the text (`Neuer Kunde`); on its own it opens the list as before.
      const char = typedCharOf(e);
      if (char !== null && (char !== ' ' || this.typed.active(Date.now()))) {
        e.preventDefault();
        e.stopPropagation();
        showEditorError(this.eGui, null);
        this.type(char, this.select.value);
        return;
      }
      if (e.key !== 'Enter') return;
      e.preventDefault();
      e.stopPropagation();
      this.commit(isFillKey(e));
    });
    this.select.focus();
  }
  getGui() { return this.eGui; }
  getValue() { return this.select.value; }
  // Left before the current value arrived: nothing was chosen.
  isCancelAfterEnd() { return !this.loaded; }
  isPopup() { return false; }
}

/**
 * Cell editor for flags enums stored as their number: a drop-down list of the flags to tick (and NULL where allowed).
 * The boxes, which are ticked and the bits no member stands for come from .NET (GetFlagsEdit); here they are only shown
 * and the ticked values added up. The sum is checked and written by .NET like typed text. Space or a click ticks, Enter
 * takes the value, Esc drops it. Typed digits are the value itself: .NET ticks the flags it holds (`5`: 1 and 4) and shows
 * the bits no member stands for as a box of their own (GetFlagsEditOf). Letters go to the flag whose label starts with
 * them (cellTyping.js); the key that started editing counts as typed.
 */
class FerretFlagsEditor {
  init(params) {
    this.params = params;
    params.state.retry = null;
    this.eGui = document.createElement('div');
    this.eGui.className = 'fs-editor fs-flags';
    this.eGui.style.minWidth = `${Math.max(params.column.getActualWidth(), 220)}px`;
    this.summary = document.createElement('div');
    this.summary.className = 'fs-flags-summary';
    this.list = document.createElement('div');
    this.list.className = 'fs-flags-list';
    this.list.setAttribute('role', 'listbox');
    this.list.setAttribute('aria-multiselectable', 'true');
    this.eGui.append(this.summary, this.list);
    this.boxes = [];
    this.typed = new TypedText();
    this.typedRequests = 0;
    this.typedEdit = null; // the boxes for typed digits, on their way from .NET
    this.list.addEventListener('change', e => this.onChange(e.target));
    const clear = params.eventKey === 'Backspace' || params.eventKey === 'Delete';
    const started = params.eventKey?.length === 1 && params.eventKey !== ' ' ? params.eventKey : null;
    params.state.dotnet.invokeMethodAsync('GetFlagsEdit', ...rowArgs(params.node, params.data), params.column.getColId())
      .then(edit => {
        if (!edit) {
          cancelEditor(params); // nothing to edit (an empty popup stayed open before)
          return;
        }

        this.show(edit);
        // Backspace or Delete opened the editor: start from NULL where it can be chosen.
        if (clear && this.nullBox) this.onChange(this.nullBox.box, true);
        this.loaded = true;
        this.focusFirst();
        if (started) this.type(started, true);
      })
      .catch(e => cancelEditor(params, e));
  }
  /** The boxes as .NET describes them (GetFlagsEdit, GetFlagsEditOf); the value is edit.text until a box is ticked. */
  show(edit) {
    this.list.replaceChildren();
    this.nullBox = edit.null ? this.addItem(edit.null, 'fs-null') : null;
    this.boxes = edit.boxes.map(box => this.addItem(box, box.unknown ? 'fs-unknown-text' : null));
    this.raw = edit.raw ?? undefined;
    this.original = edit.text;
    this.changed = false;
    this.updateSummary();
  }
  /** A box as .NET describes it ({ value, label, name, checked }); its value is what it adds to the sum. */
  addItem(box, labelClass) {
    const item = document.createElement('label');
    item.className = 'fs-flags-item';
    const input = document.createElement('input');
    input.type = 'checkbox';
    input.checked = box.checked;
    const text = document.createElement('span');
    text.textContent = box.label;
    if (labelClass) text.className = labelClass;
    item.append(input, text);
    this.list.appendChild(item);
    return { box: input, bits: box.value === '' ? 0n : BigInt(box.value), name: box.name, label: box.label };
  }
  onChange(box, tick) {
    if (tick) box.checked = true;
    this.raw = undefined;
    if (box === this.nullBox?.box) {
      if (box.checked) for (const f of this.boxes) f.box.checked = false;
    } else if (this.nullBox && box.checked) {
      this.nullBox.box.checked = false;
    }
    this.changed = true;
    showEditorError(this.eGui, null);
    this.updateSummary();
  }
  value() {
    if (!this.changed) return this.original;
    if (this.nullBox?.box.checked) return '';
    return this.boxes.filter(f => f.box.checked).reduce((all, f) => all | f.bits, 0n).toString();
  }
  updateSummary() {
    const value = this.value();
    const names = this.boxes.filter(f => f.box.checked).map(f => f.name);
    this.summary.textContent = this.raw !== undefined ? this.raw
      : value === '' ? 'NULL'
      : `${names.length ? names.join(', ') : this.params.state.texts.noFlag} = ${value}`;
  }
  /**
   * Digits are the value itself: .NET takes it apart as for a stored value (GetFlagsEditOf) and the boxes are shown anew;
   * a bit no member stands for gets a ticked box of its own, so ticking another flag keeps it. Letters go to the flag
   * whose label starts with them (fromFirst: from the first one, not the next after the focused). Switching between a
   * number and a name starts anew.
   */
  type(char, fromFirst) {
    const now = Date.now();
    if (this.typed.active(now) && /\d/.test(char) !== isDigits(this.typed.text)) this.typed.reset();
    const text = this.typed.add(char, now);
    if (isDigits(text)) {
      const params = this.params;
      const request = ++this.typedRequests;
      this.typedEdit = params.state.dotnet.invokeMethodAsync('GetFlagsEditOf', ...rowArgs(params.node, params.data), params.column.getColId(), text)
        .then(edit => {
          if (request !== this.typedRequests || !edit) return; // typed on meanwhile, or the cell can no longer be edited
          const at = [...this.list.querySelectorAll('input')].indexOf(document.activeElement);
          this.show(edit);
          showEditorError(this.eGui, null);
          const boxes = this.list.querySelectorAll('input');
          (boxes[at] ?? boxes[0])?.focus(); // the old boxes are gone, and the keys with them
        })
        .catch(callFailed);
      return;
    }

    const all = this.nullBox ? [this.nullBox, ...this.boxes] : this.boxes;
    const options = all.map((f, i) => ({ value: f === this.nullBox ? '' : String(i), label: f === this.nullBox ? 'NULL' : f.label }));
    const focused = fromFirst ? -1 : all.findIndex(f => f.box === document.activeElement);
    const picked = pickLabel(options, text, focused < 0 ? null : options[focused].value);
    if (picked !== null) all[options.findIndex(o => o.value === picked)].box.focus();
  }
  focusFirst() {
    (this.list.querySelector('input:checked') ?? this.list.querySelector('input'))?.focus();
  }
  async commit(fill) {
    const params = this.params;
    await this.typedEdit; // Enter right after typing digits: their boxes first
    if (fill && await fillAll(params, this.value(), message => showEditorError(this.eGui, message))) return;
    const error = await validateEdit(params, this.value());
    if (error) {
      showEditorError(this.eGui, error);
    } else {
      params.stopEditing();
    }
  }
  afterGuiAttached() {
    this.list.addEventListener('keydown', e => {
      // Space ticks as before; other characters type (a number or the start of a label).
      const char = typedCharOf(e);
      if (char !== null && char !== ' ') {
        e.preventDefault();
        e.stopPropagation();
        this.type(char, false);
        return;
      }
      const boxes = [...this.list.querySelectorAll('input')];
      const index = boxes.indexOf(document.activeElement);
      const move = { ArrowDown: 1, ArrowUp: -1, Home: -boxes.length, End: boxes.length }[e.key];
      if (move !== undefined) {
        e.preventDefault();
        boxes[Math.max(0, Math.min(boxes.length - 1, index + move))]?.focus();
      } else if (e.key === 'Enter') {
        e.preventDefault();
        e.stopPropagation();
        this.commit(isFillKey(e));
      }
    });
    this.focusFirst();
  }
  getGui() { return this.eGui; }
  getValue() { return this.value(); }
  // Left before the current value arrived: nothing was chosen.
  isCancelAfterEnd() { return !this.loaded; }
  isPopup() { return true; }
  getPopupPosition() { return 'over'; }
}

/** Identifies a cell across redraws: new rows by their id, loaded rows by index. */
function cellKey(node, colId) {
  return (node.data?.__new ?? `r${node.rowIndex}`) + '|' + colId;
}

// Always an element: AG Grid inserts a string a renderer returns as HTML (innerHTML), and cell values come from the database.
function renderCell(params, texts) {
  if (!params.data) return '';
  const value = params.value;
  const span = document.createElement('span');
  if (value === null || value === undefined) {
    span.className = 'fs-null';
    span.textContent = 'NULL';
    return span;
  }
  span.textContent = value;
  // A foreign key value with the display value of its target (WP-31), or a dangling reference: one element, so the
  // value keeps its own alignment rules.
  const colId = params.column.getColId();
  const name = params.data.__n?.[colId];
  const notFound = params.data.__nm?.includes(colId);
  if (!name && !notFound) return span;
  const cell = document.createElement('span');
  cell.className = 'fs-named';
  const desc = document.createElement('span');
  desc.className = notFound ? 'fs-desc missing' : 'fs-desc';
  desc.textContent = notFound ? texts.notFound : name;
  cell.append(span, desc);
  return cell;
}

function width(meta) {
  if (meta.named) return 220; // a foreign key value with a name beside it (WP-31)
  if (meta.numeric) return 140;
  if (meta.category === 'Date') return 165;
  if (meta.category === 'Timestamp' || meta.category === 'TimestampWithTimeZone') return 215;
  return 180;
}

/** The ids of the displayed columns, pinned ones first: the order in which marked cells and columns count (WP-58). */
function displayOrder(api) {
  return api.getAllDisplayedColumns().map(c => c.getColId());
}

function cellOf(node) {
  const el = node?.nodeType === Node.ELEMENT_NODE ? node : node?.parentElement;
  return el?.closest('.ag-cell') ?? null;
}

/** True if the user marked text and the marking lies within a single cell. */
function textMarkedInOneCell() {
  const selection = window.getSelection();
  if (!selection || selection.isCollapsed || !selection.toString()) return false;
  const cell = cellOf(selection.anchorNode);
  return cell !== null && cell === cellOf(selection.focusNode);
}

const ROW_HEIGHT = 30;
const BLOCK_SIZE = 500;

/** First row at the top of the viewport (getFirstDisplayedRowIndex would include the render buffer). */
function firstVisibleRow(api) {
  return Math.floor(api.getVerticalPixelRange().top / ROW_HEIGHT);
}

/** Pinned columns in display order (primary key first); .NET drops the always pinned primary key. */
function reportPinned(api, dotnet) {
  dotnet.invokeMethodAsync('OnPinnedChanged', api.getDisplayedLeftColumns().map(c => c.getColId())).catch(callFailed);
}

/**
 * columns: [{ id, name, label, alternate, clrType, clrLine, type, typeLine, category, nullable, pk, fk, fkModel, numeric, sortable,
 *   pinned, options, unknownText, … }] in display order (GridColumn in .NET).
 * options (GridOptions in .NET):
 *   sorts: [{ colId, sort }] initial sort state.
 *   firstRow: rough scroll position to restore (0 = top); the block containing it is loaded on demand.
 *   editable: whether the workspace may write (v2); the columns say whether they can be edited at all.
 *   headerHeight: with the line for C# names (WP-12), without the type line; null for the theme default.
 *   table: a table tab – header menu, Del, pinning and scroll position are reported to .NET. A query result only
 *     answers GetRows, OnCopy, OnCopyColumn (WP-57), OnCopyCells (WP-58) and OnCellContextMenu, and what the options below
 *     ask for.
 *   form: the grid reports its focus (OnFocused) and the form key (OnFormKey) – always for a table, for a query result since
 *     WP-56.
 *   headerMenu: a query result reports a right click on a column header too (OnHeaderContextMenu, SQL editor, WP-57).
 *     Whole columns can be marked (WP-58) where there is a header menu: they mean all rows, read again.
 */
export function create(elementId, dotnet, columns, options) {
  // texts: what the grid shows itself (header tooltips, the member list), in the UI language – JS holds no texts.
  const { sorts, firstRow, editable, headerHeight, table, form, headerMenu, texts } = options;
  const reportsForm = table || !!form;
  registerModules();
  destroy(elementId);
  let restoreRow = firstRow > 0 ? firstRow : null;

  // Scrolls to the saved row in two steps: after the first block, extend the (still unknown) row count so the grid
  // can scroll there and fetch that block; once that block has arrived (which resets the row count), scroll again.
  function restoreScroll(blockStart, blockEnd, lastRow) {
    if (lastRow >= 0) restoreRow = Math.min(restoreRow, lastRow - 1);
    const row = restoreRow;
    if (row <= 0) {
      restoreRow = null;
    } else if (row >= blockStart && row < blockEnd) {
      restoreRow = null;
      setTimeout(() => api.ensureIndexVisible(row, 'top'));
    } else if (blockStart === 0) {
      setTimeout(() => {
        if (api.getDisplayedRowCount() <= row + BLOCK_SIZE) api.setRowCount(row + BLOCK_SIZE, false);
        api.ensureIndexVisible(row, 'top');
      });
    }
  }

  const sortById = new Map(sorts.map((s, i) => [s.colId, { sort: s.sort, index: i }]));
  const editState = { dotnet, retry: null, enabled: !!editable, texts };
  editStates.set(elementId, editState);
  const metaById = new Map(columns.map(meta => [meta.id, meta]));
  metas.set(elementId, metaById);
  headerHeights.set(elementId, headerHeight);

  // Marked cells (WP-58): a rectangle of cells (`cells`: { anchor, end }, each { row, col }) or whole columns (`marked`) –
  // never both, never together with selected rows. JS only knows what is marked; what happens with the cells .NET decides.
  let cells = null;
  let marked = new Set();
  let markAnchor = null; // the column a Ctrl+Shift+click on a header marks from
  let order = []; // the displayed columns when the marks were drawn
  let rect = null; // `cells` as rows and display positions (rectOf), for drawing
  const marksColumns = table || !!headerMenu; // whole columns need a way to read all rows: a table, the SQL editor

  // Ctrl+Enter in a cell editor (WP-59): the marked cells or columns, or the selected rows, the edited cell lies in; null
  // if only the cell itself – the value is then an ordinary edit.
  editState.fillTargets = (node, colId) => {
    if (!table || node.rowPinned || node.rowIndex == null) return null;
    const rows = selectedRows();
    const target = fillTargetOf(rect, marked, order, rows, node.rowIndex, colId);
    return target === 'marks' ? { marks: marksOf(), rows: [] } : target === 'rows' ? { marks: null, rows } : null;
  };
  editState.applyUpdates = updates => applyUpdates(api, updates);

  // How a cell is drawn: null if it is not marked, else which of its sides are the outline.
  const markAt = (p, colId) => (!rect && marked.size === 0) || p.node.rowPinned || p.node.rowIndex == null
    ? null
    : markOf(rect, marked, order, p.node.rowIndex, colId);

  const columnDefs = columns.map(meta => ({
    colId: meta.id,
    field: meta.id,
    headerName: meta.name,
    width: width(meta),
    sortable: meta.sortable,
    sort: sortById.get(meta.id)?.sort ?? null,
    sortIndex: sortById.get(meta.id)?.index ?? null,
    cellClass: () => meta.numeric ? 'fs-num' : meta.category === 'Date' || meta.category.startsWith('Timestamp') ? 'fs-date' : undefined,
    cellRenderer: p => renderCell(p, texts),
    // Editing (v2): new rows (pinned at the top) may fill the key; loaded rows not deleted and not too large a number.
    editable: p => !editState.enabled ? false : p.node.rowPinned === 'top'
      ? meta.editableNew
      : meta.editable && !!p.data && !p.data.__d && !(p.data.__ro ?? []).includes(meta.id),
    // Enum and bool columns of a C# model pick from their members, flags enums tick theirs (the model may arrive after
    // the grid was built).
    cellEditorSelector: () => ({
      component: meta.flags ? FerretFlagsEditor : meta.options ? FerretSelectEditor : FerretCellEditor,
      params: { meta, state: editState },
    }),
    cellClassRules: {
      'fs-unknown': p => !!p.data?.__u?.includes(meta.id),
      'fs-pending': p => p.data?.__s?.[meta.id] === 'p',
      'fs-flushed': p => p.data?.__s?.[meta.id] === 'f',
      'fs-range': p => !!markAt(p, meta.id),
      'fs-range-top': p => !!markAt(p, meta.id)?.top,
      'fs-range-bottom': p => !!markAt(p, meta.id)?.bottom,
      'fs-range-left': p => !!markAt(p, meta.id)?.left,
      'fs-range-right': p => !!markAt(p, meta.id)?.right,
    },
    // While editing, Enter belongs to the editor (check in .NET first, Shift+Enter: new line in the multi-line box).
    // The member list keeps the arrow keys while editing.
    suppressKeyboardEvent: p => p.editing && (p.event.key === 'Enter' || (!!meta.options && listKeys.has(p.event.key))),
    tooltip: p => (p.value === null || p.value === undefined ? null
      : [p.value, p.data?.__n?.[meta.id], p.data?.__nm?.includes(meta.id) ? texts.notFoundTitle : null, p.data?.__t?.[meta.id],
        p.data?.__u?.includes(meta.id) ? meta.unknownText : null].filter(Boolean).join('\n')),
    headerComponent: FerretHeader,
    // A function, not the object: AG Grid deep-copies plain objects of the column def, and updateColumns changes meta in place.
    headerComponentParams: {
      getMeta: () => meta,
      texts,
      markColumn: marksColumns ? (colId, extend) => markColumn(colId, extend) : null,
      isMarked: colId => marked.has(colId),
    },
    pinned: meta.pinned ? 'left' : null,
    // The primary key always stays pinned at the very left; other columns can also be pinned by dragging them there.
    lockPinned: meta.pk,
    lockPosition: meta.pk ? 'left' : undefined,
  }));

  const element = document.getElementById(elementId);
  // No WebView menu (Back, Reload, Inspect). On a column header, the column menu is a Blazor component; AG Grid's
  // columnHeaderContextMenu event carries no mouse position.
  element.addEventListener('contextmenu', e => {
    e.preventDefault();
    const colId = e.target.closest?.('.ag-header-cell')?.getAttribute('col-id');
    if (colId && (table || headerMenu)) {
      dotnet.invokeMethodAsync('OnHeaderContextMenu', colId, e.clientX, e.clientY, window.innerWidth, window.innerHeight).catch(callFailed);
    }
  });

  // The grid's keys, in the capture phase (before AG Grid): each handler says whether the key was its.
  // Ctrl+D after the actions the user can give a key (TakenFromGrid in Shortcuts.cs: such an action comes first).
  element.addEventListener('keydown', e => onDeleteKey(e) || onCopyKey(e) || onCopyColumnKey(e) || onFormKey(e) || onLobKey(e) || onEditHistoryKey(e)
    || onFillDownKey(e) || onMarkKey(e) || onSelectKey(e) || onAltGrKey(e), true);

  const selectedRows = () => api.getSelectedNodes().map(n => n.rowIndex).filter(i => i != null);

  // The selection follows the focus like in the Windows Explorer: moving to another row with the keyboard (or the form's
  // ▲ ▼) selects just that row, Shift+↑ ↓, Shift+PageUp/PageDown, Shift+Home/End and Ctrl+Shift+↑ ↓ (AG Grid's Home/End
  // and Ctrl+↑ ↓ go to the first or last row) select from the anchor to it and shrink again on the way back. New rows at
  // the top are never selected (AG Grid does not select pinned rows): focusing one clears the selection, so Del never
  // takes a row out of sight with it. Clicks stay AG Grid's own.
  // AG Grid raises cellFocused asynchronously, after the key is up: the key is kept a moment longer (a key that moved
  // nothing must not make a later focus change from .NET select).
  let keyMove = null; // after a navigation key: 'range' or 'end' (to the last row) with Shift, otherwise 'single'
  let keyPresses = 0;
  let anchor = null; // where a Shift range starts: the row last clicked or moved to without Shift
  let focusedRow = null; // the loaded row focused last
  // The rows the keyboard selected, { from, to } (to: Infinity after Ctrl+A or a range to the last row). Rows of a block
  // that arrives later within them are selected too: a block still on its way, the rows below the last loaded one, a
  // block dropped from the cache and fetched again. A click, Space or anything that loads other rows ends it.
  let span = null;
  element.addEventListener('keydown', e => {
    keyPresses++;
    keyMove = keyMoveOf(e);
  }, true);
  element.addEventListener('keyup', () => {
    const press = keyPresses;
    setTimeout(() => { if (press === keyPresses) keyMove = null; }, 250);
  }, true);
  element.addEventListener('mousedown', () => { keyMove = null; }, true);
  // A left click into the rows selects anew (AG Grid's): it ends the span. Here, not in onCellClicked – AG Grid raises
  // that later, a Ctrl+A right after the click would lose its span. A right click keeps the selection for the menu.
  element.addEventListener('mousedown', e => { if (e.button === 0 && e.target.closest?.('.ag-cell')) span = null; }, true);

  // Rows not loaded yet (a block still on its way) cannot be selected yet: the span takes them in when they arrive.
  function selectRange(gridApi, from, to, toEnd) {
    span = spanOf(from, to, toEnd);
    const range = new Set();
    for (let i = span.from; i <= Math.max(from, to); i++) {
      const node = gridApi.getDisplayedRowAtIndex(i);
      if (node?.data) range.add(node);
    }
    const dropped = gridApi.getSelectedNodes().filter(n => !range.has(n));
    if (dropped.length > 0) gridApi.setNodesSelected({ nodes: dropped, newValue: false });
    gridApi.setNodesSelected({ nodes: [...range], newValue: true });
  }

  // Ctrl+A: every loaded row; rows loaded later (scrolling on, a block still on its way) join while the span lasts.
  function selectAll(gridApi) {
    clearMarks();
    span = { from: 0, to: Infinity };
    const nodes = [];
    gridApi.forEachNode(node => { if (node.data && !node.rowPinned) nodes.push(node); });
    gridApi.setNodesSelected({ nodes, newValue: true });
  }

  // A block arrived (getRows): its rows within the span are selected.
  function selectArrived(gridApi, from, to) {
    const arrived = arrivedIn(span, from, to);
    if (!arrived) return;
    const nodes = [];
    for (let i = arrived[0]; i < arrived[1]; i++) {
      const node = gridApi.getDisplayedRowAtIndex(i);
      if (node?.data && !node.isSelected()) nodes.push(node);
    }
    if (nodes.length > 0) gridApi.setNodesSelected({ nodes, newValue: true });
  }

  function followFocus(gridApi, rowIndex, pinned, how) {
    clearMarks(); // the selected rows take over
    if (pinned) {
      if (gridApi.getSelectedNodes().length > 0) gridApi.deselectAll();
      anchor = null;
      span = null;
      return;
    }
    anchor = anchorFor(how, anchor, anchor !== null && !!gridApi.getDisplayedRowAtIndex(anchor)?.isSelected(), focusedRow, rowIndex);
    selectRange(gridApi, anchor, rowIndex, how === 'end');
  }

  // e.api: the event may come while createGrid still runs (before `api` is assigned).
  function onCellFocused(e) {
    if (e.rowIndex == null) return;
    const pinned = e.rowPinned === 'top';
    if (pinned || (keyMove && e.rowIndex !== focusedRow)) followFocus(e.api, e.rowIndex, pinned, keyMove);
    else if (keyMove) clearMarks(); // moving sideways in the row
    focusedRow = pinned ? null : e.rowIndex;
    reportFocus(e);
  }
  focusControls.set(elementId, {
    forgetKey: () => { keyMove = null; },
    // Other rows in the same places (filters changed, F5): neither the span nor marked cells mean them any longer.
    forgetSpan: () => {
      span = null;
      clearCells();
    },
    select: (rowIndex, pinned) => followFocus(api, rowIndex, pinned, 'single'),
    markColumn: colId => {
      marked = new Set();
      markColumn(colId, false);
    },
  });

  // Ctrl+A selects all rows, Esc clears the selection (only if there is one: otherwise Esc goes on). Space selects or
  // deselects the focused row and makes it the anchor (AG Grid's own Space knows nothing of the anchor, with Shift it
  // would select from where AG Grid last clicked). Not while editing (the editor's own keys, Esc cancels the input), not
  // on a new row at the top (it cannot be selected).
  function onSelectKey(e) {
    if (e.altKey || api.getEditingCells().length > 0) return false;
    if ((e.ctrlKey || e.metaKey) && !e.shiftKey && e.key.toLowerCase() === 'a') {
      e.preventDefault();
      e.stopPropagation();
      selectAll(api);
      return true;
    }
    if (e.key === 'Escape' && !e.ctrlKey && !e.shiftKey && !e.metaKey) {
      if (api.getSelectedNodes().length === 0) return false;
      e.preventDefault();
      e.stopPropagation();
      span = null;
      api.deselectAll();
      return true;
    }
    if (e.key !== ' ') return false;
    const cell = api.getFocusedCell();
    const node = cell && !cell.rowPinned ? api.getDisplayedRowAtIndex(cell.rowIndex) : null;
    if (!node?.data) return false;
    e.preventDefault();
    e.stopPropagation();
    clearMarks();
    node.setSelected(!node.isSelected());
    anchor = cell.rowIndex;
    span = null;
    return true;
  }

  // ---------- marked cells and columns (WP-58) ----------

  // Redraws the marks – the cells (cellClassRules) and the headers of marked columns – and reports them for the footer.
  function drawMarks() {
    order = displayOrder(api);
    rect = cells ? rectOf(cells.anchor, cells.end, order) : null;
    if (cells && !rect) cells = null; // a column of the rectangle is no longer displayed
    api.refreshCells({ suppressFlash: true });
    for (const header of element.querySelectorAll('.ag-header-cell[col-id]')) {
      header.querySelector('.fs-header')?.classList.toggle('marked', marked.has(header.getAttribute('col-id')));
    }
    reportSelection();
  }

  // Marks the cells from anchor to end: the selected rows and marked columns go.
  function markCells(start, end) {
    cells = { anchor: start, end };
    marked = new Set();
    markAnchor = null;
    span = null;
    if (api.getSelectedNodes().length > 0) api.deselectAll();
    drawMarks();
  }

  // Ctrl+click on a header (or the header menu): the column comes in or goes, with Shift the columns up to it come in.
  function markColumn(colId, extend) {
    ({ marked, anchor: markAnchor } = markedColumns(marked, markAnchor, colId, extend, displayOrder(api)));
    cells = null;
    span = null;
    if (api.getSelectedNodes().length > 0) api.deselectAll();
    drawMarks();
  }

  // True if anything was marked.
  function clearMarks() {
    if (!cells && marked.size === 0) return false;
    cells = null;
    marked = new Set();
    markAnchor = null;
    drawMarks();
    return true;
  }

  // Sorting, filtering, moving or pinning a column: other cells in the same places. Marked columns stay – they mean all rows.
  function clearCells() {
    if (!cells) return;
    cells = null;
    drawMarks();
  }

  // A column was moved or pinned: marked cells go, marked columns get their outline anew.
  function clearMarksAfterMove() {
    if (cells) clearCells();
    else if (marked.size > 0) drawMarks();
  }

  // What is marked, for .NET: the rows and columns of the marked cells, or whole columns (all rows); null if nothing is.
  function marksOf() {
    if (rect) return { fromRow: rect.top, toRow: rect.bottom, columns: rect.cols, whole: false };
    if (marked.size > 0) return { fromRow: -1, toRow: -1, columns: inDisplayOrder(marked, displayOrder(api)), whole: true };
    return null;
  }

  // The end of the marked cells in view (keys, scrolling while marking by mouse).
  function showEnd() {
    api.ensureIndexVisible(cells.end.row);
    api.ensureColumnVisible(cells.end.col);
  }

  const pageRows = () => {
    const range = api.getVerticalPixelRange();
    return Math.max(1, Math.floor((range.bottom - range.top) / ROW_HEIGHT) - 1);
  };

  // Shift+← → marks cells from the focused one on; with marked cells Shift+↑ ↓ and Shift+Page↑ Page↓ extend them (without,
  // they stay the row selection). Esc clears the marks (before it clears selected rows). Not while editing, not on the new
  // rows at the top. The focus stays where the marking started, like Excel's active cell.
  function onMarkKey(e) {
    if (e.altKey || e.ctrlKey || e.metaKey || api.getEditingCells().length > 0) return false;
    if (e.key === 'Escape') {
      if (e.shiftKey || !clearMarks()) return false;
      e.preventDefault();
      e.stopPropagation();
      return true;
    }
    if (!e.shiftKey || (!cells && e.key !== 'ArrowLeft' && e.key !== 'ArrowRight')) return false;
    let start = cells?.anchor;
    let end = cells?.end;
    if (!cells) {
      const focused = api.getFocusedCell();
      if (!focused || focused.rowPinned || focused.rowIndex == null) return false;
      start = end = { row: focused.rowIndex, col: focused.column.getColId() };
    }
    const moved = movedEnd(end, e.key, displayOrder(api), api.getDisplayedRowCount(), pageRows());
    if (!moved) return false;
    e.preventDefault();
    e.stopPropagation();
    markCells(start, moved);
    showEnd();
    return true;
  }

  // The cell under an element: row index and column id; null outside the rows (the new rows at the top included).
  function cellAt(target) {
    const cell = target?.closest?.('.ag-cell');
    if (!cell || !element.contains(cell)) return null;
    const index = cell.closest('.ag-row')?.getAttribute('row-index') ?? '';
    const col = cell.getAttribute('col-id');
    return col && /^\d+$/.test(index) ? { row: Number(index), col } : null;
  }

  // Dragging from one cell into another marks the cells between; within one cell the mouse marks text as before, which goes
  // once it leaves the cell. Beyond the edge of the rows the grid scrolls on. Shift+click extends marked cells (without,
  // it stays the row selection). It starts in mousedown: AG Grid raises onCellClicked later (ui-grid.md).
  let drag = null; // the left button held down in a cell: { start, active, x, y, timer }
  let swallowClick = false; // the click after marking by mouse selects no row
  element.addEventListener('mousedown', e => {
    swallowClick = false;
    if (e.button !== 0 || api.getEditingCells().length > 0) return;
    const cell = cellAt(e.target);
    if (!cell) return;
    if (e.shiftKey && cells) {
      e.stopPropagation(); // AG Grid would select the rows in between
      swallowClick = true;
      markCells(cells.anchor, cell);
      return;
    }
    clearMarks();
    if (e.ctrlKey || e.metaKey || e.shiftKey) return; // the row selection's own clicks
    drag = { start: cell, active: false, x: e.clientX, y: e.clientY, timer: null, outside: false };
    document.addEventListener('mousemove', onDragMove, true);
    document.addEventListener('mouseup', onDragEnd, true);
    document.addEventListener('mouseout', onDragOut, true);
  }, true);

  // The mouse left the page while marking. Released out there, the WPF host passes no mouseup (seen with real input in the
  // E2E): the grid stops scrolling on until the mouse is back – the next move tells whether the button is still down.
  function onDragOut(e) {
    if (drag && !e.relatedTarget) drag.outside = true;
  }
  element.addEventListener('click', e => {
    if (!swallowClick) return;
    swallowClick = false;
    e.stopPropagation();
  }, true);

  function onDragMove(e) {
    if (!drag) return;
    if ((e.buttons & 1) === 0) {
      onDragEnd(); // released where no mouseup reached the page
      return;
    }
    drag.x = e.clientX;
    drag.y = e.clientY;
    drag.outside = false;
    const cell = cellAt(document.elementFromPoint(e.clientX, e.clientY));
    if (!drag.active) {
      if (!cell || (cell.row === drag.start.row && cell.col === drag.start.col)) return;
      drag.active = true;
      window.getSelection()?.removeAllRanges();
      element.classList.add('fs-marking');
      drag.timer = setInterval(scrollWhileMarking, 60);
      markCells(drag.start, cell);
    } else if (cell && (cell.row !== cells.end.row || cell.col !== cells.end.col)) {
      markCells(cells.anchor, cell);
    }
  }

  function onDragEnd() {
    document.removeEventListener('mousemove', onDragMove, true);
    document.removeEventListener('mouseup', onDragEnd, true);
    document.removeEventListener('mouseout', onDragOut, true);
    if (drag?.active) {
      swallowClick = true; // the click lands on the row (or on what both cells share)
      clearInterval(drag.timer);
      element.classList.remove('fs-marking');
    }
    drag = null;
  }

  // The mouse at or beyond the edge of the rows while marking: a column further per step, rows the faster the farther
  // the mouse is beyond the edge. The rows' area is the grid's viewport below the header (AG Grid 36: `.ag-grid-viewport`,
  // without the scrollbars).
  function scrollWhileMarking() {
    if (!drag?.active || drag.outside || !cells) return;
    const view = (element.querySelector('.ag-grid-viewport') ?? element).getBoundingClientRect();
    const top = element.querySelector('.ag-header')?.getBoundingClientRect().bottom ?? view.top;
    const edge = 6;
    const beyond = drag.y >= view.bottom - edge ? drag.y - view.bottom + edge : drag.y <= top + edge ? top + edge - drag.y : 0;
    const rows = beyond === 0 ? null : drag.y > top + edge ? 'PageDown' : 'PageUp';
    const columns = drag.x >= view.right - edge ? 'ArrowRight' : drag.x <= view.left + edge ? 'ArrowLeft' : null;
    if (!rows && !columns) return;
    const shown = displayOrder(api);
    const count = api.getDisplayedRowCount();
    let end = cells.end;
    if (rows) end = movedEnd(end, rows, shown, count, Math.max(1, Math.round(beyond / 8)));
    if (columns) end = movedEnd(end, columns, shown, count, 1);
    if (end.row === cells.end.row && end.col === cells.end.col) return;
    markCells(cells.anchor, end);
    showEnd();
  }

  // Del (not while editing): mark the selected rows for deletion – pending until written, can be reverted. .NET decides
  // between the focused new row and the selected loaded rows. On marked cells or columns: NULL (WP-59).
  function onDeleteKey(e) {
    if (!table || e.key !== 'Delete' || e.ctrlKey || e.altKey || api.getEditingCells().length > 0) return false;
    const marks = marksOf();
    if (marks) {
      e.preventDefault();
      if (editState.enabled) dotnet.invokeMethodAsync('OnClearMarks', marks).then(updates => applyUpdates(api, updates)).catch(callFailed);
      return true;
    }
    const loaded = selectedRows();
    const newId = newIdOf(api, api.getFocusedCell());
    if (loaded.length > 0 || newId) {
      e.preventDefault();
      dotnet.invokeMethodAsync('OnDeleteKey', loaded, newId).then(updates => applyUpdates(api, updates)).catch(callFailed);
    }
    return true;
  }

  // Ctrl+D on marked cells or columns (WP-59): the top row's values into the rows below, as in Excel. Without marks the key
  // does nothing here.
  function onFillDownKey(e) {
    if (!table || !(e.ctrlKey || e.metaKey) || e.shiftKey || e.altKey || e.key.toLowerCase() !== 'd' || api.getEditingCells().length > 0) return false;
    const marks = marksOf();
    if (!marks) return false;
    e.preventDefault();
    e.stopPropagation();
    if (editState.enabled) dotnet.invokeMethodAsync('OnFillDown', marks).then(updates => applyUpdates(api, updates)).catch(callFailed);
    return true;
  }

  // Ctrl+C: the marked cells or columns (WP-58), the focused cell, or the selected rows if there are several (AG Grid
  // Community has no clipboard). Text marked with the mouse inside one cell is copied by the browser as usual.
  function onCopyKey(e) {
    if (!(e.ctrlKey || e.metaKey) || e.shiftKey || e.altKey || e.key.toLowerCase() !== 'c') return false;
    if (textMarkedInOneCell()) return true;
    const marks = marksOf();
    if (marks) {
      e.preventDefault();
      dotnet.invokeMethodAsync('OnCopyCells', marks, false).catch(callFailed);
      return true;
    }
    const cell = api.getFocusedCell();
    if (!cell || cell.rowIndex == null || cell.rowPinned) return true;
    e.preventDefault();
    dotnet.invokeMethodAsync('OnCopy', cell.rowIndex, cell.column.getColId(), selectedRows()).catch(callFailed);
    return true;
  }

  // The values of the focused cell's column from the selected rows (WP-57) or the marked cells (WP-58): no default key, the
  // user gives it one. In both grids; not while a cell is edited.
  function onCopyColumnKey(e) {
    const key = localCombo('copyColumn');
    if (!key || comboOf(e) !== key || api.getEditingCells().length > 0) return false;
    e.preventDefault();
    e.stopPropagation();
    const cell = api.getFocusedCell();
    const marks = marksOf();
    if (marks) {
      // The marked cells of the focused column (it is among them), else all of them.
      const col = cell?.column.getColId();
      dotnet.invokeMethodAsync('OnCopyCells', marks.columns.includes(col) ? { ...marks, columns: [col] } : marks, false).catch(callFailed);
    } else if (cell && cell.rowIndex != null && !cell.rowPinned) {
      dotnet.invokeMethodAsync('OnCopyColumn', cell.rowIndex, cell.column.getColId(), selectedRows()).catch(callFailed);
    }
    return true;
  }

  // Shift+click selects a range of rows; without this the browser would also mark text across the cells.
  element.addEventListener('mousedown', e => { if (e.shiftKey) e.preventDefault(); }, true);

  // Double click starts editing. The WPF host passes the second mouse-down of a real double click twice, so the
  // browser never raises dblclick (.claude/rules/ui.md) – AG Grid's own double-click editing would not react.
  element.addEventListener('click', e => {
    if (e.detail < 2 || api.getEditingCells().length > 0) return;
    const cell = e.target.closest?.('.ag-cell');
    const rowElement = cell?.closest('.ag-row');
    const colId = cell?.getAttribute('col-id');
    // New rows pinned at the top carry row-index="t-0", "t-1" …
    const rawIndex = rowElement?.getAttribute('row-index') ?? '';
    const pinned = rawIndex.startsWith('t-') ? 'top' : undefined;
    const rowIndex = Number(pinned ? rawIndex.slice(2) : rawIndex);
    if (!colId || rawIndex === '' || Number.isNaN(rowIndex)) return;
    if (metaById.get(colId)?.lob) {
      openLob(rowIndex, pinned ? api.getPinnedTopRow(rowIndex)?.data?.__new : null, colId);
    } else {
      api.startEditingCell({ rowIndex, colKey: colId, rowPinned: pinned });
    }
  });

  // LOB cells are not edited in the cell: double click or Enter opens the LOB editor (a Blazor dialog) – also on a
  // locked workspace, to read the whole value.
  function openLob(rowIndex, newId, colId) {
    dotnet.invokeMethodAsync('OnLobCell', newId ? -1 : rowIndex, newId ?? null, colId).catch(callFailed);
  }

  // Alt+Enter by default (Windows' "properties", changeable since WP-25): the form of the focused row, or the comparison
  // of the selected rows (WP-21). Only in the grid, not a global shortcut – the SQL and LINQ editors keep their keys.
  function onFormKey(e) {
    const formKey = localCombo('form');
    if (!reportsForm || !formKey || comboOf(e) !== formKey || api.getEditingCells().length > 0) return false;
    e.preventDefault();
    e.stopPropagation();
    const cell = api.getFocusedCell();
    dotnet.invokeMethodAsync('OnFormKey', cell && !cell.rowPinned ? cell.rowIndex : -1, newIdOf(api, cell), selectedRows()).catch(callFailed);
    return true;
  }

  // Ctrl+Z / Ctrl+Y by default (WP-30, changeable): take back or apply again a pending edit of this tab. Only in the grid,
  // not while a cell is edited (the input's own undo) – the SQL and LINQ editors keep their keys; written changes are
  // taken back with ↶.
  function onEditHistoryKey(e) {
    if (!table || api.getEditingCells().length > 0) return false;
    const combo = comboOf(e);
    const undo = combo !== null && combo === localCombo('undoEdit');
    if (!undo && (combo === null || combo !== localCombo('redoEdit'))) return false;
    e.preventDefault();
    e.stopPropagation();
    dotnet.invokeMethodAsync('OnEditHistoryKey', undo).catch(callFailed);
    return true;
  }

  // How many rows are selected and whether rows still to load join them (span to the end), or how many cells or whole
  // columns are marked (WP-58), for the footer; Shift+↓ held down changes it fast, so the state is reported at most every
  // moment – as it is then. Not after a pause: scrolling while marking by mouse changes it every 60 ms, the footer stood
  // still until the end (E2E).
  let selectionTimer = null;
  function reportSelection() {
    if (selectionTimer) return;
    selectionTimer = setTimeout(() => {
      selectionTimer = null;
      if (grids.get(elementId) !== api) return; // destroyed or created anew meanwhile
      const marks = marksOf();
      dotnet.invokeMethodAsync('OnSelectionChanged', api.getSelectedNodes().length, span?.to === Infinity,
        marks && !marks.whole ? marks.toRow - marks.fromRow + 1 : 0, marks?.columns.length ?? 0, !!marks?.whole).catch(callFailed);
    }, 60);
  }

  // The form follows the focused cell; arrow keys move it fast, so only the last position within a moment is reported.
  let focusTimer = null;
  function reportFocus(e) {
    if (!reportsForm || e.rowIndex == null) return;
    const newId = newIdOf(e.api, e);
    if (e.rowPinned && !newId) return;
    const colId = typeof e.column === 'string' ? e.column : e.column?.getColId?.() ?? null;
    clearTimeout(focusTimer);
    focusTimer = setTimeout(() => dotnet.invokeMethodAsync('OnFocused', e.rowPinned ? -1 : e.rowIndex, newId ?? null, colId).catch(callFailed), 60);
  }

  // A character typed with AltGr (@ € \ { } on a German keyboard) starts editing like any other. Chromium reports it as
  // Ctrl+Alt with the character, which AG Grid takes for a shortcut: editing starts with the character. The WPF host
  // reports the plain key with the AltGraph modifier (AltGr+Q: `q`), which AG Grid took for typed – and suppressed the
  // `@` that follows as text input: editing starts empty, and the default is left so the `@` lands in the box. A member
  // list has no use for such a character. Only on a focused cell (not the header) that can be edited; LOB cells open
  // their editor with Enter.
  function onAltGrKey(e) {
    const withChar = isAltGrChar(e);
    if ((!withChar && !isAltGrKey(e)) || !e.target.closest?.('.ag-cell') || api.getEditingCells().length > 0) return false;
    const cell = api.getFocusedCell();
    const meta = cell ? metaById.get(cell.column.getColId()) : null;
    if (!cell || cell.rowIndex == null || meta?.lob) return false;
    const node = cell.rowPinned ? api.getPinnedTopRow(cell.rowIndex) : api.getDisplayedRowAtIndex(cell.rowIndex);
    if (!node || !cell.column.isCellEditable(node)) return false;
    e.stopPropagation();
    if (withChar || meta?.options || meta?.flags) e.preventDefault();
    if (!withChar && (meta?.options || meta?.flags)) return true;
    api.startEditingCell({ rowIndex: cell.rowIndex, colKey: cell.column.getColId(), rowPinned: cell.rowPinned ?? undefined, key: withChar ? e.key : ALTGR_FOLLOWS });
    return true;
  }

  // Enter on a LOB cell opens the LOB editor (see openLob).
  function onLobKey(e) {
    if (e.key !== 'Enter' || e.ctrlKey || e.altKey || e.shiftKey || api.getEditingCells().length > 0) return false;
    const cell = api.getFocusedCell();
    if (!cell || !metaById.get(cell.column.getColId())?.lob) return false;
    e.preventDefault();
    e.stopPropagation();
    openLob(cell.rowIndex, newIdOf(api, cell), cell.column.getColId());
    return true;
  }

  const api = agGrid.createGrid(element, {
    theme: theme(headerHeight),
    columnDefs,
    defaultColDef: { resizable: true, minWidth: 70 },
    rowModelType: 'infinite',
    cacheBlockSize: BLOCK_SIZE,
    maxBlocksInCache: 40,
    maxConcurrentDatasourceRequests: 1,
    infiniteInitialRowCount: 1,
    // Since AG Grid 35 the infinite row model shows "No Rows" / "No Matching Rows" itself (English, no localeText): the
    // tab's footer already says how many rows there are.
    suppressOverlays: ['noRows', 'noMatchingRows'],
    // Click selects a row, Ctrl+click adds/removes, Shift+click selects a range (only loaded rows).
    rowSelection: { mode: 'multiRow', checkboxes: false, headerCheckbox: false, enableClickSelection: true },
    enableCellTextSelection: true,
    ensureDomOrder: true,
    tooltipShowDelay: 700,
    suppressMultiSort: false,
    animateRows: false,
    // Editing: the grid only reports the input; .NET checks it and sends the row back (no business logic here).
    readOnlyEdit: true,
    singleClickEdit: false,
    stopEditingWhenCellsLoseFocus: true,
    pinnedTopRowData: [],
    rowClassRules: {
      'fs-row-deleted': p => !!p.data?.__d,
      'fs-row-new': p => p.node.rowPinned === 'top',
    },
    onCellEditRequest: e => {
      const colId = e.column.getColId();
      const text = e.newValue ?? '';
      // True if .NET took the value; a shortcut that writes waits for it (finishEditing).
      const edit = (async () => {
        try {
          const result = await dotnet.invokeMethodAsync('OnCellEdit', ...rowArgs(e.node, e.data), colId, text);
          if (result.error) {
            // Left with Tab or a click elsewhere: reopen the editor with the rejected text and the reason.
            editState.retry = { key: cellKey(e.node, colId), text, error: result.error };
            setTimeout(() => {
              api.stopEditing(true);
              api.startEditingCell({ rowIndex: e.node.rowIndex, colKey: colId, rowPinned: e.node.rowPinned ?? undefined });
            });
            return false;
          }

          if (result.row) e.node.setData(result.row);
          return true;
        } catch (err) {
          console.error(err);
          return false;
        }
      })();
      pendingEdits.add(edit);
      edit.finally(() => pendingEdits.delete(edit));
    },
    onBodyScrollEnd: () => { if (table) dotnet.invokeMethodAsync('OnScrolled', firstVisibleRow(api)).catch(callFailed); },
    onCellFocused,
    // A click without Shift sets where a Shift+↑ ↓ range starts (the selection itself is AG Grid's).
    onCellClicked: e => { if (!e.event?.shiftKey && !e.node.rowPinned) anchor = e.rowIndex; },
    // Other rows in the same places: the span no longer means them. A block still loading counts against
    // maxConcurrentDatasourceRequests even after the grid dropped it, so .NET cancels it – the new order waited behind it.
    // The new order goes along: this event comes after the grid may already have asked for its first block.
    onSortChanged: e => {
      span = null;
      clearCells();
      if (!table) return;
      const sortModel = e.api.getColumnState()
        .filter(s => s.sort)
        .sort((a, b) => (a.sortIndex ?? 0) - (b.sortIndex ?? 0))
        .map(s => ({ colId: s.colId, sort: s.sort }));
      dotnet.invokeMethodAsync('OnSortChanged', sortModel).catch(callFailed);
    },
    onSelectionChanged: reportSelection,
    // Pinned or reordered within the pinned area by dragging (changes from setPinned have source 'api'). Marked cells mean
    // other columns afterwards; marked columns get their outline anew.
    onColumnPinned: e => {
      if (table && e.source?.startsWith('ui')) reportPinned(api, dotnet);
      clearMarksAfterMove();
    },
    onColumnMoved: e => {
      if (!e.finished) return;
      if (table && e.source?.startsWith('ui') && e.column?.getPinned()) reportPinned(api, dotnet);
      clearMarksAfterMove();
    },
    // The menu itself is a Blazor component; the grid only reports where the user right-clicked.
    // Right-click on a selected row keeps the selection (export of several rows), otherwise selects just this row. Inside
    // marked cells or columns (WP-58) they stay, and no row is selected: the row's own entries take the clicked row.
    onCellContextMenu: e => {
      if (e.rowIndex == null || !e.data || !e.event || e.node.rowPinned) return; // new rows: Del removes them
      const colId = e.column.getColId();
      let marks = marksOf();
      if (marks && !(marks.whole ? marks.columns.includes(colId) : markOf(rect, null, order, e.rowIndex, colId))) {
        clearMarks();
        marks = null;
      }
      if (!marks && !e.node.isSelected()) {
        e.node.setSelected(true, true);
        anchor = e.rowIndex;
        span = null;
      }
      const selected = marks ? [e.rowIndex] : api.getSelectedNodes().map(n => n.rowIndex).filter(i => i != null);
      dotnet.invokeMethodAsync('OnCellContextMenu', e.rowIndex, colId, selected, marks,
        e.event.clientX, e.event.clientY, window.innerWidth, window.innerHeight).catch(callFailed);
    },
    datasource: {
      getRows: async params => {
        try {
          const sortModel = params.sortModel.map(s => ({ colId: s.colId, sort: s.sort }));
          const page = await dotnet.invokeMethodAsync('GetRows', params.startRow, params.endRow, sortModel);
          if (page.failed) {
            params.failCallback(); // .NET shows why (footer, status)
            return;
          }
          params.successCallback(page.rows, page.lastRow);
          selectArrived(api, params.startRow, params.startRow + page.rows.length);
          if (restoreRow !== null) restoreScroll(params.startRow, params.startRow + page.rows.length, page.lastRow);
        } catch (e) {
          console.error(e);
          params.failCallback();
        }
      },
    },
  });

  grids.set(elementId, api);
}

function applyUpdates(api, updates) {
  for (const update of updates ?? []) {
    api.getDisplayedRowAtIndex(update.rowIndex)?.setData(update.row);
  }
}

/** Redraws loaded rows after delete/revert from the context menu: [{ rowIndex, row }]. */
export function updateRows(elementId, updates) {
  const api = grids.get(elementId);
  if (api) applyUpdates(api, updates);
}

/** New rows that are not inserted yet, pinned at the top. */
export function setNewRows(elementId, rows) {
  grids.get(elementId)?.setGridOption('pinnedTopRowData', rows);
}

/**
 * Redraws the new rows in place (names of their foreign keys arrived, WP-31): setNewRows would recreate every pinned
 * row and close an editor open in one of them. A different number of rows is set anew.
 */
export function updateNewRows(elementId, rows) {
  const api = grids.get(elementId);
  if (!api) return;
  if (api.getPinnedTopRowCount() !== rows.length) {
    api.setGridOption('pinnedTopRowData', rows);
    return;
  }
  rows.forEach((row, i) => api.getPinnedTopRow(i)?.setData(row));
}

/** Starts editing a cell; <paramref name="pinned"/>: index among the new rows at the top. */
export function startEditing(elementId, rowIndex, colId, pinned) {
  const api = grids.get(elementId);
  if (!api) return;
  api.ensureColumnVisible(colId);
  focusControls.get(elementId)?.forgetKey();
  api.setFocusedCell(rowIndex, colId, pinned ? 'top' : undefined);
  api.startEditingCell({ rowIndex, colKey: colId, rowPinned: pinned ? 'top' : undefined });
}

/**
 * Moves the focused cell to a row (the form's ▲ ▼, WP-21); a block not loaded yet is fetched. colId null keeps the
 * focused column. pinned: rowIndex counts the new rows at the top. keepFocus: the keyboard focus stays where it was
 * (in the form), the grid only shows the row. select: the row becomes the selection, like moving there with the keyboard.
 */
export function focusRow(elementId, rowIndex, colId, pinned, keepFocus, select) {
  const api = grids.get(elementId);
  if (!api) return;
  const column = colId ?? api.getFocusedCell()?.column.getColId() ?? api.getAllDisplayedColumns()[0]?.getColId();
  if (!column) return;
  const active = document.activeElement;
  if (!pinned) api.ensureIndexVisible(rowIndex);
  api.ensureColumnVisible(column);
  focusControls.get(elementId)?.forgetKey();
  api.setFocusedCell(rowIndex, column, pinned ? 'top' : undefined);
  if (select) focusControls.get(elementId)?.select(rowIndex, pinned);
  if (keepFocus && active && active !== document.body && !document.getElementById(elementId)?.contains(active)) {
    active.focus();
  }
}

/** Fetches the cached blocks again without moving (after writing): the rows show what the transaction holds. */
export function reload(elementId) {
  const api = grids.get(elementId);
  if (!api) return;
  api.stopEditing(true);
  api.refreshInfiniteCache();
}

/** Marks a whole column instead of anything marked before (the header menu's "Mark column", WP-58). */
export function markColumn(elementId, colId) {
  focusControls.get(elementId)?.markColumn(colId);
}

/** Drops all cached blocks and reloads from the first row (filters changed, F5). */
export function refresh(elementId) {
  const api = grids.get(elementId);
  if (!api) return;
  focusControls.get(elementId)?.forgetSpan();
  api.ensureIndexVisible(0, 'top');
  api.purgeInfiniteCache();
}

/**
 * Sorts by exactly these columns (a filter preset, WP-37): [{ colId, sort }] in sort order, all others unsorted. AG Grid
 * then drops its cached blocks and asks .NET for the first rows again, as after a click on a header.
 */
export function setSorts(elementId, sorts) {
  const api = grids.get(elementId);
  if (!api) return;
  api.ensureIndexVisible(0, 'top');
  api.applyColumnState({
    state: sorts.map((s, i) => ({ colId: s.colId, sort: s.sort, sortIndex: i })),
    defaultState: { sort: null, sortIndex: null },
  });
}

/**
 * Pins exactly these columns to the left, in this order. The other columns keep their current order; a released
 * column goes back in front of the first column that follows it in the schema (ids are c + schema index).
 */
export function setPinned(elementId, pinnedIds) {
  const api = grids.get(elementId);
  if (!api) return;
  const pinned = new Set(pinnedIds);
  const schemaIndex = colId => Number(colId.slice(1));
  const state = api.getColumnState().filter(s => !pinned.has(s.colId));
  const rest = state.filter(s => !s.pinned).map(s => s.colId);
  for (const released of state.filter(s => s.pinned)) {
    const at = rest.findIndex(id => schemaIndex(id) > schemaIndex(released.colId));
    rest.splice(at < 0 ? rest.length : at, 0, released.colId);
  }
  api.applyColumnState({
    state: [...pinnedIds.map(colId => ({ colId, pinned: 'left' })), ...rest.map(colId => ({ colId, pinned: null }))],
    applyOrder: true,
  });
}

/**
 * Column search (Ctrl+F): scrolls the column into view (pinned ones always are), focuses its cell in the first
 * visible row (so Ctrl+C and the context menu work right away) and briefly highlights header and cells.
 */
export function jumpToColumn(elementId, colId) {
  const api = grids.get(elementId);
  if (!api) return;
  api.ensureColumnVisible(colId, 'middle');
  const rows = api.getDisplayedRowCount();
  focusControls.get(elementId)?.forgetKey();
  if (rows > 0) api.setFocusedCell(Math.min(firstVisibleRow(api), rows - 1), colId);
  // The grid renders the newly visible columns after the scroll.
  setTimeout(() => {
    api.flashCells({ columns: [colId] });
    const header = document.querySelector(`#${elementId} .ag-header-cell[col-id="${colId}"]`);
    if (header) {
      header.classList.remove('fs-flash');
      void header.offsetWidth; // restart the animation when jumping to the same column again
      header.classList.add('fs-flash');
    }
  }, 50);
}

/** Known total (after COUNT): the scrollbar then reflects the full table. */
export function setRowCount(elementId, count) {
  grids.get(elementId)?.setRowCount(count, true);
}

/** The workspace was unlocked or locked again: editing on or off, without reloading the grid. */
export function setEditable(elementId, enabled) {
  const state = editStates.get(elementId);
  if (state) state.enabled = enabled;
  if (!enabled) grids.get(elementId)?.stopEditing(true);
}

export function destroy(elementId) {
  grids.get(elementId)?.destroy();
  grids.delete(elementId);
  focusControls.delete(elementId);
  metas.delete(elementId);
  headerHeights.delete(elementId);
  editStates.delete(elementId);
}

/**
 * The C# model arrived or the name or header setting changed (WP-12): new labels, badges, member lists and header height,
 * without rebuilding the grid (widths, order, sorting and scroll position stay). The rows are reloaded by .NET afterwards
 * where they read differently.
 */
export function updateColumns(elementId, columns, headerHeight, keepEditing) {
  const api = grids.get(elementId);
  const byId = metas.get(elementId);
  if (!api || !byId) return;
  // keepEditing: only badges and alignment change (names beside foreign keys came or went, WP-31) – an open editor
  // stays, AG Grid does not refresh a cell while it is edited.
  if (!keepEditing) api.stopEditing(true);
  // A column whose default width changes (names beside its values come or go) takes the new one – unless the user
  // resized it.
  const resized = [];
  for (const meta of columns) {
    const current = byId.get(meta.id);
    if (!current) continue;
    const before = width(current);
    Object.assign(current, meta);
    const after = width(current);
    if (before !== after && api.getColumn(meta.id)?.getActualWidth() === before) resized.push({ key: meta.id, newWidth: after });
  }
  if (resized.length) api.setColumnWidths(resized);
  api.refreshCells({ force: true }); // alignment classes follow meta
  if (headerHeights.get(elementId) !== headerHeight) {
    headerHeights.set(elementId, headerHeight);
    api.setGridOption('theme', theme(headerHeight));
  }
  api.refreshHeader();
}
