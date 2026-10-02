// Bridge to AG Grid Community (global `agGrid`, loaded by index.html). Infinite row model: every block of rows,
// including sorting, comes from .NET; cells arrive as display strings (null = SQL NULL). No business logic here.

const grids = new Map();
const media = window.matchMedia('(prefers-color-scheme: dark)');
let modulesRegistered = false;

function registerModules() {
  if (!modulesRegistered && agGrid.ModuleRegistry && agGrid.AllCommunityModule) {
    agGrid.ModuleRegistry.registerModules([agGrid.AllCommunityModule]);
  }
  modulesRegistered = true;
}

function theme() {
  const dark = media.matches;
  return agGrid.themeQuartz
    .withPart(dark ? agGrid.colorSchemeDark : agGrid.colorSchemeLight)
    .withParams({
      fontFamily: '"Segoe UI Variable Text", "Segoe UI", system-ui, sans-serif',
      fontSize: 13,
      headerFontSize: 12,
      headerFontWeight: 600,
      rowHeight: ROW_HEIGHT,
      headerHeight: 46,
      spacing: 6,
      wrapperBorder: false,
      wrapperBorderRadius: 0,
      columnBorder: true,
      accentColor: dark ? '#7aa2ff' : '#3b6fe0',
      backgroundColor: dark ? '#1c1d22' : '#ffffff',
      foregroundColor: dark ? '#e6e6ea' : '#1d1d22',
      headerBackgroundColor: dark ? '#202127' : '#f9f9fb',
      oddRowBackgroundColor: dark ? '#1f2025' : '#fbfbfc',
      borderColor: dark ? '#2c2d34' : '#ececf0',
    });
}

media.addEventListener('change', () => grids.forEach(api => api.setGridOption('theme', theme())));

/** Header: name, Oracle type, NOT NULL, PK/FK badges, sort arrow. */
class FerretHeader {
  init(params) {
    this.params = params;
    const meta = params.meta;
    this.eGui = document.createElement('div');
    this.eGui.className = 'fs-header' + (meta.numeric ? ' num' : '');
    this.eGui.innerHTML =
      '<div class="fs-h-top"><span class="fs-h-name"></span><span class="fs-h-badges"></span><span class="fs-h-sort"></span></div>' +
      '<div class="fs-h-type"></div>';
    this.eGui.querySelector('.fs-h-name').textContent = meta.name;
    this.eGui.querySelector('.fs-h-type').textContent = meta.type + (meta.nullable ? '' : ' · NOT NULL');
    const badges = this.eGui.querySelector('.fs-h-badges');
    if (meta.pk) badges.insertAdjacentHTML('beforeend', '<span class="fs-badge pk">PK</span>');
    if (meta.fk) badges.insertAdjacentHTML('beforeend', '<span class="fs-badge fk">FK</span>');
    this.sortEl = this.eGui.querySelector('.fs-h-sort');
    if (meta.sortable) {
      this.eGui.addEventListener('click', e => params.progressSort(e.shiftKey));
      this.onSortChanged = () => this.updateSort();
      params.column.addEventListener('sortChanged', this.onSortChanged);
      this.updateSort();
    } else {
      this.eGui.title = 'Nach diesem Typ kann nicht sortiert werden';
    }
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

const ROW_HEIGHT = 30;
const BLOCK_SIZE = 500;

/** First row at the top of the viewport (getFirstDisplayedRowIndex would include the render buffer). */
function firstVisibleRow(api) {
  return Math.floor(api.getVerticalPixelRange().top / ROW_HEIGHT);
}

/**
 * columns: [{ id, name, type, category, nullable, pk, fk, numeric, sortable }]
 * sorts: [{ colId, sort }] initial sort state.
 * firstRow: rough scroll position to restore (0 = top); the block containing it is loaded on demand.
 */
export function create(elementId, dotnet, columns, sorts, firstRow) {
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
  const columnDefs = columns.map(meta => ({
    colId: meta.id,
    field: meta.id,
    headerName: meta.name,
    width: width(meta),
    sortable: meta.sortable,
    sort: sortById.get(meta.id)?.sort ?? null,
    sortIndex: sortById.get(meta.id)?.index ?? null,
    cellClass: meta.numeric ? 'fs-num' : meta.category === 'Date' || meta.category.startsWith('Timestamp') ? 'fs-date' : undefined,
    cellRenderer: renderCell,
    tooltipValueGetter: p => (p.value === null || p.value === undefined ? null : p.value),
    headerComponent: FerretHeader,
    headerComponentParams: { meta },
    pinned: meta.pk ? 'left' : undefined,
  }));

  const element = document.getElementById(elementId);
  element.addEventListener('contextmenu', e => e.preventDefault()); // no WebView menu (Back, Reload, Inspect)

  const api = agGrid.createGrid(element, {
    theme: theme(),
    columnDefs,
    defaultColDef: { resizable: true, minWidth: 70 },
    rowModelType: 'infinite',
    cacheBlockSize: BLOCK_SIZE,
    maxBlocksInCache: 40,
    maxConcurrentDatasourceRequests: 1,
    infiniteInitialRowCount: 1,
    rowSelection: { mode: 'singleRow', checkboxes: false, enableClickSelection: true },
    enableCellTextSelection: true,
    ensureDomOrder: true,
    tooltipShowDelay: 700,
    suppressMultiSort: false,
    animateRows: false,
    onBodyScrollEnd: () => dotnet.invokeMethodAsync('OnScrolled', firstVisibleRow(api)).catch(() => {}),
    // The menu itself is a Blazor component; the grid only reports where the user right-clicked.
    onCellContextMenu: e => {
      if (e.rowIndex == null || !e.data || !e.event) return;
      e.node.setSelected(true);
      dotnet.invokeMethodAsync('OnCellContextMenu', e.rowIndex, e.column.getColId(),
        e.event.clientX, e.event.clientY, window.innerWidth, window.innerHeight).catch(() => {});
    },
    datasource: {
      getRows: async params => {
        try {
          const sortModel = params.sortModel.map(s => ({ colId: s.colId, sort: s.sort }));
          const page = await dotnet.invokeMethodAsync('GetRows', params.startRow, params.endRow, sortModel);
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

/** Drops all cached blocks and reloads from the first row (filters changed, F5). */
export function refresh(elementId) {
  const api = grids.get(elementId);
  if (!api) return;
  api.ensureIndexVisible(0, 'top');
  api.purgeInfiniteCache();
}

/** Known total (after COUNT): the scrollbar then reflects the full table. */
export function setRowCount(elementId, count) {
  grids.get(elementId)?.setRowCount(count, true);
}

export function destroy(elementId) {
  grids.get(elementId)?.destroy();
  grids.delete(elementId);
}
