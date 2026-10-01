# Changelog

All notable changes to this project are documented here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), versions follow [SemVer](https://semver.org/).

## [Unreleased]

### Added
- Solution scaffold (`FerretSharp.slnx`, .NET 10, central package management).
- Blazor Hybrid shell: thin WPF host with `BlazorWebView`, platform-neutral Razor UI library (`FerretSharp.UI`) with design tokens for light/dark, native title bar following the theme, `--theme=dark|light` override.
- Generic host with DI and Serilog file logging (`%APPDATA%\FerretSharp\logs`), global exception handlers.
- Unit test project and Oracle integration test project (Testcontainers, skipped without Docker).
- Connections: add/edit/delete Oracle connection profiles (host/port with service name or SID, or TNS alias with `TNS_ADMIN`), environment kind with color coding, read-only default for Prod, connection test showing duration, server version and ORA error codes.
- Connection groups (e.g. project/customer), connection switcher popover with search, collapsible groups and keyboard navigation (`Ctrl+Shift+O`), and a connections overview page with edit, duplicate (reuses the original's password) and delete with confirmation.
- Passwords are stored in the Windows Credential Manager, never in `connections.json`.
- Read-only `OracleSession` (pooling off, `V$SESSION` module/action/client info, serialized and cancellable queries).
- `--data-dir=<path>` redirects connections and logs to another folder.
- Connect/disconnect with progress, cancellation and an error view showing the ORA code; recently used connections in the switcher and on the overview page; red frame while a Prod connection is active.
- Schema explorer: tables, views and materialized views of the connection's schema with search and A–Z letter index; object list and foreign keys load on connect, column details lazily.
- Table structure view: columns with types, defaults, identity and PK/UK/FK markers, keys, row key, incoming and outgoing foreign keys with navigation.
- Data grid (AG Grid Community 34.3.1, vendored): tables open as tabs, rows load in blocks of 500 while scrolling, server-side sorting (multi-sort with Shift), German formatting, explicit NULL, CLOB preview, exact NUMBER(38), placeholders for unsupported types.
- TablePlus-style filter bar: per-column operators, German/ISO value parsing with validation, case-insensitive contains with literal `%`/`_`, whole-day date semantics, IN lists, enable/disable per row, `Ctrl+Enter` to apply, SQL preview with bind variables.
- Row count on demand (cancellable), `F5` reloads the grid.
- View and materialized view definitions (highlighted SQL) in the structure view.
- Synonyms: tables, views and materialized views of other schemas reached through private or public synonyms appear in the explorer (tag "SYN") and open like own objects; foreign keys of those schemas are loaded too.

### Fixed
- Sorting followed the Windows locale (`NLS_SORT=GERMAN`); sessions now sort and compare binary.
