---
description: Create an isolated git worktree for a GitHub issue with deterministic per-worktree ports + bootstrapped dev stack
argument-hint: <number>[,<number>...] [iterm|terminal|konsole]
---

Create isolated worktrees for the issue numbers in `$ARGUMENTS`. Accepts a single issue (`42` or `#42`), several comma- or space-separated issues (`42,43,44` or `42 43 44`), and an optional trailing terminal keyword (`iterm`, `terminal`, or `konsole`).

This wraps `scripts/new-worktree.sh`. Each worktree is created at `.worktrees/issue-<n>/` (gitignored) with its own dev stack (postgres + minio + .NET API + Vite) on offset ports derived from the issue number, plus a generated `.env.worktree.local`. The stack is fully bootstrapped (compose up + migrate + seed) so the user can `cd` in and `make dev-all` immediately. If the `code` CLI is available, a new VS Code window opens on each worktree automatically.

## Steps

1. **Parse `$ARGUMENTS`.**
   - Split on commas and/or whitespace.
   - If a terminal keyword (`iterm`, `terminal`, or `konsole`, case-insensitive) is present, remove it and remember `TERMINAL=<keyword>` (lowercased). At most one terminal keyword may be specified — abort if more than one is found.
   - For each remaining token, strip a leading `#`. If what remains is not a positive integer, abort with `usage: /worktree <number>[,<number>...] [iterm|terminal|konsole]`.
   - Deduplicate while preserving order. Result is the issue list.

2. **Preconditions:**
   - `gh auth status` must succeed (the script calls `gh issue view`).
   - `docker info` must succeed (the script runs `make up`).
   - If `TERMINAL` is set, sanity-check the platform: `iterm` and `terminal` require macOS (`uname` == `Darwin`); `konsole` requires `konsole` on `$PATH`. If the check fails, abort with a clear message — don't fall back silently.

3. **Run `./scripts/new-worktree.sh <n>` sequentially for each issue.** Don't parallelize — the bootstrap touches shared docker state (image pulls, migrations) and concurrent runs can collide. Stream output so the user sees progress. If any one fails, stop and report which succeeded.

4. **On success per issue**, capture the summary line (`postgres :…   minio :…/…   api :…   web :…`) and the next-step command for that worktree.

5. **After all worktrees are created:**
   - Print a combined summary: one line per worktree with its ports.
   - If `TERMINAL` is set, invoke the `worktree-open` skill with the issue list and the chosen terminal. The skill handles the per-terminal layout (iTerm2: 3 tabs × N side-by-side panes; Terminal.app: 3 windows × N tabs; Konsole: 3 windows × N tabs — one window/tab group per role: `claude`, `server`, `web`).
   - Otherwise, print the manual next-step commands (`cd .worktrees/issue-<n> && make dev-all`) for each worktree.

6. **On failure**, surface stderr and stop. Common failures:
   - Worktree directory already exists → tell the user to run `/worktree-remove <n>` first.
   - Port already in use → another stack is on the same offset; suggest tearing it down or using a different issue number.
   - `gh` not authenticated → tell the user to `gh auth login`.

Do not start `make dev-all` yourself — that runs in the foreground and blocks. The terminal panes/tabs (when a terminal keyword is requested) cover this; otherwise leave it for the user.

## Example invocations

- `/worktree 900` — single worktree, no terminal opened.
- `/worktree 900,901,902` — three worktrees, no terminal opened.
- `/worktree 900,901,902 iterm` — three worktrees, then opens 3 iTerm tabs × 3 panes (claude/server/web) on macOS.
- `/worktree 900 terminal` — single worktree, then opens 3 Terminal.app windows (one per role) on macOS.
- `/worktree 900,901 konsole` — two worktrees, then opens 3 Konsole windows × 2 tabs (one per role) on Linux.
