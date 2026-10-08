// Bridge to AG Grid Community (global `agGrid`, loaded by index.html). Infinite row model: every block of rows,
// including sorting, comes from .NET; cells arrive as display strings (null = SQL NULL). No business logic here.

import { comboOf, localCombo } from './shortcuts.js';

const grids = new Map();
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
      pinnedColumnBorder: { style: 'solid', width: 2, color: dark ? '#4a4b56' : '#c4c4ce' },
      accentColor: dark ? '#7aa2ff' : '#3b6fe0',
      backgroundColor: dark ? '#1c1d22' : '#ffffff',
      foregroundColor: dark ? '#e6e6ea' : '#1d1d22',
      headerBackgroundColor: dark ? '#202127' : '#f9f9fb',
      oddRowBackgroundColor: dark ? '#1f2025' : '#fbfbfc',
      borderColor: dark ? '#2c2d34' : '#ececf0',
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
    else if (meta.fkModel) badges.insertAdjacentHTML('beforeend', '<span class="fs-badge fk model" title="Beziehung aus dem C#-Modell, ohne Constraint in der Datenbank">FK</span>');
    this.sortEl = this.eGui.querySelector('.fs-h-sort');
    if (meta.sortable) {
      this.eGui.addEventListener('click', e => params.progressSort(e.shiftKey));
      this.onSortChanged = () => this.updateSort();
      params.column.addEventListener('sortChanged', this.onSortChanged);
      this.updateSort();
    }
    // Column comment (ALL_COL_COMMENTS) as tooltip.
    this.eGui.title = [meta.alternate ? `${meta.label} · ${meta.alternate}` : null, meta.comment,
      meta.sortable ? null : 'Nach diesem Typ kann nicht sortiert werden'].filter(Boolean).join('\n');
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
        .catch(() => { this.input.disabled = false; });
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
      const error = await params.state.dotnet.invokeMethodAsync('ValidateEdit',
        ...rowArgs(params.node, params.data), params.column.getColId(), this.input.value);
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
  isPopup() { return !!this.params.meta.multiline; }
}

/**
 * Cell editor for enum and converted bool columns (WP-12): a list of the members; the value is the database value, so
 * .NET checks and writes it like typed text. Picking with the mouse takes the value at once; with the keyboard Enter
 * does (arrow keys on the closed list already change the selection).
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
    const clear = params.eventKey === 'Backspace' || params.eventKey === 'Delete';
    params.state.dotnet.invokeMethodAsync('GetEditText', ...rowArgs(params.node, params.data), params.column.getColId())
      .then(text => {
        const value = clear ? '' : text ?? '';
        // A value without a member stays selectable, so leaving the editor does not change it.
        if (![...this.select.options].some(o => o.value === value)) this.addOption(value, `${value} (kein Member)`);
        this.select.value = value;
        this.select.disabled = false;
        this.loaded = true;
        this.select.focus();
      })
      .catch(() => { this.select.disabled = false; });
  }
  addOption(value, label) {
    const option = document.createElement('option');
    option.value = value;
    option.textContent = label;
    this.select.appendChild(option);
  }
  async commit() {
    const params = this.params;
    const error = await params.state.dotnet.invokeMethodAsync('ValidateEdit',
      ...rowArgs(params.node, params.data), params.column.getColId(), this.select.value);
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

/** Identifies a cell across redraws: new rows by their id, loaded rows by index. */
function cellKey(node, colId) {
  return (node.data?.__new ?? `r${node.rowIndex}`) + '|' + colId;
}

function renderCell(params) {
  if (!params.data) return '';
  const value = params.value;
  if (value === null || value === undefined) {
    const span = document.createElement('span');
    span.className = 'fs-null';
    span.textContent = 'NULL';
    return span;
  }
  return value;
}

