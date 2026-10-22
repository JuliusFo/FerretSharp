// Bridge to AG Grid Community (global `agGrid`, loaded by index.html). Infinite row model: every block of rows,
// including sorting, comes from .NET; cells arrive as display strings (null = SQL NULL). No business logic here.

import { comboOf, localCombo, setEditFinisher } from './shortcuts.js';
import { anchorFor, arrivedIn, keyMoveOf, spanOf } from './gridSelection.js';
import { TYPE_PAUSE, isAltGrChar, pickTyped, typedCharOf } from './cellTyping.js';

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
 * other name and the C# type (`KundeId · int`); `meta.clrLine` says whether the table has that line at all.
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
      '<div class="fs-h-type"></div>';
    this.eGui.querySelector('.fs-h-name').textContent = meta.label;
    if (meta.clrLine) {
      this.eGui.querySelector('.fs-h-clr').textContent = [meta.alternate, meta.clrType].filter(Boolean).join(' · ');
    }
    this.eGui.querySelector('.fs-h-type').textContent = meta.type + (meta.nullable ? '' : ' · NOT NULL');
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
    if (meta.sortable) {
      this.eGui.addEventListener('click', e => params.progressSort(e.shiftKey));
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
    } else if (params.eventKey === 'Backspace' || params.eventKey === 'Delete') {
      this.input.value = '';
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
    this.typed = '';
    this.typedAt = 0;
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
    const now = Date.now();
    this.typed = (now - this.typedAt > TYPE_PAUSE ? '' : this.typed) + char;
    this.typedAt = now;
    const options = [...this.select.options].map(o => ({ value: o.value, label: o.textContent }));
    const value = pickTyped(options, this.typed, current);
    if (value !== null) this.select.value = value;
  }
  async commit() {
    const params = this.params;
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
      if (char !== null && (char !== ' ' || this.typed && Date.now() - this.typedAt <= TYPE_PAUSE)) {
        e.preventDefault();
        e.stopPropagation();
        showEditorError(this.eGui, null);
        this.type(char, this.select.value);
        return;
      }
      if (e.key !== 'Enter') return;
      e.preventDefault();
      e.stopPropagation();
      this.commit();
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
 * takes the value, Esc drops it.
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
    this.list.addEventListener('change', e => this.onChange(e.target));
    const clear = params.eventKey === 'Backspace' || params.eventKey === 'Delete';
    params.state.dotnet.invokeMethodAsync('GetFlagsEdit', ...rowArgs(params.node, params.data), params.column.getColId())
      .then(edit => {
        if (!edit) {
          cancelEditor(params); // nothing to edit (an empty popup stayed open before)
          return;
        }

        this.nullBox = edit.null ? this.addItem(edit.null, 'fs-null') : null;
        this.boxes = edit.boxes.map(box => this.addItem(box, box.unknown ? 'fs-unknown-text' : null));
        this.raw = edit.raw ?? undefined;
        this.original = edit.text;
        // Backspace or Delete opened the editor: start from NULL where it can be chosen.
        if (clear && this.nullBox) this.onChange(this.nullBox.box, true);
        this.updateSummary();
        this.loaded = true;
        this.focusFirst();
      })
      .catch(e => cancelEditor(params, e));
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
    return { box: input, bits: box.value === '' ? 0n : BigInt(box.value), name: box.name };
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
  focusFirst() {
    (this.list.querySelector('input:checked') ?? this.list.querySelector('input'))?.focus();
  }
  async commit() {
    const params = this.params;
    const error = await validateEdit(params, this.value());
    if (error) {
      showEditorError(this.eGui, error);
    } else {
      params.stopEditing();
    }
  }
  afterGuiAttached() {
    this.list.addEventListener('keydown', e => {
      const boxes = [...this.list.querySelectorAll('input')];
      const index = boxes.indexOf(document.activeElement);
      const move = { ArrowDown: 1, ArrowUp: -1, Home: -boxes.length, End: boxes.length }[e.key];
      if (move !== undefined) {
        e.preventDefault();
        boxes[Math.max(0, Math.min(boxes.length - 1, index + move))]?.focus();
      } else if (e.key === 'Enter') {
        e.preventDefault();
        e.stopPropagation();
        this.commit();
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
 * columns: [{ id, name, label, alternate, clrType, clrLine, type, category, nullable, pk, fk, fkModel, numeric, sortable, pinned,
 *   options, unknownText, … }] in display order (GridColumn in .NET).
 * options (GridOptions in .NET):
 *   sorts: [{ colId, sort }] initial sort state.
 *   firstRow: rough scroll position to restore (0 = top); the block containing it is loaded on demand.
 *   editable: whether the workspace may write (v2); the columns say whether they can be edited at all.
 *   headerHeight: with the line for C# names (WP-12); null for the theme default.
 *   table: a table tab – header menu, Del, pinning and scroll position are reported to .NET. A query result only
 *     answers GetRows, OnCopy, OnCopyColumn (WP-57) and OnCellContextMenu, and what the options below ask for.
 *   form: the grid reports its focus (OnFocused) and the form key (OnFormKey) – always for a table, for a query result since
 *     WP-56.
 *   headerMenu: a query result reports a right click on a column header too (OnHeaderContextMenu, SQL editor, WP-57).
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
    },
    // While editing, Enter belongs to the editor (check in .NET first, Shift+Enter: new line in the multi-line box).
    // The member list keeps the arrow keys while editing.
    suppressKeyboardEvent: p => p.editing && (p.event.key === 'Enter' || (!!meta.options && listKeys.has(p.event.key))),
    tooltip: p => (p.value === null || p.value === undefined ? null
      : [p.value, p.data?.__n?.[meta.id], p.data?.__nm?.includes(meta.id) ? texts.notFoundTitle : null, p.data?.__t?.[meta.id],
        p.data?.__u?.includes(meta.id) ? meta.unknownText : null].filter(Boolean).join('\n')),
    headerComponent: FerretHeader,
    // A function, not the object: AG Grid deep-copies plain objects of the column def, and updateColumns changes meta in place.
    headerComponentParams: { getMeta: () => meta, texts },
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
  element.addEventListener('keydown', e => onDeleteKey(e) || onCopyKey(e) || onCopyColumnKey(e) || onFormKey(e) || onLobKey(e) || onEditHistoryKey(e)
    || onSelectKey(e) || onAltGrKey(e), true);

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
    focusedRow = pinned ? null : e.rowIndex;
    reportFocus(e);
  }
  focusControls.set(elementId, {
    forgetKey: () => { keyMove = null; },
    forgetSpan: () => { span = null; },
    select: (rowIndex, pinned) => followFocus(api, rowIndex, pinned, 'single'),
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
    node.setSelected(!node.isSelected());
    anchor = cell.rowIndex;
    span = null;
    return true;
  }

  // Del (not while editing): mark the selected rows for deletion – pending until written, can be reverted. .NET decides
  // between the focused new row and the selected loaded rows.
  function onDeleteKey(e) {
    if (!table || e.key !== 'Delete' || e.ctrlKey || e.altKey || api.getEditingCells().length > 0) return false;
    const loaded = selectedRows();
    const newId = newIdOf(api, api.getFocusedCell());
    if (loaded.length > 0 || newId) {
      e.preventDefault();
      dotnet.invokeMethodAsync('OnDeleteKey', loaded, newId).then(updates => applyUpdates(api, updates)).catch(callFailed);
    }
    return true;
  }

  // Ctrl+C: the focused cell, or the selected rows if there are several (AG Grid Community has no clipboard).
  // Text marked with the mouse inside one cell is copied by the browser as usual.
  function onCopyKey(e) {
    if (!(e.ctrlKey || e.metaKey) || e.shiftKey || e.altKey || e.key.toLowerCase() !== 'c') return false;
    if (textMarkedInOneCell()) return true;
    const cell = api.getFocusedCell();
    if (!cell || cell.rowIndex == null || cell.rowPinned) return true;
    e.preventDefault();
    dotnet.invokeMethodAsync('OnCopy', cell.rowIndex, cell.column.getColId(), selectedRows()).catch(callFailed);
    return true;
  }

  // The values of the focused cell's column from the selected rows (WP-57): no default key, the user gives it one. In both
  // grids; not while a cell is edited.
  function onCopyColumnKey(e) {
    const key = localCombo('copyColumn');
    if (!key || comboOf(e) !== key || api.getEditingCells().length > 0) return false;
    e.preventDefault();
    e.stopPropagation();
    const cell = api.getFocusedCell();
    if (cell && cell.rowIndex != null && !cell.rowPinned) {
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

  // How many rows are selected and whether rows still to load join them (span to the end), for the footer; Shift+↓ held
  // down changes it fast, so only the last state within a moment is reported.
  let selectionTimer = null;
  function reportSelection() {
    clearTimeout(selectionTimer);
    selectionTimer = setTimeout(() => {
      if (grids.get(elementId) !== api) return; // destroyed or created anew meanwhile
      dotnet.invokeMethodAsync('OnSelectionChanged', api.getSelectedNodes().length, span?.to === Infinity).catch(callFailed);
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

  // A character typed with AltGr (@ € \ { } on a German keyboard) starts editing like any other: AG Grid takes Ctrl+Alt
  // for a shortcut. Only on a focused cell (not the header) that can be edited; LOB cells open their editor with Enter.
  function onAltGrKey(e) {
    if (!isAltGrChar(e) || !e.target.closest?.('.ag-cell') || api.getEditingCells().length > 0) return false;
    const cell = api.getFocusedCell();
    if (!cell || cell.rowIndex == null || metaById.get(cell.column.getColId())?.lob) return false;
    const node = cell.rowPinned ? api.getPinnedTopRow(cell.rowIndex) : api.getDisplayedRowAtIndex(cell.rowIndex);
    if (!node || !cell.column.isCellEditable(node)) return false;
    e.preventDefault();
    e.stopPropagation();
    api.startEditingCell({ rowIndex: cell.rowIndex, colKey: cell.column.getColId(), rowPinned: cell.rowPinned ?? undefined, key: e.key });
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
      if (!table) return;
      const sortModel = e.api.getColumnState()
        .filter(s => s.sort)
        .sort((a, b) => (a.sortIndex ?? 0) - (b.sortIndex ?? 0))
        .map(s => ({ colId: s.colId, sort: s.sort }));
      dotnet.invokeMethodAsync('OnSortChanged', sortModel).catch(callFailed);
    },
    onSelectionChanged: reportSelection,
    // Pinned or reordered within the pinned area by dragging (changes from setPinned have source 'api').
    onColumnPinned: e => { if (table && e.source?.startsWith('ui')) reportPinned(api, dotnet); },
    onColumnMoved: e => { if (table && e.finished && e.source?.startsWith('ui') && e.column?.getPinned()) reportPinned(api, dotnet); },
    // The menu itself is a Blazor component; the grid only reports where the user right-clicked.
    // Right-click on a selected row keeps the selection (export of several rows), otherwise selects just this row.
    onCellContextMenu: e => {
      if (e.rowIndex == null || !e.data || !e.event || e.node.rowPinned) return; // new rows: Del removes them
      if (!e.node.isSelected()) {
        e.node.setSelected(true, true);
        anchor = e.rowIndex;
        span = null;
      }
      const selected = api.getSelectedNodes().map(n => n.rowIndex).filter(i => i != null);
      dotnet.invokeMethodAsync('OnCellContextMenu', e.rowIndex, e.column.getColId(), selected,
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
 * The C# model arrived or the name setting changed (WP-12): new labels, badges, member lists and header height, without
 * rebuilding the grid (widths, order, sorting and scroll position stay). The rows are reloaded by .NET afterwards.
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
