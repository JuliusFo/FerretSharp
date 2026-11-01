---
name: ship-wp
description: Implement a work package from the roadmap end to end, from plan to green CI
disable-model-invocation: true
---
1. Read the WP from the roadmap and write the plan. Wait for approval.
2. Implement core, UI, tests and docs.
3. Run E2E in a scratch instance (`docs/e2e-testing.md`): CDP for state, real Windows clicks via `tools/e2e/Invoke-RealClick.ps1` for mouse interactions. Run `tools/e2e/Test-InjectedInput.ps1` first; if FACEIT filters injected input, ask the user to click and read the effect via CDP.
4. Push, open the PR and enable auto-fix.
5. Run `/branch-code-review` (separate agent, findings by severity, wait for approval, fix, second pass).
6. Push and watch CI to green.
7. Remove the test DB container this session created, by its name (`docker rm -f <name>`) – never `ferret-sample` or another session's container. Ask first if more E2E may follow.
