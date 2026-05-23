---
description: Create an isolated git worktree for a GitHub issue with deterministic per-worktree ports + bootstrapped dev stack
argument-hint: <number>[,<number>...] [iterm]
---

Create isolated worktrees for the issue numbers in `$ARGUMENTS`. Accepts a single issue (`42` or `#42`), several comma- or space-separated issues (`42,43,44` or `42 43 44`), and an optional trailing `iterm` keyword.

This wraps `scripts/new-worktree.sh`. Each worktree is created at `.worktrees/issue-<n>/` (gitignored) with its own dev stack (postgres + minio + .NET API + Vite) on offset ports derived from the issue number, plus a generated `.env.worktree.local`. The stack is fully bootstrapped (compose up + migrate + seed) so the user can `cd` in and `make dev-all` immediately. If the `code` CLI is available, a new VS Code window opens on each worktree automatically.

## Steps

1. **Parse `$ARGUMENTS`.**
   - Split on commas and/or whitespace.
   - If the literal token `iterm` (case-insensitive) is present, remove it and remember `OPEN_ITERM=true`.
   - For each remaining token, strip a leading `#`. If what remains is not a positive integer, abort with `usage: /worktree <number>[,<number>...] [iterm]`.
   - Deduplicate while preserving order. Result is the issue list.

2. **Preconditions:**
   - `gh auth status` must succeed (the script calls `gh issue view`).
   - `docker info` must succeed (the script runs `make up`).
   - If `OPEN_ITERM=true`, additionally verify iTerm2 is installed (`osascript -e 'tell application "System Events" to exists application process "iTerm2"'` or just trust the user and let the AppleScript fail loudly).

3. **Run `./scripts/new-worktree.sh <n>` sequentially for each issue.** Don't parallelize — the bootstrap touches shared docker state (image pulls, migrations) and concurrent runs can collide. Stream output so the user sees progress. If any one fails, stop and report which succeeded.

4. **On success per issue**, capture the summary line (`postgres :…   minio :…/…   api :…   web :…`) and the next-step command for that worktree.

5. **After all worktrees are created:**
   - Print a combined summary: one line per worktree with its ports.
   - If `OPEN_ITERM=true`, invoke the `gankedtv-worktree-tabs` skill with the issue list. The skill opens one iTerm tab per worktree, each split into four side-by-side panes (`make web`, `make server`, `claude`, `make up`).
   - Otherwise, print the manual next-step commands (`cd .worktrees/issue-<n> && make dev-all`) for each worktree.

6. **On failure**, surface stderr and stop. Common failures:
   - Worktree directory already exists → tell the user to run `/worktree-remove <n>` first.
   - Port already in use → another stack is on the same offset; suggest tearing it down or using a different issue number.
   - `gh` not authenticated → tell the user to `gh auth login`.

Do not start `make dev-all` yourself — that runs in the foreground and blocks. The iTerm panes (when `iterm` is requested) cover this; otherwise leave it for the user.

## Example invocations

- `/worktree 900` — single worktree, no iTerm.
- `/worktree 900,901,902` — three worktrees, no iTerm.
- `/worktree 900,901,902 iterm` — three worktrees, then opens four-pane tabs in iTerm for each.
- `/worktree 900 iterm` — single worktree, then opens a four-pane tab.