function width(meta) {
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
 *     answers GetRows, OnCopy and OnCellContextMenu.
 */
export function create(elementId, dotnet, columns, options) {
  const { sorts, firstRow, editable, headerHeight, table } = options;
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
  const editState = { dotnet, retry: null, enabled: !!editable };
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
    cellRenderer: renderCell,
    // Editing (v2): new rows (pinned at the top) may fill the key; loaded rows not deleted and not too large a number.
    editable: p => !editState.enabled ? false : p.node.rowPinned === 'top'
      ? meta.editableNew
      : meta.editable && !!p.data && !p.data.__d && !(p.data.__ro ?? []).includes(meta.id),
    // Enum and bool columns of a C# model pick from their members (the model may arrive after the grid was built).
    cellEditorSelector: () => ({ component: meta.options ? FerretSelectEditor : FerretCellEditor, params: { meta, state: editState } }),
    cellClassRules: {
      'fs-unknown': p => !!p.data?.__u?.includes(meta.id),
      'fs-pending': p => p.data?.__s?.[meta.id] === 'p',
      'fs-flushed': p => p.data?.__s?.[meta.id] === 'f',
    },
    // While editing, Enter belongs to the editor (check in .NET first, Shift+Enter: new line in the multi-line box).
    // The member list keeps the arrow keys while editing.
    suppressKeyboardEvent: p => p.editing && (p.event.key === 'Enter' || (!!meta.options && listKeys.has(p.event.key))),
    tooltipValueGetter: p => (p.value === null || p.value === undefined ? null
      : p.data?.__u?.includes(meta.id) ? `${p.value}\n${meta.unknownText}`
      : p.data?.__t?.[meta.id] ? `${p.value}\n${p.data.__t[meta.id]}` : p.value),
    headerComponent: FerretHeader,
    // A function, not the object: AG Grid deep-copies plain objects of the column def, and updateColumns changes meta in place.
    headerComponentParams: { getMeta: () => meta },
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
    if (colId && table) {
      dotnet.invokeMethodAsync('OnHeaderContextMenu', colId, e.clientX, e.clientY, window.innerWidth, window.innerHeight).catch(callFailed);
    }
  });

  // The grid's keys, in the capture phase (before AG Grid): each handler says whether the key was its.
  element.addEventListener('keydown', e => onDeleteKey(e) || onCopyKey(e) || onFormKey(e) || onLobKey(e), true);

  const selectedRows = () => api.getSelectedNodes().map(n => n.rowIndex).filter(i => i != null);

  // Del (not while editing): mark the selected rows for deletion – pending until written, can be reverted.
  function onDeleteKey(e) {
    if (!table || e.key !== 'Delete' || e.ctrlKey || e.altKey || api.getEditingCells().length > 0) return false;
    const loaded = selectedRows();
    const pinned = [newIdOf(api, api.getFocusedCell())].filter(Boolean);
    if (loaded.length > 0 || pinned.length > 0) {
      e.preventDefault();
      dotnet.invokeMethodAsync('OnDeleteKey', loaded, pinned).then(updates => applyUpdates(api, updates)).catch(callFailed);
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

  // Shift+click selects a range of rows; without this the browser would also mark text across the cells.
  element.addEventListener('mousedown', e => { if (e.shiftKey) e.preventDefault(); }, true);

  // Double click starts editing. The WPF host passes the second mouse-down of a real double click twice, so the
  // browser never raises dblclick (src/FerretSharp.UI/CLAUDE.md) – AG Grid's own double-click editing would not react.
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
    if (!table || !formKey || comboOf(e) !== formKey || api.getEditingCells().length > 0) return false;
    e.preventDefault();
    e.stopPropagation();
    const cell = api.getFocusedCell();
    dotnet.invokeMethodAsync('OnFormKey', cell && !cell.rowPinned ? cell.rowIndex : -1, newIdOf(api, cell), selectedRows()).catch(callFailed);
    return true;
  }

  // The form follows the focused cell; arrow keys move it fast, so only the last position within a moment is reported.
  let focusTimer = null;
  function reportFocus(e) {
    if (!table || e.rowIndex == null) return;
    // e.api: the event may come while createGrid still runs (before `api` is assigned).
    const newId = newIdOf(e.api, e);
    if (e.rowPinned && !newId) return;
    const colId = typeof e.column === 'string' ? e.column : e.column?.getColId?.() ?? null;
    clearTimeout(focusTimer);
    focusTimer = setTimeout(() => dotnet.invokeMethodAsync('OnFocused', e.rowPinned ? -1 : e.rowIndex, newId ?? null, colId).catch(callFailed), 60);
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
    onCellEditRequest: async e => {
      const colId = e.column.getColId();
      const text = e.newValue ?? '';
      try {
        const result = await dotnet.invokeMethodAsync('OnCellEdit', ...rowArgs(e.node, e.data), colId, text);
        if (result.error) {
          // Left with Tab or a click elsewhere: reopen the editor with the rejected text and the reason.
          editState.retry = { key: cellKey(e.node, colId), text, error: result.error };
          setTimeout(() => {
            api.stopEditing(true);
            api.startEditingCell({ rowIndex: e.node.rowIndex, colKey: colId, rowPinned: e.node.rowPinned ?? undefined });
          });
        } else if (result.row) {
          e.node.setData(result.row);
        }
      } catch (err) {
        console.error(err);
      }
    },
    onBodyScrollEnd: () => { if (table) dotnet.invokeMethodAsync('OnScrolled', firstVisibleRow(api)).catch(callFailed); },
    onCellFocused: reportFocus,
    // Pinned or reordered within the pinned area by dragging (changes from setPinned have source 'api').
    onColumnPinned: e => { if (table && e.source?.startsWith('ui')) reportPinned(api, dotnet); },
    onColumnMoved: e => { if (table && e.finished && e.source?.startsWith('ui') && e.column?.getPinned()) reportPinned(api, dotnet); },
    // The menu itself is a Blazor component; the grid only reports where the user right-clicked.
    // Right-click on a selected row keeps the selection (export of several rows), otherwise selects just this row.
    onCellContextMenu: e => {
      if (e.rowIndex == null || !e.data || !e.event || e.node.rowPinned) return; // new rows: Del removes them
      if (!e.node.isSelected()) e.node.setSelected(true, true);
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

/** Starts editing a cell; <paramref name="pinned"/>: index among the new rows at the top. */
export function startEditing(elementId, rowIndex, colId, pinned) {
  const api = grids.get(elementId);
  if (!api) return;
  api.ensureColumnVisible(colId);
  api.setFocusedCell(rowIndex, colId, pinned ? 'top' : undefined);
  api.startEditingCell({ rowIndex, colKey: colId, rowPinned: pinned ? 'top' : undefined });
}

/**
 * Moves the focused cell to a row (the form's ▲ ▼, WP-21); a block not loaded yet is fetched. colId null keeps the
 * focused column. pinned: rowIndex counts the new rows at the top. keepFocus: the keyboard focus stays where it was
 * (in the form), the grid only shows the row.
 */
export function focusRow(elementId, rowIndex, colId, pinned, keepFocus) {
  const api = grids.get(elementId);
  if (!api) return;
  const column = colId ?? api.getFocusedCell()?.column.getColId() ?? api.getAllDisplayedColumns()[0]?.getColId();
  if (!column) return;
  const active = document.activeElement;
  if (!pinned) api.ensureIndexVisible(rowIndex);
  api.ensureColumnVisible(column);
  api.setFocusedCell(rowIndex, column, pinned ? 'top' : undefined);
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
  api.ensureIndexVisible(0, 'top');
  api.purgeInfiniteCache();
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
  metas.delete(elementId);
  headerHeights.delete(elementId);
  editStates.delete(elementId);
}

/**
 * The C# model arrived or the name setting changed (WP-12): new labels, badges, member lists and header height, without
 * rebuilding the grid (widths, order, sorting and scroll position stay). The rows are reloaded by .NET afterwards.
 */
export function updateColumns(elementId, columns, headerHeight) {
  const api = grids.get(elementId);
  const byId = metas.get(elementId);
  if (!api || !byId) return;
  api.stopEditing(true);
  for (const meta of columns) {
    const current = byId.get(meta.id);
    if (current) Object.assign(current, meta);
  }
  headerHeights.set(elementId, headerHeight);
  api.setGridOption('theme', theme(headerHeight));
  api.refreshHeader();
}
