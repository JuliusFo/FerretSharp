---
name: ship-wp
description: Implement a work package from the roadmap end to end, from plan to green CI
disable-model-invocation: true
---
1. Read the WP from the roadmap and write the plan. Wait for approval.
2. Implement core, UI, tests and docs.
3. Run E2E in a scratch instance via CDP.
4. Push, open the PR and enable auto-fix.
5. Spawn a separate review agent and fix all findings.
6. Push and watch CI to green.
