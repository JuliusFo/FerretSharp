# Agent briefs (complete-code-review)

Each agent gets its block below verbatim, in addition to what `SKILL.md` step 2 lists for every prompt. The blocks say what
to check at least – an agent may go further within its topic. "Leave to" names the agent that owns a neighbouring
question, so a finding is reported once.

Common to all: read the files in question, never guess from names. A finding needs a failure scenario someone could
reproduce (input, steps, wrong result), not "could be a problem". Check the "Ohne Befund geprüft" list of the last review
and say whether it still holds for your topic.

## DB – DB access

- The three paths (`ExecuteReaderAsync`, `ExecuteNonQueryAsync`, `ExecuteDdlAsync`) and every new way to run SQL since the
  last review: does it go through one of them, or does it get its own narrow method with ADR and tests (`CLAUDE.md` §2)?
- `IsReadOnlyStatement`, `IsWriteStatement`, `IsDdlStatement`: try to slip past them (comments, `WITH … DELETE`, two
  statements, `FOR UPDATE` in odd spacing, `TRUNCATE` look-alikes from `ddl.md`).
- SQL text only in `Core/Query` and `Core/Oracle`, DDL only in `Core/Ddl`; values only as binds, every bind used in the SQL;
  column names validated against the schema and quoted; deterministic order (PK → ROWID).
- Transactions: flush, savepoints, commit, rollback, undo stack (ADR 0018); DDL never with an open transaction.
- Oracle types: NUMBER precision, DATE/TIMESTAMP (TZ), LOBs, RAW, intervals, NaN/Inf – read, display, edit and back.
- Cancellation: every DB call takes the `CancellationToken`; a cancelled read leaves the session usable.
- Leave to: ST (who calls when), SE (where the input comes from).

## ST – State and concurrency

- Lifetimes: workspaces, tabs, connection scopes (`ConnectionHub`), dispose order, events unsubscribed, `_disposed` checks
  after every `await` in components with JS interop.
- Races: two actions on one workspace session (gate), quick double clicks, a save while connecting, background work that
  outlives its tab or connection.
- Silent VPN: what hangs without a timeout, what retries, what the user sees after 5 s (`RunProgress`, "Session trennen").
- `Fire`/`GuardAsync` instead of `_ = …`; no `OperationCanceledException` escaping to the WPF dispatcher.
- Persistence: `AtomicJsonFile`/`VersionedJsonFile` for every JSON file, unreadable or newer files never overwritten.
- Leave to: DB (SQL correctness), UI (rendering).

## UI – UI, JS, CSS

- Components follow `.claude/rules/ui*.md`: cascading scoped services (never `@inject`), `ModalFrame`/`PopoverMenu`/
  `MenuButton`, `SearchBox`, `title` tooltips, icons only from `Icons.cs`, shortcuts never as fixed text.
- WebView2 host traps from `ui.md` (no `@ondblclick`, no `@onauxclick`, mouse capture, `<select>` value). A drag or click
  path that was only tested via CDP is a finding with "mit echter Maus prüfen".
- JS: one ES module per topic via `JsModule`, no business logic in JS, listeners removed on destroy.
- Every visible text in en and de (typed resx), no hard-coded strings in markup; German texts read naturally.
- CSS: design tokens only, dark and light, no dead classes; render cost (components re-rendering on every
  `ShellState.Changed`).
- Leave to: ST (lifetimes), SE (XSS, `MarkupString`).

## PH – Host, processes, build

- App host: `MainWindow`, `WindowTheme`, Velopack start order, instance lock, link pipe, editor launcher.
- ModelHost and AppTrace: process start (`ArgumentList`, no shell), timeouts, what happens when the child hangs or dies.
- Build: `Directory.Build.props`, central package versions (outdated or vulnerable packages: `dotnet list package
  --vulnerable --include-transitive` only reads, no restore changes), project references against the layer rules
  (Core/UI without Windows APIs).
