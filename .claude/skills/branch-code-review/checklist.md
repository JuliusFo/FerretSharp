# Review checklist (FerretSharp)

Shared by `branch-code-review` and `complete-code-review`. Hand this file to every reviewer agent; it lists what is specific to this project. General review craft (logic errors, edge cases, resource leaks) applies on top.

## Severity

- **S** wrong data, data loss, security hole, hang
- **M** wrong behaviour in realistic cases, or a clear maintenance burden
- **K** minor

## Correctness

- Async: every DB call takes the `CancellationToken`; nothing blocks on `.Result`; a component or state object may be disposed while an await is pending (check after the await).
- Concurrency: one session per workspace, writes serialized; Blazor render vs. background continuations; events unsubscribed on dispose.
- Silent VPN (`CLAUDE.md` §7): what happens when a call hangs or the connection drops mid-way? Can the user still cancel, disconnect, close the tab, quit the app?
- Oracle semantics: NULL in comparisons and NOT, CHAR padding, NUMBER precision beyond 15 digits, DATE vs. TIMESTAMP (TZ), empty string = NULL, quoted mixed-case identifiers, RAW keys.
- Transactions: pending → flushed → committed; savepoints, undo stack shared by grid and SQL/LINQ (ADR 0018); DDL only without an open transaction (ADR 0019).

## Invariants (`CLAUDE.md` §2 and §4)

- Read, write and schema paths stay separate; the read guard is never weakened. A new path gets its own narrow method, an ADR and tests.
- SQL text only in `Core/Query/` and `Core/Oracle/`, DDL only in `Core/Ddl/`. Values as binds (each bind used in the SQL), identifiers validated against the schema and quoted, deterministic order (PK → ROWID).
- No `System.Windows` or Windows-only APIs in Core and UI; host-only features behind interfaces implemented in `FerretSharp.App`.
- Schema records stay the DB view; C# names, CLR types and enums go through `ClrModelMapping`; cell values and captions through the presentation layer.
- Filter model stays translatable 1:1 to a LINQ `Where`.
- Prod: no bind values or text literals in the log, no bind values in the SQL history, passwords nowhere (also not in error dialogs, `SqlRedaction`).
- Persistence: write via `AtomicJsonFile`, read via `VersionedJsonFile`; no `object` properties.
- Recursive delete only via `SafeDelete.DirectoryBelow` / `TestFolder`.
- No business logic in JS; one ES module per topic. No banned libraries (`CLAUDE.md` §3). Excel export writes text, never formulas.

## Security: follow outside input to where it lands

A fixed pass, not a general impression: for every source the change touches, trace the value to every sink it reaches. The rules already named (`CLAUDE.md` §2/§6, `.claude/rules/links.md`, `oracle.md`, `connection-transfer.md`) hold the details; check against them.

Sources (untrusted):
- Files from others: model files, imported connection files (FerretSharp, DBeaver, SQL Developer), audit templates, display columns, filter presets, workspace and settings files edited by hand.
- Links `ferretsharp://` and the redirect page (`pages/`).
- Pasted text: filters, SQL, LINQ, IN lists.
- Data from the database: values, comments, quoted names.
- The user's project: Git files in work clones (`GitFolder`), build output, the ModelHost.

Sinks (never unchecked):
- Generated code: names from outside files in C# identifiers, type names, `//` comments (line breaks), string literals – pattern: `ModelFileCheck` (WP-64).
- SQL: binds only, names validated against the schema and quoted; read, write and schema paths never weakened (ADR 0006/0019/0021).
- WebView: no `innerHTML` or `MarkupString` with outside text; `textContent`, `title` as a property; JS interop arguments as data.
- Paths: `..`, absolute paths, UNC shares (login to a foreign host, reviews 3.31.0 A4 and 3.44.0 A31), refs or names joined into paths; deleting only via `SafeDelete`.
- Processes: only via `DotNetCli`, no arguments from outside data; ModelHost and LINQ console run project code, so only for a project the user chose.
- Size and load: limits for files and lists, no unbounded loops (watchers, retries).
- Log, history, error dialog, export, trace: no values or passwords on Prod (`SqlRedaction`), never a connection string.

A security finding names the concrete path from source to sink, the check at the boundary (for exports also on the sending side) and a test that fails without the fix. Once fixed, it gets its line in the package protocol like every finding (see "Review" in `docs/work-packages/wp-64-model-file.md`).

## UI

- Every visible text in English and German (typed resx, `ResourceTests`); no hard-coded strings in markup.
- Colours and spacing via the tokens in `ferretsharp.css`; no hard-coded colours.
- JS modules disposed; components check after `import` whether they are already disposed.
- Keyboard shortcuts and dialogs (`.modal-backdrop`) behave as described in `docs/architecture.md`.

## Tests

- Core logic has unit tests without a DB; DB-dependent tests live in `FerretSharp.Integration.Tests` and skip cleanly without Docker.
- A bug fix comes with a test that fails without it, where feasible.
- Rules in `.claude/rules/tests.md` (UI culture, number formats, `TestFolder`).

## Docs

- `CHANGELOG.md`, work-package protocol plus index line, ADR for non-trivial decisions, `docs/architecture.md` and `.claude/rules/` still true after the change.
- No names or structure from the user's customer database in code, tests, docs or commits; neutral examples only.

## Not a finding

- Decisions the user already made: "Entscheidung des Nutzers" in `docs/work-packages/`, `docs/review-*.md`, ADRs. In particular no extra Prod guards on deliberate user actions (copy, export) – the invariants above stay.
- Items already in `docs/backlog.md` or `docs/roadmap.md`, unless the change makes them worse.
- Style preferences without a concrete cost.
