---
name: release
description: Prepare and publish a FerretSharp release
disable-model-invocation: true
---
1. Summarize unreleased commits since the last tag. Propose the version number and wait for my OK.
2. Bump the version and update CHANGELOG.md, keeping its line endings (no BOM).
   Group the new version's entries in the sections and order `docs/release.md` gives (Security, Added, Changed, Fixed,
   Internal; the main topic may go first). Leave out empty sections. Run `git diff --stat` to verify.
3. Build and run the full test suite.
4. Create the PR using --body-file. Run `gh pr checks --watch` until green. Do NOT use --auto.
5. Merge, pull main, tag vX.Y.Z and push the tag.
6. Watch the release workflow until it is published, then delete merged branches and worktrees (show the list first).
7. Count the work packages in `docs/work-packages/README.md` released since the last `docs/review-*.md` and name those whose protocol has no code review. Five or more packages, or two or more without a review: suggest `/complete-code-review` in the final message.