- CI and release workflows: version check, permissions of the token, pinned actions, what runs on Linux vs Windows.
- Leave to: SE (input from outside into processes), DO (`docs/release.md`).

## SE – Security

- Follow the checklist's "Security" section (`branch-code-review/checklist.md`) for every source listed there, each traced
  to every sink; name source, boundary check and the test that fails without it.
- Redaction: Prod logs and SQL history without binds and literals as `CLAUDE.md` §2 says, passwords nowhere
  (`SqlRedaction`, error dialog, AppTrace).
- Paths: every path from settings, files, links or the model checked before use; recursive deletes only via `SafeDelete`.
- Files from others (connection imports, model files, workspace files, links): size limits, unknown versions, polymorphic
  JSON.
- Leave to: nobody – a security question is always reported here, also when it touches another topic.

## RE – Reuse and dead code

- Duplicates and copies that drift apart (the same rule in two places, one of them fixed), helpers that exist but are not
  used.
- Dead code: unused types, members, resx keys, CSS classes, JS exports, `[JSInvokable]`; settings without effect.
- Readability: methods too long to follow, names that no longer match what the code does, comments that are wrong.
- Structure: files in the wrong folder (R6b), types that belong in another layer.
- Leave to: TE (duplicate tests), DO (docs that drift).

## TE – Tests

Only TE builds and runs the tests. Run `dotnet build` and `dotnet test` on the snapshot once at the start; with Docker
available also `tools/coverage/Get-Coverage.ps1` (writes only under `artifacts/`).

**Gaps**
- Every fix since the last review (protocols in `docs/work-packages/`): is there a test that fails without it? Name the
  fixes without one.
- Coverage report: untested branches in `Core` and `UI/State` that decide something (guards, retries, cancellation,
  rename/merge, redaction). Not a finding: a line count, generated code, the WPF host.
- Invariant tests still cover all paths: `ReadOnlyTests` (every read path, also new ones), `SafeDeleteTests`,
  `ArchitectureTests`, `ResourceTests`, `ConnectionServiceInjectionTests`, `SettingsCatalogTests`.

**Weak and flaky**
- Asserts that cannot fail (only `NotNull`, comparing a value with itself, a mock returning what it was told), tests
  without an assert, `Assert.Throws<Exception>`.
- Fixed waits (`Task.Delay` with a number, `Thread.Sleep`), real clocks instead of `TimeProvider`, tests that depend on
  culture, order or the machine (see `.claude/rules/tests.md`).
- The ten slowest tests with their time (report options of the test runner, `--help`), and whether each needs to be slow.

**Redundant – what can be merged or removed**
- Tests that repeat the same arrange and assert with one changed value → one `[Theory]`.
- Tests that check the same failure as another test on the same or a lower layer (a UI.Tests case that only repeats a Core
  test, two tests of one guard with equivalent input).
- Tests of the language or a library (record equality, property setters, `System.Text.Json` defaults) and tests of code
  that no longer exists or no longer decides anything.
- For every proposal: which test still catches the same failure after the change. Never propose removing a test that pins
  a review finding or a bug fix, or an invariant test, unless another test covers exactly that failure.
- Check the tests added since the last review against the section "Redundante Tests" in `.claude/rules/tests.md`.

**Structure**
- Tests in the folder of the area they test, one test class per file, names that state the behaviour.

- Leave to: RE (duplicate production code).

## DO – Docs

- `README.md` matches the app (features, screenshots, requirements).
- `CLAUDE.md`: still true, still only what always applies; estimate its size in tokens and name what could move into a
  rule.
- `.claude/rules/`: every `paths` glob matches files, no rule without `paths`, size per rule, contradictions between rules,
  traps that are fixed and no longer need a rule.
- `docs/architecture.md`, ADRs (numbering without gaps, status), work package protocols and index (release versions),
  roadmap and backlog (done items still listed, duplicates).
- All relative Markdown links resolve; CHANGELOG sections match the tags.
- No names or structure from the user's customer database anywhere.
- Leave to: RE (code comments).
