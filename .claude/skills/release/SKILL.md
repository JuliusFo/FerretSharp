---
name: release
description: Prepare and publish a FerretSharp release
disable-model-invocation: true
---
1. Pull `main` first (other sessions merge meanwhile; 3.70.0 missed WP-41 that way). Summarize unreleased commits since the
   last tag. Propose the version number and wait for my OK.
2. Bump the version and update CHANGELOG.md, keeping its line endings (no BOM).
   Group the new version's entries in the sections and order `docs/release.md` gives (Security, Added, Changed, Fixed,
   Internal; the main topic may go first). Leave out empty sections. In `docs/work-packages/README.md` set the release
   column of every package in it, keep the rows in release order and extend the package range of its version line; in
   each of their protocols replace "noch ohne Release" in the head (review 3.70.0, D1). Run `git diff --stat` to verify.
3. Build and run the full test suite.
4. Create the PR using --body-file. Run `gh pr checks --watch` until green. Do NOT use --auto.
5. Merge, pull main, tag vX.Y.Z and push the tag.
6. Watch the release workflow until it is published, then delete merged branches and worktrees (show the list first).
7. Count the work packages (`WP-NN` rows only; review follow-ups `R…` and `Fixes` rows do not count) in
   `docs/work-packages/README.md` released since the last `docs/review-*.md` and name those whose protocol has no code
   review. Five or more packages, or two or more without a review: suggest `/complete-code-review` in the final message.
